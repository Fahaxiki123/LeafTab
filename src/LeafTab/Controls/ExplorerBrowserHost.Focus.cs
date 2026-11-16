using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        private bool _synchronizingLogicalFocus;

        // Native child HWNDs do not participate in WPF's routed focus events.
        // Remember this host in the window's focus scope, otherwise WPF restores
        // the last text box when focus returns to the WPF part of the window.
        private void RememberNativeFocus()
        {
            if (_synchronizingLogicalFocus || !CanReceiveKeyboardFocus)
                return;

            _synchronizingLogicalFocus = true;
            try
            {
                DependencyObject scope = FocusManager.GetFocusScope(this);
                FocusManager.SetFocusedElement(scope, this);
                // Retain only logical focus. A WPF keyboard-focused HwndHost lets
                // DirectionalNavigation consume arrows before the native list can.
                Keyboard.ClearFocus();
            }
            finally
            {
                _synchronizingLogicalFocus = false;
            }
        }

        protected override bool TabIntoCore(TraversalRequest request)
        {
            if (!CanReceiveKeyboardFocus)
                return false;

            // Keyboard.Focus / SetFocusedElement can call back into this method.
            // Only the native half runs here; it must not start another WPF focus move.
            FocusNativeViewCore();
            return IsNativeFocusInside();
        }

        protected override bool TranslateAcceleratorCore(ref MSG message, ModifierKeys modifiers)
        {
            // WPF consults its native input sinks before directional focus
            // navigation. ThreadPreprocessMessage alone runs too late: WPF can
            // already have consumed an arrow and moved focus to the search box.
            long key = message.wParam.ToInt64();
            if (message.message != NativeMethods.WM_KEYDOWN || key < 0x25 || key > 0x28 ||
                (modifiers & ModifierKeys.Alt) != 0 || !CanReceiveKeyboardFocus || !IsNativeFocusInside())
                return false;

            // The interop input-sink contract does not allow changing WPF's MSG.
            MSG shellMessage = message;
            if (TryTranslateShellAccelerator(ref shellMessage))
                return true;

            IntPtr focused = NativeMethods.GetFocus();
            if (focused == IntPtr.Zero || focused == _hostHwnd ||
                !NativeMethods.IsChild(_hostHwnd, focused))
                return false;

            // Shell views normally leave plain navigation keys to their child
            // window. Deliver to that exact HWND (tree/list/rename edit), then
            // consume the key so WPF cannot move to another control as well.
            NativeMethods.SendMessageW(focused, (uint)message.message, message.wParam, message.lParam);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_isDestroyed)
                    ViewInteractionOccurred?.Invoke(this, EventArgs.Empty);
            }));
            return true;
        }

        private void RememberFocusAfterNativeInput()
        {
            if (_synchronizingLogicalFocus || !CanReceiveKeyboardFocus || !IsNativeFocusInside())
                return;

            // Preserve the exact native child chosen by the Shell (including an
            // inline rename editor); synchronizing WPF must not select another item.
            IntPtr focused = NativeMethods.GetFocus();
            RememberNativeFocus();
            if (CanReceiveKeyboardFocus && NativeMethods.IsWindow(focused) &&
                (focused == _hostHwnd || NativeMethods.IsChild(_hostHwnd, focused)))
                NativeMethods.SetFocus(focused);
        }
    }
}
