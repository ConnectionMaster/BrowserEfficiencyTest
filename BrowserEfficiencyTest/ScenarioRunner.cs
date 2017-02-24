﻿//--------------------------------------------------------------
//
// Browser Efficiency Test
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files(the ""Software""),
// to deal in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included
// in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE AUTHORS
// OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF
// OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//--------------------------------------------------------------

using Elevator;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrowserEfficiencyTest
{
    /// <summary>
    /// Executes automated scenarios on selected web browsers using WebDriver
    /// </summary>
    internal class ScenarioRunner
    {
        private ResponsivenessTimer _timer;
        private bool _useTimer;
        private bool _doWarmup;
        private int _iterations;
        private int _maxAttempts;
        private string _browserProfilePath;
        private bool _usingTraceController;
        private string _etlPath;
        private bool _overrideTimeout;
        private List<WorkloadScenario> _scenarios = new List<WorkloadScenario>();
        private List<string> _browsers = new List<string>();
        private CredentialManager _logins;
        private string _scenarioName;
        private int _e3RefreshDelaySeconds;

        // _measureSets format: Dictionary< "measure set name", Tuple < "WPR profile name", "tracing mode" >>
        private Dictionary<string, Tuple<string, string>> _measureSets;

        /// <summary>
        /// Instantiates a ScenarioRunner with the passed in arguments
        /// </summary>
        /// <param name="args"></param>
        public ScenarioRunner(Arguments args)
        {
            _e3RefreshDelaySeconds = 12;
            _doWarmup = args.DoWarmup;
            _iterations = args.Iterations;
            _browserProfilePath = args.BrowserProfilePath;
            _usingTraceController = args.UsingTraceController;
            _etlPath = args.EtlPath;
            _maxAttempts = args.MaxAttempts;
            _overrideTimeout = args.OverrideTimeout;
            _scenarios = args.Scenarios.ToList();
            _browsers = args.Browsers.ToList();
            _scenarioName = args.ScenarioName;
            _measureSets = GetMeasureSetInfo(args.SelectedMeasureSets.ToList());
            _logins = new CredentialManager(args.CredentialPath);
            _timer = new ResponsivenessTimer();

            if (args.MeasureResponsiveness)
            {
                _useTimer = true;
            }
            else
            {
                _useTimer = false;
            }
        }

        // Creates a data structure of measure sets name, wprp file and tracing mode and creates an empty one
        // if there are no measure sets selected (user isn't doing any tracing). This helps to simplify the 
        // logic needed to allow both the ability to cycle through measure sets if the user selected any as
        // well as not use any measure sets if none were selected. The alternative would be to use the full
        // measureset objects, which would require either creating a dummy/empty measure set objects or adding
        // multiple checks throughout the main pass loop checking to see if measure sets were enabled or not.
        private Dictionary<string, Tuple<string, string>> GetMeasureSetInfo(List<MeasureSet> measureSets)
        {
            // _measureSets format: Dictionary< "measure set name", Tuple < "WPR profile name", "tracing mode" >>
            Dictionary<string, Tuple<string, string>> measureSetInfo = new Dictionary<string, Tuple<string, string>>();

            if (measureSets == null || measureSets.Count == 0)
            {
                // No measure sets selected so create a single empty value to use as a dummy measure set.
                measureSetInfo.Add("None", new Tuple<string, string>("", ""));
            }
            else
            {
                // Create a data structure containing the name, WPR profile name and tracing mode of all selected measure sets.
                var msInfo = from m in measureSets
                             select new KeyValuePair<string, Tuple<string, string>>(m.Name, new Tuple<string, string>(m.WprProfile, m.TracingMode.ToString()));

                // Format msInfo to a Dictionary of <string, Tuple<string, string>>
                measureSetInfo = msInfo.ToDictionary(k => k.Key, v => v.Value);
            }

            return measureSetInfo;
        }

        /// <summary>
        /// Runs the test passes specified by the arguments passed in when the ScenarioRunner object was instantiated.
        /// </summary>
        public void Run()
        {
            if (_doWarmup)
            {
                RunWarmupPass();
            }

            if (_useTimer)
            {
                _timer.Enable();
            }

            RunMainLoop();
        }

        public List<string> GetResponsivenessResults()
        {
            return _timer.GetResults();
        }

        private void RunWarmupPass()
        {
            // A warmup pass is one run thru the selected scenarios and browsers.
            // It allows the browsers to cache some content which helps reduce variability from run to run.
            Logger.LogWriteLine("- Starting warmup pass -");

            foreach (string browser in _browsers)
            {
                using (var driver = RemoteWebDriverExtension.CreateDriverAndMaximize(browser, _browserProfilePath))
                {
                    foreach (var scenario in _scenarios)
                    {
                        Logger.LogWriteLine(string.Format("Warmup - Browser: {0}  Scenario: {1}", browser, scenario.Scenario.Name));
                        scenario.Scenario.Run(driver, browser, _logins, _timer);

                        Thread.Sleep(1 * 1000);
                    }
                    driver.Quit();
                }
            }
            Logger.LogWriteLine("- Completed warmup pass -");
        }

        /// <summary>
        /// The main loop of the class. This method will run through the specified number of iterations on all the
        /// specified browsers across all the specified scenarios.
        /// </summary>
        private void RunMainLoop()
        {
            if (_usingTraceController)
            {
                Logger.LogWriteLine("Pausing before starting first tracing session to reduce interference.");

                // E3 system aggregates energy data at regular intervals. For our test passes we use 10 second intervals. Waiting here for 12 seconds before continuing ensures
                // that the browser energy data reported by E3 going forward is from this test run and not from warmup or before running the test pass.
                Thread.Sleep(_e3RefreshDelaySeconds * 1000);
            }

            using (var elevatorClient = ElevatorClient.Create(_usingTraceController))
            {
                elevatorClient.ConnectAsync().Wait();
                elevatorClient.SendControllerMessageAsync($"{Elevator.Commands.START_PASS} {_etlPath}").Wait();

                Logger.LogWriteLine("- Starting Test Pass -");

                // Core Execution Loop
                // TODO: Consider breaking up this large loop into smaller methods to ease readability.
                for (int iteration = 0; iteration < _iterations; iteration++)
                {
                    _timer.SetIteration(iteration);
                    foreach (var currentMeasureSet in _measureSets)
                    {
                        _timer.SetMeasureSet(currentMeasureSet.Key);

                        // Randomize the order the browsers each iteration to reduce systematic bias in the test
                        Random rand = new Random();
                        _browsers = _browsers.OrderBy(a => rand.Next()).ToList<String>();

                        foreach (string browser in _browsers)
                        {
                            _timer.SetBrowser(browser);

                            bool passSucceeded = false;
                            for (int attemptNumber = 0; attemptNumber < _maxAttempts && !passSucceeded; attemptNumber++)
                            {
                                if (attemptNumber > 0)
                                {
                                    Logger.LogWriteLine("-- Attempting again...");
                                }

                                elevatorClient.SendControllerMessageAsync($"{Elevator.Commands.START_BROWSER} {browser} ITERATION {iteration} SCENARIO_NAME {_scenarioName} WPRPROFILE {currentMeasureSet.Value.Item1} MODE {currentMeasureSet.Value.Item2}").Wait();

                                Logger.LogWriteLine(string.Format("-- Launching Browser Driver {0} -", browser));

                                using (var driver = RemoteWebDriverExtension.CreateDriverAndMaximize(browser, _browserProfilePath))
                                {
                                    string currentScenario = "";
                                    try
                                    {
                                        Stopwatch watch = Stopwatch.StartNew();
                                        bool isFirstScenario = true;

                                        _timer.SetDriver(driver);

                                        foreach (var scenario in _scenarios)
                                        {
                                            currentScenario = scenario.ScenarioName;
                                            _timer.SetScenario(scenario.ScenarioName);

                                            // We want every scenario to take the same amount of time total, even if there are changes in
                                            // how long pages take to load. The biggest reason for this is so that you can measure energy
                                            // or power and their ratios will be the same either way.
                                            // So start by getting the current time.
                                            var startTime = watch.Elapsed;

                                            // The first scenario naviagates in the browser's new tab / welcome page.
                                            // After that, scenarios open in their own tabs
                                            if (!isFirstScenario && scenario.Tab == "new")
                                            {
                                                driver.CreateNewTab(browser);
                                            }
                                            else
                                            {
                                                isFirstScenario = false;
                                            }

                                            Logger.LogWriteLine(string.Format("-- Executing - Iteration: {0}  Attempt: {1}  Browser: {2}  Scenario: {3}  MeasureSet: {4}.", iteration, attemptNumber, browser, scenario.Scenario.Name, currentMeasureSet.Key));

                                            // Here, control is handed to the scenario to navigate, and do whatever it wants
                                            scenario.Scenario.Run(driver, browser, _logins, _timer);

                                            // When we get control back, we sleep for the remaining time for the scenario. This ensures
                                            // the total time for a scenario is always the same
                                            var runTime = watch.Elapsed.Subtract(startTime);
                                            var timeLeft = TimeSpan.FromSeconds(scenario.Duration).Subtract(runTime);
                                            if (timeLeft < TimeSpan.FromSeconds(0) && !_overrideTimeout)
                                            {
                                                // Of course it's possible we don't get control back until after we were supposed to
                                                // continue to the next scenario. In that case, invalidate the run by throwing.
                                                Logger.LogWriteLine(string.Format("-- !!! Scenario {0} ran longer than expected! The browser ran for {1}s. The timeout for this scenario is {2}s.", scenario.Scenario.Name, runTime.TotalSeconds, scenario.Duration));
                                                throw new Exception(string.Format("Scenario {0} ran longer than expected! The browser ran for {1}s. The timeout for this scenario is {2}s.", scenario.Scenario.Name, runTime.TotalSeconds, scenario.Duration));
                                            }
                                            else if (!_overrideTimeout)
                                            {
                                                Logger.LogWriteLine(string.Format("-- Scenario {0} returned in {1} seconds. Now sleeping for remaining time of {2} seconds.", scenario.Scenario.Name, runTime.TotalSeconds, timeLeft.TotalSeconds));
                                                Thread.Sleep(timeLeft);
                                            }

                                            Logger.LogWriteLine(string.Format("-- Completed - Scenario: {0} for Iteration: {1}  Attempt: {2}  Browser: {3}  MeasureSet: {4}. Scenario ran for {5} seconds.", scenario.Scenario.Name, iteration, attemptNumber, browser, currentMeasureSet.Key, runTime.TotalSeconds));
                                        }

                                        driver.CloseAllTabs(browser);
                                        passSucceeded = true;
                                        Logger.LogWriteLine(string.Format("SUCCESS! - Completed Browser: {0}  Iteration: {1}  Attempt: {2}  MeasureSet: {3}", browser, iteration, attemptNumber, currentMeasureSet.Key));
                                    }
                                    catch (Exception ex)
                                    {
                                        // If something goes wrong and we get an exception halfway through the scenario, we clean up
                                        // and put everything back into a state where we can start the next iteration.
                                        elevatorClient.SendControllerMessageAsync(Elevator.Commands.CANCEL_PASS);
                                        driver.CloseAllTabs(browser);
                                        Logger.LogWriteLine("------ EXCEPTION caught while trying to run scenario! ------------------------------------");
                                        Logger.LogWriteLine(string.Format("--- Iteration:   {0}", iteration));
                                        Logger.LogWriteLine(string.Format("--- Measure Set: {0}", currentMeasureSet));
                                        Logger.LogWriteLine(string.Format("--- Browser:     {0}", browser));
                                        Logger.LogWriteLine(string.Format("--- Attempt:     {0}", attemptNumber));
                                        Logger.LogWriteLine(string.Format("--- Scenario:    {0}", currentScenario));
                                        Logger.LogWriteLine(string.Format("--- Exception:   " + ex.ToString()));

                                        if (_usingTraceController)
                                        {
                                            Logger.LogWriteLine("--- Trace has been discarded");
                                        }

                                        Logger.LogWriteLine("-------------------------------------------------------");
                                    }
                                    finally
                                    {
                                        if (_usingTraceController)
                                        {
                                            Logger.LogWriteLine("-- Pausing between tracing sessions to reduce interference.");

                                            // E3 system aggregates energy data at regular intervals. For our test passes we use 10 second intervals. Waiting here for 12 seconds before continuing ensures
                                            // that the browser energy data reported by E3 for this run is only for this run and does not bleed into any other runs.
                                            Thread.Sleep(_e3RefreshDelaySeconds * 1000);
                                        }
                                    }
                                }
                            }

                            if (passSucceeded)
                            {
                                elevatorClient.SendControllerMessageAsync($"{Elevator.Commands.END_BROWSER} {browser}").Wait();
                            }
                        }
                    }
                }
                Logger.LogWriteLine("- Ending Test Pass -");
                elevatorClient.SendControllerMessageAsync(Elevator.Commands.END_PASS).Wait();
            }
        }
    }
}