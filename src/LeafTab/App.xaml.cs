using System;
using System.IO;
using System.Threading;
using System.Windows;
using LeafTab.Diagnostics;
using LeafTab.Interop;
using LeafTab.Views;

namespace LeafTab
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string? selfTestOut = null;
            string? initialPath = null;

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (string.Equals(e.Args[i], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTestOut = (i + 1 < e.Args.Length)
                        ? e.Args[++i]
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_report.md");
                }
                else if (!e.Args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    initialPath = e.Args[i];
                }
            }

            bool isSelfTest = !string.IsNullOrEmpty(selfTestOut);

            DispatcherUnhandledException += (_, args) =>
            {
                if (isSelfTest && !string.IsNullOrEmpty(selfTestOut))
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(Path.GetFullPath(selfTestOut!));
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(selfTestOut!, "# Unhandled Exception\r\n" + args.Exception);
                    }
                    catch
                    {
                        // Ignore logging errors during shutdown
                    }

                    args.Handled = true;
                    Shutdown(1);
                    return;
                }

                MessageBox.Show(
                    $"LeafTab 遇到未处理异常：\n{args.Exception.Message}",
                    "LeafTab",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                args.Handled = true;
            };
            var mainWindow = new MainWindow(skipSessionPersistence: isSelfTest);
            MainWindow = mainWindow;
            mainWindow.Show();

            if (!string.IsNullOrEmpty(initialPath))
            {
                (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(initialPath!, CancellationToken.None);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        if (mainWindow.Tabs.Count > 1)
                        {
                            mainWindow.AddNewTab(pidl, activate: true);
                        }
                        else
                        {
                            mainWindow.SelectedTab?.BrowserHost.NavigateToPidl(pidl);
                        }
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
            }

            if (isSelfTest)
            {
                await SelfTestRunner.RunAndExitAsync(mainWindow, selfTestOut!);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            UiaGestureDetector.Shared.Dispose();
            base.OnExit(e);
        }
    }
}
