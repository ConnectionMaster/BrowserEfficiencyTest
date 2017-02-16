//--------------------------------------------------------------
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

using System.Collections.Generic;
using System.Threading;
using OpenQA.Selenium.Remote;
using OpenQA.Selenium;

namespace BrowserEfficiencyTest
{
    internal class GoogleSearch : Scenario
    {
        public GoogleSearch()
        {
            Name = "GoogleSearch";
            // Default time
        }

        public override void Run(RemoteWebDriver driver, string browser, CredentialManager credentialManager, ResponsivenessTimer timer)
        {
            driver.Navigate().GoToUrl("http://www.google.com");
            driver.WaitForPageLoad();
            driver.Wait(5);

            // Measure how long it takes for results to appear    
            timer.StartMeasureOnEnterKeyPressed("Search results visible");
            timer.EndMeasureWhenElementExists("Search results visible", "#ires");

            // Search for "Seattle" and hit enter
            driver.TypeIntoField(driver.FindElementByXPath("//*[@title='Search']"), "Seattle" + Keys.Enter);
            driver.Wait(5);

            timer.ExtractMeasures();

            // Simply yield control back to the main thread and look at results
        }
    }
}
