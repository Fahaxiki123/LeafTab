using System;
using System.Text;
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
        private int _wpfEditorFocusHandoffDepth;
        private Action? _pendingWpfEditorFocus;
        private IntPtr _subclassedNavigationTreeHwnd;
        private readonly SUBCLASSPROC _navigationTreeSubclassDelegate;
        private static readonly UIntPtr NavigationTreeSubclassId = new UIntPtr(0x4C465452);

        internal bool IsNativeEditControlFocused()
        {
            if (!IsNativeFocusInside()) return false;
            var name = new StringBuilder(64);
            return NativeMethods.GetClassNameW(NativeMethods.GetFocus(), name, name.Capacity) > 0 &&
                   string.Equals(name.ToString(), "Edit", StringComparison.OrdinalIgnoreCase);
        }

        internal void MoveFocusToWpfEditor(Action moveFocus)
        {
            _pendingWpfEditorFocus = IsNativeEditControlFocused() ? moveFocus : null;
            _wpfEditorFocusHandoffDepth++;
            try { moveFocus(); }
            finally { _wpfEditorFocusHandoffDepth--; }
        }

        internal bool HasPendingWpfEditorFocus => _pendingWpfEditorFocus != null;

        internal void CancelPendingWpfEditorFocus() => _pendingWpfEditorFocus = null;

        private bool RestoreWpfEditorAfterNativeRenameCompletion()
        {
            if (_pendingWpfEditorFocus == null || _wpfEditorFocusHandoffDepth > 0 || !CanReceiveKeyboardFocus)
                return false;
            // Shell posts one final focus restoration when an inline rename
            // closes. Honor the newer explicit editor request once; real native
            // mouse input cancels this handoff before it can receive focus.
            Action restore = _pendingWpfEditorFocus;
            _pendingWpfEditorFocus = null;
            _wpfEditorFocusHandoffDepth++;
            try { restore(); }
            finally { _wpfEditorFocusHandoffDepth--; }
            return true;
        }

        protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            // SetFocusedElement also attempts Keyboard.Focus. Native focus is
            // already correct here: do not let the WPF input provider briefly
            // move it through the root HWND and commit an inline edit.
            if (_synchronizingLogicalFocus && IsNativeFocusInside())
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewGotKeyboardFocus(e);
        }

        // Native child HWNDs do not participate in WPF's routed focus events.
        // Remember this host in the window's focus scope, otherwise WPF restores
        // the last text box when focus returns to the WPF part of the window.
        private void RememberNativeFocus()
        {
            if (_synchronizingLogicalFocus || _wpfEditorFocusHandoffDepth > 0 || !CanReceiveKeyboardFocus)
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
            if (_wpfEditorFocusHandoffDepth > 0)
                return false;
            if (_synchronizingLogicalFocus)
                return IsNativeFocusInside();
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
            bool isTab = key == 0x09 && (modifiers & ModifierKeys.Control) == 0;
            bool isArrow = key >= 0x25 && key <= 0x28;
            if (message.message != NativeMethods.WM_KEYDOWN || (!isArrow && !isTab) ||
                (modifiers & ModifierKeys.Alt) != 0 || !CanReceiveKeyboardFocus || !IsNativeFocusInside())
                return false;

            // The interop input-sink contract does not allow changing WPF's MSG.
            MSG shellMessage = message;
            if (TryTranslateShellAccelerator(ref shellMessage))
                return true;

            if (isTab && !IsNativeEditControlFocused())
                return false;

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
            if (_synchronizingLogicalFocus || _wpfEditorFocusHandoffDepth > 0 || !CanReceiveKeyboardFocus || !IsNativeFocusInside())
                return;
            if (RestoreWpfEditorAfterNativeRenameCompletion())
                return;

            // Preserve the exact native child chosen by the Shell (including an
            // inline rename editor); synchronizing WPF must not select another item.
            IntPtr focused = NativeMethods.GetFocus();
            RememberNativeFocus();
            if (CanReceiveKeyboardFocus && NativeMethods.IsWindow(focused) &&
                (focused == _hostHwnd || NativeMethods.IsChild(_hostHwnd, focused)))
                NativeMethods.SetFocus(focused);
        }

        private void RememberFocusAfterNativePointerDown(IntPtr clickedWindow)
        {
            // After rejecting Shell's posted rename-focus restoration, the view
            // may still consider its selected item focused. A real user click
            // is an explicit request to leave the WPF editor in either case.
            if (CanReceiveKeyboardFocus && !IsNativeFocusInside())
                NativeMethods.SetFocus(clickedWindow);
            RememberFocusAfterNativeInput();
        }

        private void AttachNavigationTreeFocusObserver()
        {
            if (_browseHostHwnd == IntPtr.Zero || _isDestroyed) return;
            IntPtr tree = IntPtr.Zero;
            var className = new StringBuilder(64);
            NativeMethods.EnumChildWindows(_browseHostHwnd, (child, _) =>
            {
                className.Clear();
                if (NativeMethods.GetClassNameW(child, className, className.Capacity) > 0 &&
                    string.Equals(className.ToString(), "SysTreeView32", StringComparison.Ordinal))
                {
                    tree = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (tree == _subclassedNavigationTreeHwnd) return;
            DetachNavigationTreeFocusObserver();
            if (tree != IntPtr.Zero && NativeMethods.SetWindowSubclass(tree, _navigationTreeSubclassDelegate,
                    NavigationTreeSubclassId, UIntPtr.Zero))
                _subclassedNavigationTreeHwnd = tree;
        }

        private void DetachNavigationTreeFocusObserver()
        {
            if (_subclassedNavigationTreeHwnd != IntPtr.Zero && NativeMethods.IsWindow(_subclassedNavigationTreeHwnd))
                NativeMethods.RemoveWindowSubclass(_subclassedNavigationTreeHwnd, _navigationTreeSubclassDelegate, NavigationTreeSubclassId);
            _subclassedNavigationTreeHwnd = IntPtr.Zero;
        }

        private IntPtr NavigationTreeSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
            UIntPtr subclassId, UIntPtr referenceData)
        {
            if (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_RBUTTONDOWN ||
                message == NativeMethods.WM_MBUTTONDOWN)
                CancelPendingWpfEditorFocus();
            if (message == NativeMethods.WM_NCDESTROY)
            {
                NativeMethods.RemoveWindowSubclass(hwnd, _navigationTreeSubclassDelegate, NavigationTreeSubclassId);
                if (_subclassedNavigationTreeHwnd == hwnd) _subclassedNavigationTreeHwnd = IntPtr.Zero;
                return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
            }
            IntPtr result = NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
            if (!_isDestroyed && (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_RBUTTONDOWN ||
                message == NativeMethods.WM_MBUTTONDOWN))
                RememberFocusAfterNativePointerDown(hwnd);
            else if (!_isDestroyed && message == NativeMethods.WM_SETFOCUS)
                RememberFocusAfterNativeInput();
            if (!_isDestroyed && (message == NativeMethods.WM_LBUTTONUP || message == NativeMethods.WM_KEYUP))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!_isDestroyed) ViewInteractionOccurred?.Invoke(this, EventArgs.Empty);
                }));
            return result;
        }
    }
}
