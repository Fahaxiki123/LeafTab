using System;
using System.Windows;

namespace LeafTab.Setup
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetupOptions opts = SetupEngine.ParseArguments(e.Args);

            if (opts.IsSilent)
            {
                if (opts.IsUninstall)
                {
                    var res = await SetupEngine.ExecuteUninstallAsync(opts);
                    if (res.Ok)
                    {
                        SetupEngine.ScheduleSelfDeleteIfRunningAsUninstaller(opts.InstallDir);
                    }
                    Environment.Exit(res.Ok ? 0 : 1);
                    return;
                }
                else
                {
                    var res = await SetupEngine.ExecuteInstallAsync(opts);
                    Environment.Exit(res.Ok ? 0 : 1);
                    return;
                }
            }

            var win = new SetupWindow(opts);
            win.Closed += (_, _) => Shutdown();
            win.Show();
        }
    }
}
