using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
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
        static partial void RunProtocolAndPackagingTests()
        {
            Test("Old copy cannot consume a new copy's backup for a missing verb", () => RegistrySandbox((classes, backup) =>
            {
                string other = Path.Combine(Root, "new-install.exe"); File.Copy(Exe, other, true);
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, other, classes, backup, false);
                Registry.CurrentUser.DeleteSubKeyTree(classes + @"\Folder\shell\explore", false);
                ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false);
                using var saved = Registry.CurrentUser.OpenSubKey(backup + @"\FolderExploreCmd");
                Check(saved != null && Convert.ToInt32(saved.GetValue("Captured", 0)) == 1);
            }));
            Test("Unchecked upgrade choices remove only the selected integrations", () => RegistrySandbox((classes, backup) =>
            {
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                ShellIntegrationService.SetContextMenuEnabledCore(true, Exe, classes, false);
                LeafTab.Setup.SetupEngine.RemoveDisabledAssociations(Exe, true, false, classes, backup);
                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes) && !ShellIntegrationService.IsContextMenuEnabledCore(classes));
                ShellIntegrationService.SetContextMenuEnabledCore(true, Exe, classes, false);
                LeafTab.Setup.SetupEngine.RemoveDisabledAssociations(Exe, false, true, classes, backup);
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes) && ShellIntegrationService.IsContextMenuEnabledCore(classes));
            }));
            Test("Old uninstall preserves another installation's shared configuration", () =>
            {
                string old = MakeDirectory("old-uninstall"), shared = MakeDirectory("shared-config");
                File.WriteAllText(Path.Combine(old, "config.json"), "old");
                File.WriteAllText(Path.Combine(shared, "config.json"), "new");
                LeafTab.Setup.SetupEngine.RemoveKnownUserData(old, shared, false);
                Check(!File.Exists(Path.Combine(old, "config.json")) && File.ReadAllText(Path.Combine(shared, "config.json")) == "new");
            });
            Test("Path repair never replaces another manager's explicit command", () => RegistrySandbox((classes, backup) =>
            {
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                string path = classes + @"\Folder\shell\open\command";
                using (var key = Registry.CurrentUser.OpenSubKey(path, true)) { key!.SetValue("", "other-manager"); key.SetValue("DelegateExecute", "{OTHER}"); }
                ShellIntegrationService.RepairRegisteredPathsIfMovedCore(Exe, classes, backup, false);
                Check(Read(path) == "other-manager" && Read(path, "DelegateExecute") == "{OTHER}");
            }));
            Test("IPC round trip preserves virtual folder PIDL and selection", () =>
            {
                IntPtr pidl = PidlManager.CreateThisPcPidl();
                try
                {
                    byte[] bytes = PidlManager.ToBytes(pidl);
                    var request = new LaunchActionRequest { OpenInNewWindow = true };
                    request.Targets.Add(new LaunchTargetItem("此电脑", "item.txt", bytes));
                    var decoded = LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(request))!;
                    Check(decoded.OpenInNewWindow && decoded.Targets[0].SelectItemName == "item.txt" && decoded.Targets[0].PidlBytes!.SequenceEqual(bytes));
                }
                finally { PidlManager.Free(ref pidl); }
            });
            Test("Path repair preserves an edited COM server", () => RegistrySandbox((classes, backup) =>
            {
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                string path = classes + @"\CLSID\{8E4C7A29-3D51-4F82-9B14-6A5E20C91D73}\LocalServer32";
                using (var key = Registry.CurrentUser.OpenSubKey(path, true)) key!.SetValue("", "other-server");
                ShellIntegrationService.RepairRegisteredPathsIfMovedCore(Exe, classes, backup, false);
                Check(Read(path) == "other-server");
            }));
            Test("Malformed PIDLs are rejected before native allocation", () =>
            {
                foreach (var bytes in new[] { new byte[] { 2, 0 }, new byte[] { 255, 255, 0, 0 }, new byte[] { 0, 0, 1 } })
                    Check(!PidlManager.IsValidPidlBytes(bytes) && PidlManager.FromBytes(bytes) == IntPtr.Zero);
            });
            Test("IPC rejects oversized data", () =>
            {
                bool rejected = false;
                try { LaunchRequestCodec.ReadBoundedAsync(new MemoryStream(new byte[LaunchRequestCodec.MaxPayloadBytes + 1]), 1000, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            });
            Test("IPC imposes a deadline even on a noncooperating stream", () =>
            {
                bool timedOut = false;
                using var stream = new StalledStream();
                try { LaunchRequestCodec.ReadBoundedAsync(stream, 50, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (TimeoutException) { timedOut = true; }
                Check(timedOut);
            });
            Test("IPC retains compatibility with three-field target frames", () =>
            {
                string frame = "LEAFTAB_IPC_V1\nTARGET|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Root)) + "|\n";
                Check(LaunchRequestCodec.Decode(Encoding.UTF8.GetBytes(frame))!.Targets.Single().FolderOrAlias == Root);
            });
            Test("Pipe access is restricted to the current user", () =>
            {
                using var pipe = new NamedPipeServerStream("LeafTabRegression." + Guid.NewGuid().ToString("N"), PipeDirection.In,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, SingleInstanceService.CreateCurrentUserPipeSecurity());
                var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Check(rules.Length == 1 && rules[0].IdentityReference.Equals(WindowsIdentity.GetCurrent().User) && rules[0].AccessControlType == AccessControlType.Allow);
            });
            Test("Installer detects child timeout", () =>
            {
                var method = typeof(LeafTab.Setup.SetupOptions).Assembly.GetType("LeafTab.Setup.SetupEngine")!.GetMethod("RunProcessHiddenAndWait", BindingFlags.NonPublic | BindingFlags.Static)!;
                bool rejected = false;
                try { method.Invoke(null, new object[] { Assembly.GetExecutingAssembly().Location, "--child-slow", 50 }); }
                catch (TargetInvocationException ex) { rejected = ex.InnerException is TimeoutException; }
                Check(rejected);
            });
            Test("Installer payload matches this build and preserves old executable", () =>
            {
                var setup = typeof(LeafTab.Setup.SetupOptions).Assembly;
                string destination = Path.Combine(MakeDirectory("payload"), "LeafTab.exe");
                byte[] old = { 77, 90, 1, 2, 3, 4 };
                File.WriteAllBytes(destination, old);
                setup.GetType("LeafTab.Setup.SetupEngine")!.GetMethod("ExtractEmbeddedPayload", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { destination });
                Check(File.ReadAllBytes(destination + ".previous").SequenceEqual(old));
                using var sha = SHA256.Create();
                Check(sha.ComputeHash(File.ReadAllBytes(destination)).SequenceEqual(sha.ComputeHash(File.ReadAllBytes(Exe))));
            });
            Test("Failed executable replacement preserves the usable old file", () =>
            {
                string path = Path.Combine(MakeDirectory("locked-payload"), "LeafTab.exe");
                byte[] old = { 77, 90, 9, 8, 7 }; File.WriteAllBytes(path, old);
                bool rejected = false;
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { typeof(LeafTab.Setup.SetupOptions).Assembly.GetType("LeafTab.Setup.SetupEngine")!.GetMethod("ExtractEmbeddedPayload", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { path }); }
                    catch (TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
                    Check(rejected && File.ReadAllBytes(path).SequenceEqual(old));
                }
            });
        }

        private sealed class StalledStream : MemoryStream
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => new TaskCompletionSource<int>().Task;
        }
    }
}
