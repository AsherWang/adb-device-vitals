using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    internal static class WindowsDataUsagePage
    {
        public static Task<bool> OpenAsync(UsbTetherAdapter adapter, CancellationToken token) => Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true })?.Dispose();
            // Network & internet exposes a separate Data Usage action for each adapter.
            // Only invoke the action identified by the phone's actual interface GUID.
            var button = new AndCondition(ById(adapter.Id.ToString("B").ToUpperInvariant() + "_UsageButton"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var combo = ById("SystemSettings_DataUsage_AppList_Page_ComboBox");
            var selected = new AndCondition(ById("ContentPresenter"),
                new PropertyCondition(AutomationElement.NameProperty, adapter.Name));
            bool invoked = false;
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(18))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var window = FindSettingsWindow();
                    if (window != null && !invoked)
                    {
                        var action = window.FindFirst(TreeScope.Descendants, button);
                        if (action != null && action.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            invoked = true;
                            elapsed.Restart();
                        }
                    }
                    else if (window != null)
                    {
                        foreach (AutomationElement selector in window.FindAll(TreeScope.Descendants, combo))
                            if (!selector.Current.IsOffscreen && selector.FindFirst(TreeScope.Descendants, selected) != null)
                                return true;
                    }
                }
                catch (ElementNotAvailableException) { } // Settings replaces elements while navigating.
                catch (COMException) { }
                await Task.Delay(350, token).ConfigureAwait(false);
            }
            return false;
        }, token);

        private static PropertyCondition ById(string id) => new(AutomationElement.AutomationIdProperty, id);

        private static AutomationElement FindSettingsWindow()
        {
            // Windows 11 may host Settings inside ApplicationFrameHost. Activation
            // and foreground focus need not change at the same time.
            foreach (var process in Process.GetProcessesByName("SystemSettings"))
            {
                using (process)
                {
                    var pid = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
                    var desktop = AutomationElement.RootElement;
                    var direct = desktop.FindFirst(TreeScope.Children, pid);
                    if (direct != null) return direct;
                    var frames = desktop.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"));
                    foreach (AutomationElement frame in frames)
                    {
                        if (frame.FindFirst(TreeScope.Children, pid) != null) return frame;
                    }
                }
            }
            return null;
        }
    }
}
