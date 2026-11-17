using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LeafTab.Shared
{
    // Shared with Setup so upgrading an old executable never depends on its
    // command-line support, and a path-scoped uninstall cannot quit another copy.
    internal sealed class ProcessLifetimeSignals : IDisposable
    {
        private readonly string _identityHash;
        private readonly List<EventWaitHandle> _events = new List<EventWaitHandle>();
        private readonly List<RegisteredWaitHandle> _waits = new List<RegisteredWaitHandle>();
        private volatile bool _disposed;

        internal ProcessLifetimeSignals(string? userKey = null)
        {
            _identityHash = Hash(userKey ?? WindowsIdentity.GetCurrent().User!.Value + ":" + Process.GetCurrentProcess().SessionId);
        }

        internal static string CurrentUserMediumIntegritySddl() =>
            $"D:P(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})S:(ML;;NW;;;ME)";

        private string EventName(string? path) => "Local\\LeafTab.Exit." + _identityHash +
            (path == null ? string.Empty : "." + Hash(Path.GetFullPath(path)));

        internal static Mutex CreateCurrentUserMutex(string name)
        {
            using SafeWaitHandle native = CreateWaitHandle(name, isMutex: true);
            var mutex = new Mutex(false);
            mutex.SafeWaitHandle.Dispose();
            mutex.SafeWaitHandle = new SafeWaitHandle(native.DangerousGetHandle(), ownsHandle: true);
            native.SetHandleAsInvalid();
            return mutex;
        }

        private EventWaitHandle OpenEvent(string? path)
        {
            using SafeWaitHandle native = CreateWaitHandle(EventName(path), isMutex: false);
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
            signal.SafeWaitHandle.Dispose();
            signal.SafeWaitHandle = new SafeWaitHandle(native.DangerousGetHandle(), ownsHandle: true);
            native.SetHandleAsInvalid();
            return signal;
        }

        private static SafeWaitHandle CreateWaitHandle(string name, bool isMutex)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(CurrentUserMediumIntegritySddl(), 1, out IntPtr descriptor, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                // Limited rights also permit opening synchronization objects made
                // by older elevated versions without asking to modify their ACL.
                IntPtr handle = isMutex ? CreateMutexExW(ref attributes, name, 0, 0x00100001)
                    : CreateEventExW(ref attributes, name, 1, 0x00100002);
                if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return new SafeWaitHandle(handle, ownsHandle: true);
            }
            finally { LocalFree(descriptor); }
        }

        internal void StartListening(string executablePath, Action onExit)
        {
            if (_events.Count != 0 || _disposed) return;
            foreach (string? scope in new string?[] { null, executablePath })
            {
                try
                {
                    EventWaitHandle signal = OpenEvent(scope);
                    _events.Add(signal);
                    _waits.Add(ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
                    {
                        if (!timedOut && !_disposed) onExit();
                    }, null, Timeout.Infinite, executeOnlyOnce: true));
                }
                catch (Exception ex) { DiagnosticLog.Write("exit-listener-unavailable", ex); }
            }
        }

        internal bool SignalExit(string? executablePath = null)
        {
            try
            {
                using EventWaitHandle signal = OpenEvent(executablePath);
                return signal.Set();
            }
            catch (Exception ex) { DiagnosticLog.Write("exit-signal-unavailable", ex); return false; }
        }

        internal bool TrySendQuitToPrimary(string? expectedExecutablePath, int timeoutMs = 1500)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "LeafTab.SingleInstance.Pipe." + _identityHash,
                    PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(timeoutMs);
                if (expectedExecutablePath != null &&
                    (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid) ||
                     !ShellAssociationRecovery.PathsEqual(TryGetProcessImagePath(pid), expectedExecutablePath))) return false;
                byte[] message = Encoding.UTF8.GetBytes("LEAFTAB_IPC_V1\nQUIT_PROCESS\n");
                return client.WriteAsync(message, 0, message.Length).Wait(timeoutMs);
            }
            catch (Exception ex) { DiagnosticLog.Write("quit-forward-unavailable", ex); return false; }
        }

        internal static string? TryGetProcessImagePath(uint pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(32768);
                int length = path.Capacity;
                return QueryFullProcessImageNameW(process, 0, path, ref length) ? path.ToString() : null;
            }
            finally { CloseHandle(process); }
        }

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
            return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (RegisteredWaitHandle wait in _waits) wait.Unregister(null);
            _waits.Clear();
            foreach (EventWaitHandle signal in _events) signal.Dispose();
            _events.Clear();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
