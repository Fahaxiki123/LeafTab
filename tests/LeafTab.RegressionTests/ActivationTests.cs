using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using LeafTab.Interop;
using LeafTab.Services;
using LeafTab.Views;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        static partial void RunActivationTests()
        {
            Test("COM infrastructure startup never becomes an activate-new-tab IPC request", () =>
            {
                foreach (string flag in new[] { "--com-server", "-Embedding", "/embedding", "--background" })
                {
                    var startup = LaunchRequestParser.Parse(new[] { flag });
                    byte[] frame = LaunchRequestCodec.Encode(startup);
                    Check(startup.IsPassiveStartup);
                    Check(!Encoding.UTF8.GetString(frame).Contains("ACTIVATE_NEW_TAB"));
                    Check(LaunchRequestCodec.Decode(frame) == null);
                }
                var explicitOpen = LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(LaunchRequestParser.Parse(Array.Empty<string>())));
                Check(explicitOpen != null && !explicitOpen.IsPassiveStartup && explicitOpen.Targets.Count == 0);
                var withTarget = LaunchRequestParser.Parse(new[] { "--com-server", Root });
                Check(!withTarget.IsPassiveStartup && LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(withTarget))!.Targets.Count == 1);
                var quit = new LaunchActionRequest { IsComServerLaunch=true, IsQuitProcess=true };
                Check(!quit.IsPassiveStartup && LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(quit))!.IsQuitProcess);
            });

            Test("Passive requests cannot show an unloaded or hidden window", () =>
            {
                var window = new MainWindow(skipSessionPersistence:true,deferInitialFallbackTab:true);
                try
                {
                    foreach (var req in new[] { new LaunchActionRequest { IsComServerLaunch=true }, new LaunchActionRequest { IsBackgroundWarmup=true } })
                    {
                        var handling = window.HandleExternalLaunchRequestAsync(req,isInitialLaunch:false);
                        Check(handling.IsCompleted && !window.IsVisible && window.Tabs.Count == 0);
                        handling.GetAwaiter().GetResult();
                    }
                }
                finally { window.PrepareForForceExit(); window.Close(); }
            });

            Test("Suppressing Shell error UI still opens folders while SW_HIDE remains hidden", () =>
            {
                foreach (bool newWindow in new[] { false, true })
                {
                    int routed = 0;
                    LaunchActionRequest? last = null;
                    var command = new LeafTabExecuteCommand(r => { routed++; last=r; }, newWindow);
                    command.SetParameters(Root);
                    command.SetShowWindow(NativeMethods.SW_SHOWNORMAL);
                    command.SetNoShowUI(true);
                    Check(command.Execute() == ShellConstants.S_OK && routed == 1,
                        "SetNoShowUI(true) must not discard a visible folder-open command.");
                    Check(last!.OpenInNewWindow == newWindow && last.Targets.Count == 1 && last.Targets[0].FolderOrAlias == Root);

                    command.SetNoShowUI(false);
                    Check(command.Execute() == ShellConstants.S_OK && routed == 2);
                    command.SetShowWindow(NativeMethods.SW_HIDE);
                    foreach (bool noErrorUi in new[] { false, true })
                    {
                        command.SetNoShowUI(noErrorUi);
                        Check(command.Execute() == ShellConstants.S_OK && routed == 2,
                            "An explicitly hidden invocation must not activate a folder window.");
                    }
                    command.SetShowWindow(NativeMethods.SW_SHOWNORMAL);
                    Check(command.Execute() == ShellConstants.S_OK && routed == 3);
                }
            });

            Test("Explorer launcher opens This PC even when Shell error UI is suppressed", () =>
            {
                foreach (bool newWindow in new[] { false, true })
                {
                    LaunchActionRequest? request = null;
                    var command = new LeafTabExecuteCommand(r => request = r, newWindow);
                    command.SetParameters("::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}");
                    command.SetShowWindow(NativeMethods.SW_SHOWNORMAL);
                    command.SetNoShowUI(true);
                    Check(command.Execute() == ShellConstants.S_OK && request != null,
                        "The taskbar File Explorer launch must reach window routing.");
                    Check(request!.OpenInNewWindow == newWindow && request.Targets.Count == 1 &&
                          request.Targets[0].FolderOrAlias == "此电脑");
                }
            });

            Test("A COM factory probe does not open a window but its later Execute still forwards", () =>
            {
                int routed=0,activity=0;
                var factory=new LeafTabExecuteClassFactory(_=>routed++,false,()=>activity++);
                Guid iid=typeof(IExecuteCommand).GUID;
                Check(factory.CreateInstance(IntPtr.Zero,ref iid,out IntPtr pointer) == ShellConstants.S_OK && pointer != IntPtr.Zero);
                try
                {
                    Check(routed == 0 && activity > 0);
                    var command=(IExecuteCommand)Marshal.GetObjectForIUnknown(pointer);
                    command.SetParameters(Root);
                    Check(command.Execute() == ShellConstants.S_OK && routed == 1 && activity > 1);
                }
                finally { Marshal.Release(pointer); }
            });

            Test("COM idle exit guard never shuts down visible windows or primary instances after handling an open", () =>
            {
                long under30s = 29L * Stopwatch.Frequency;
                long over30s = 31L * Stopwatch.Frequency;

                // Passive hidden relay: exits only after 30s with 0 visible windows
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: false, visibleWindowsCount: 0, hasHandledExternalOpen: false, idleStopwatchTicks: under30s));
                Check(LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: false, visibleWindowsCount: 0, hasHandledExternalOpen: false, idleStopwatchTicks: over30s));

                // Relay that promoted to local fallback window: must never auto-exit while window is visible
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: false, visibleWindowsCount: 1, hasHandledExternalOpen: true, idleStopwatchTicks: over30s));

                // Cold-start primary --com-server with passive probe only: exits after 30s while hidden
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: true, visibleWindowsCount: 0, hasHandledExternalOpen: false, idleStopwatchTicks: under30s));
                Check(LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: true, visibleWindowsCount: 0, hasHandledExternalOpen: false, idleStopwatchTicks: over30s));

                // Cold-start primary --com-server that opened a window (e.g. during EnsureHandle) or later entered Warm Standby: never auto-exits
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: true, visibleWindowsCount: 1, hasHandledExternalOpen: false, idleStopwatchTicks: over30s));
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: true, visibleWindowsCount: 1, hasHandledExternalOpen: true, idleStopwatchTicks: over30s));
                Check(!LeafTab.App.ShouldExitOnComIdle(isPrimaryInstance: true, visibleWindowsCount: 0, hasHandledExternalOpen: true, idleStopwatchTicks: over30s));
            });

            Test("Current-user named pipe security applies Medium mandatory label and accepts real kernel pipe connection", () =>
            {
                string pipeName = "LeafTab.TestPipe." + Guid.NewGuid().ToString("N");
                using NamedPipeServerStream server = SingleInstanceService.CreatePipeServerStream(pipeName);

                var rules = server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Check(rules.Length == 1 && rules[0].IdentityReference.Equals(WindowsIdentity.GetCurrent().User) && rules[0].AccessControlType == AccessControlType.Allow);

                string? labelSddl = NativeMethods.TryGetMandatoryLabelSddl(server.SafePipeHandle);
                Check(!string.IsNullOrWhiteSpace(labelSddl) &&
                      (labelSddl!.IndexOf("(ML;;NW;;;ME)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       labelSddl.IndexOf("(ML;;NW;;;S-1-16-8192)", StringComparison.OrdinalIgnoreCase) >= 0));

                Task waitConn = server.WaitForConnectionAsync();
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(1500);
                waitConn.GetAwaiter().GetResult();
                Check(client.IsConnected && server.IsConnected);

                Check(NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverPid) && serverPid == (uint)Process.GetCurrentProcess().Id);
                string? imagePath = NativeMethods.TryGetProcessImagePath(serverPid);
                Check(!string.IsNullOrWhiteSpace(imagePath));
            });
        }
    }
}
