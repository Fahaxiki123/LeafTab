using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LeafTab.Interop;
using LeafTab.Shared;
using LeafTab.Views;

namespace LeafTab.Diagnostics
{
    // Opt-in observer for --self-test-ui. It never injects input, activates a
    // window, changes focus, or reads the contents of an input control.
    internal sealed class InteractiveFocusRecorder : IDisposable
    {
        private readonly MainWindow _window;
        private readonly StreamWriter _writer;
        private readonly DispatcherTimer _timer;
        private string? _lastState;
        private long _keySequence;
        private bool _disposed;

        internal static void Attach(MainWindow window, string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                _ = new InteractiveFocusRecorder(window, fullPath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("interactive-focus-recorder-open", ex);
            }
        }

        private InteractiveFocusRecorder(MainWindow window, string path)
        {
            _window = window;
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
            _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += TimerTick;
            _window.Closed += WindowClosed;
            ComponentDispatcher.ThreadPreprocessMessage += ObserveMessage;
            Capture("attached", force: true);
            if (!_disposed) _timer.Start();
        }

        private void TimerTick(object? sender, EventArgs e) => Capture("state", force: false);

        private void ObserveMessage(ref MSG message, ref bool handled)
        {
            if (_disposed ||
                (message.message != NativeMethods.WM_KEYDOWN && message.message != NativeMethods.WM_KEYUP &&
                 message.message != NativeMethods.WM_SYSKEYDOWN && message.message != NativeMethods.WM_SYSKEYUP))
                return;

            long key = message.wParam.ToInt64();
            if (key != 0x09 && key != 0x1B && (key < 0x25 || key > 0x28))
                return;

            IntPtr root = new WindowInteropHelper(_window).Handle;
            if (root == IntPtr.Zero || message.hwnd == IntPtr.Zero ||
                (message.hwnd != root && NativeMethods.GetAncestor(message.hwnd, NativeMethods.GA_ROOT) != root))
                return;

            long sequence = unchecked(++_keySequence);
            Capture("key-observed seq=" + sequence + " msg=" + message.message.ToString("X", CultureInfo.InvariantCulture) +
                " vk=" + key.ToString("X", CultureInfo.InvariantCulture) + " target=" + Hwnd(message.hwnd) +
                " handled=" + handled, force: true);

            // Do not capture the ref MSG or modify handled. This is only an
            // after-dispatch observation of the state produced by real input.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => Capture("key-after seq=" + sequence, force: true)));
        }

        private void Capture(string reason, bool force)
        {
            if (_disposed) return;
            try
            {
                var host = _window.SelectedTab?.BrowserHost;
                IntPtr root = new WindowInteropHelper(_window).Handle;
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                IntPtr nativeFocus = NativeMethods.GetFocus(); // Must run on the LeafTab UI thread.
                var previewColumn = _window.FindName("PreviewPaneColumn") as ColumnDefinition;
                var previewBorder = _window.FindName("PreviewSidebarBorder") as FrameworkElement;
                string[] selected = host?.GetSelectedPaths().Select(p => Safe(Path.GetFileName(p))).ToArray()
                    ?? Array.Empty<string>();
                string state = "active=" + _window.IsActive +
                    " visible=" + _window.IsVisible + " windowState=" + _window.WindowState +
                    " root=" + Hwnd(root) + " foreground=" + Hwnd(foreground) +
                    " ownsForeground=" + (root != IntPtr.Zero && root == foreground) +
                    " nativeFocus=" + Hwnd(nativeFocus) + " nativeClass=" + WindowClass(nativeFocus) +
                    " canReceive=" + (host?.CanReceiveKeyboardFocus ?? false) +
                    " nativeInside=" + (host?.IsNativeFocusInside() ?? false) +
                    " keyboard=" + Element(Keyboard.FocusedElement) +
                    " logical=" + Element(FocusManager.GetFocusedElement(_window)) +
                    " searchFocus=" + HasKeyboardFocus("SearchTextBox") +
                    " addressFocus=" + HasKeyboardFocus("AddressTextBox") +
                    " previewTextFocus=" + HasKeyboardFocus("PreviewTextBox") +
                    " previewDocumentFocus=" + HasKeyboardFocus("PreviewDocumentViewer") +
                    " selectedCount=" + selected.Length + " selected=" + string.Join("|", selected.Take(4)) +
                    " previewTitle=" + Safe(_window.CurrentPreviewTitle) +
                    " previewKind=" + _window.CurrentPreviewKindName +
                    " previewWidth=" + (previewColumn?.ActualWidth ?? 0).ToString("F2", CultureInfo.InvariantCulture) +
                    " previewBorderWidth=" + (previewBorder?.ActualWidth ?? 0).ToString("F2", CultureInfo.InvariantCulture);
                if (!force && string.Equals(state, _lastState, StringComparison.Ordinal)) return;
                _lastState = state;
                _writer.WriteLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + reason + " " + state);
            }
            catch (Exception ex)
            {
                // Diagnostics must not break keyboard processing or keep a failed
                // observer subscribed to the UI dispatcher indefinitely.
                DiagnosticLog.Write("interactive-focus-recorder-read", ex);
                Dispose();
            }
        }

        private bool HasKeyboardFocus(string name) =>
            (_window.FindName(name) as UIElement)?.IsKeyboardFocusWithin ?? false;

        private static string Element(IInputElement? element) => element == null ? "none" :
            Safe(element.GetType().Name + ":" + (element as FrameworkElement)?.Name);

        private static string Hwnd(IntPtr handle) =>
            "0x" + unchecked((ulong)handle.ToInt64()).ToString("X", CultureInfo.InvariantCulture);

        private static string WindowClass(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "none";
            var buffer = new StringBuilder(80);
            NativeMethods.GetClassNameW(handle, buffer, buffer.Capacity);
            return Safe(buffer.ToString());
        }

        private static string Safe(string? value) => (value ?? string.Empty)
            .Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

        private void WindowClosed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= TimerTick;
            _window.Closed -= WindowClosed;
            ComponentDispatcher.ThreadPreprocessMessage -= ObserveMessage;
            _writer.Dispose();
        }
    }
}
