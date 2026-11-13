using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
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

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"LeafTab 遇到未处理异常：\n{args.Exception.Message}",
                    "LeafTab",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                args.Handled = true;
            };

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
            var mainWindow = new MainWindow(skipSessionPersistence: isSelfTest);
            MainWindow = mainWindow;
            mainWindow.Show();

            if (!isSelfTest)
            {
                IntPtr mainHwnd = new WindowInteropHelper(mainWindow).Handle;
                if (mainHwnd != IntPtr.Zero && !NativeMethods.IsWindowVisible(mainHwnd))
                {
                    NativeMethods.ShowWindow(
                        mainHwnd,
                        mainWindow.WindowState == WindowState.Maximized ? 3 /* SW_SHOWMAXIMIZED */ : NativeMethods.SW_SHOW);
                }
            }

            if (!string.IsNullOrEmpty(initialPath))
            {
                (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(initialPath!, CancellationToken.None);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        mainWindow.SelectedTab?.BrowserHost.NavigateToPidl(pidl);
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
