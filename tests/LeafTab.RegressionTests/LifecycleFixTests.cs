using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;
using LeafTab.Services;
using Microsoft.Win32;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunLifecycleFixTests()
        {
            RunStartupAndTabMoveTests();
            Test("Unavailable instance mutex never claims primary ownership", () =>
            {
                using var service = new SingleInstanceService("denied-" + Guid.NewGuid().ToString("N"));
                string name = (string)typeof(SingleInstanceService).GetField("_mutexName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
                var security = new MutexSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Deny));
                using var held = new Mutex(false, name, out _, security);
                Check(!service.TryAcquirePrimaryInstance());
            });
            Test("Current-user mutex can be acquired and remains owned across services", () =>
            {
                string identity = "owner-" + Guid.NewGuid().ToString("N");
                using var first = new SingleInstanceService(identity);
                Check(first.TryAcquirePrimaryInstance());
                bool otherAcquired = Task.Run(() =>
                {
                    using var second = new SingleInstanceService(identity);
                    return second.TryAcquirePrimaryInstance();
                }).GetAwaiter().GetResult();
                Check(!otherAcquired);
            });
            Test("Scoped quit reaches every same-copy relay and preserves another copy", () =>
            {
                string identity = "exit-" + Guid.NewGuid().ToString("N");
                string pathA = Path.Combine(Root, "copy-a", "LeafTab.exe"), pathB = Path.Combine(Root, "copy-b", "LeafTab.exe");
                using var first = new SingleInstanceService(identity);
                using var relay = new SingleInstanceService(identity);
                using var other = new SingleInstanceService(identity);
                using var a = new ManualResetEventSlim();
                using var r = new ManualResetEventSlim();
                using var b = new ManualResetEventSlim();
                first.StartExitListening(pathA, a.Set);
                relay.StartExitListening(pathA, r.Set);
                other.StartExitListening(pathB, b.Set);
                Check(first.SignalExit(pathA));
                Check(a.Wait(2000) && r.Wait(2000), "Primary and relay must both hear scoped exit.");
                Check(!b.IsSet, "Scoped quit reached a different executable.");
                Check(first.SignalExit());
                Check(b.Wait(2000), "Explicit global quit must reach the other copy too.");
            });
            Test("Installer quit verifies the receiving executable before sending V1 payload", () =>
            {
                string identity = "setup-quit-" + Guid.NewGuid().ToString("N");
                using var names = new SingleInstanceService(identity);
                string pipe = (string)typeof(SingleInstanceService).GetField("_pipeName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(names)!;
                Type signalsType = typeof(LeafTab.Setup.SetupOptions).Assembly.GetType("LeafTab.Shared.ProcessLifetimeSignals")!;
                using var signals = (IDisposable)Activator.CreateInstance(signalsType,
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { identity }, null)!;
                MethodInfo send = signalsType.GetMethod("TrySendQuitToPrimary", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (bool matching in new[] { false, true })
                {
                    using var server = SingleInstanceService.CreatePipeServerStream(pipe);
                    Task connected = server.WaitForConnectionAsync();
                    Task<string> read = Task.Run(async () =>
                    {
                        await connected.ConfigureAwait(false);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        return await reader.ReadToEndAsync().ConfigureAwait(false);
                    });
                    string expected = matching ? Assembly.GetExecutingAssembly().Location : Exe;
                    bool sent = (bool)send.Invoke(signals, new object[] { expected, 1500 })!;
                    Check(sent == matching);
                    Check(read.Wait(3000), "Scoped quit probe did not finish.");
                    Check(read.Result == (matching ? "LEAFTAB_IPC_V1\nQUIT_PROCESS\n" : string.Empty));
                }
            });
            Test("Quit signals disappear when the old process handles are closed", () =>
            {
                string identity = "exit-restart-" + Guid.NewGuid().ToString("N");
                string path = Path.Combine(Root, "restart", "LeafTab.exe");
                using (var previous = new SingleInstanceService(identity))
                using (var exited = new ManualResetEventSlim())
                {
                    previous.StartExitListening(path, exited.Set);
                    Check(previous.SignalExit());
                    Check(exited.Wait(2000));
                }
                using var restarted = new SingleInstanceService(identity);
                using var unexpectedQuit = new ManualResetEventSlim();
                restarted.StartExitListening(path, unexpectedQuit.Set);
                Check(!unexpectedQuit.Wait(100), "A stale global quit reached the next launch.");
            });
            Test("A non-primary local fallback cannot enter warm standby", () =>
            {
                var fallback = (LeafTab.App)FormatterServices.GetUninitializedObject(typeof(LeafTab.App));
                Check(!fallback.ShouldWindowEnterWarmStandby(null!));
            });
            Test("Real Explorer-named folders retain their filesystem PIDLs", () =>
            {
                foreach (string name in new[] { "File Explorer", "Windows Explorer", "文件资源管理器", "Windows 资源管理器", "prefix-52205fd8-5dfb-447d-801a-d0b52f2e83e1" })
                {
                    string path = MakeDirectory("launcher-names\\" + name);
                    IntPtr pidl = PidlManager.TryParseLocalOrShellFast(path);
                    try
                    {
                        Check(pidl != IntPtr.Zero && !PidlManager.IsThisPcPidl(pidl), name);
                        Check(string.Equals(PidlManager.GetDisplayInfo(pidl).FileSystemPath, path, StringComparison.OrdinalIgnoreCase), name);
                    }
                    finally { PidlManager.Free(ref pidl); }
                }
                Check(PidlManager.IsExplorerLauncherTarget("shell:::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}"));
            });
            Test("UNC launcher-like names remain available to bounded asynchronous parsing", () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Check(!PidlManager.IsExplorerLauncherTarget(@"\\leaftab-offline-test.invalid\share\File Explorer.lnk"));
                Check(timer.ElapsedMilliseconds < 500, "Fast alias detection performed network I/O.");
            });
            Test("An arbitrary File Explorer shortcut is not the system launcher", () =>
            {
                string folder = MakeDirectory("shortcut-target");
                string path = Path.Combine(MakeDirectory("shortcut-probe"), "File Explorer.lnk");
                object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
                try
                {
                    var link = (IShellLinkW)instance;
                    Check(link.SetPath(folder) >= 0);
                    ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
                    Check(!PidlManager.IsExplorerLauncherTarget(path));
                    Check(link.SetPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) >= 0);
                    ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
                    Check(PidlManager.IsExplorerLauncherTarget(path));
                }
                finally { Marshal.ReleaseComObject(instance); }
            });
            Test("Shell command parameters preserve argv boundaries, new-window, and select semantics", () =>
            {
                string first = MakeDirectory("argv first"), second = MakeDirectory("argv second");
                LaunchActionRequest? received = null;
                var command = new LeafTabExecuteCommand(r => received = r, false);
                command.SetParameters("--new-window \"" + first + "\" \"" + second + "\"");
                Check(command.Execute() == 0 && received != null && received.OpenInNewWindow);
                Check(received!.Targets.Count == 2 && received.Targets[0].FolderOrAlias == first && received.Targets[1].FolderOrAlias == second);
                command.SetParameters("/e, \"" + first + "\"");
                Check(command.Execute() == 0 && received!.Targets[0].FolderOrAlias == first);
                string selected = Path.Combine(first, "a,b.txt");
                command.SetParameters("/select,\"" + selected + "\"");
                Check(command.Execute() == 0 && received!.Targets[0].FolderOrAlias == first && received.Targets[0].SelectItemName == "a,b.txt");
            });
            Test("Embedded multi-selection routes all LeafTab folders without hijacking dialogs", () =>
            {
                Check(LeafTabExecuteCommand.ShouldRouteEmbeddedSelectionAsTabs(true, 2));
                Check(!LeafTabExecuteCommand.ShouldRouteEmbeddedSelectionAsTabs(true, 1));
                Check(!LeafTabExecuteCommand.ShouldRouteEmbeddedSelectionAsTabs(false, 2));
            });
            Test("Default verb recovery preserves case-insensitive ownership and raw value kind", () => RegistrySandbox((classes, backup) =>
            {
                string shell = classes + @"\Directory\shell";
                using (var key = Registry.CurrentUser.CreateSubKey(shell)) key.SetValue("", "%LEAFTAB_TEST_VERB%", RegistryValueKind.ExpandString);
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false).Ok);
                using (var key = Registry.CurrentUser.OpenSubKey(shell, true)) key!.SetValue("", "leaftabopen");
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false).Ok);
                using var restored = Registry.CurrentUser.OpenSubKey(shell);
                Check(restored!.GetValueKind("") == RegistryValueKind.ExpandString);
                Check((string)restored.GetValue("", "", RegistryValueOptions.DoNotExpandEnvironmentNames) == "%LEAFTAB_TEST_VERB%");
            }));
        }
    }
}
