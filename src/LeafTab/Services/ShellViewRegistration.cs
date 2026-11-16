using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LeafTab.Interop;
using LeafTab.Shared;

namespace LeafTab.Services
{
    // IShellWindows is the public rendezvous used by Shell clients to locate a folder's
    // view after opening it. Registering the actual view lets SHOpenFolderAndSelectItems
    // complete its second (selection) step instead of timing out after the open verb.
    internal sealed class ShellViewRegistration : IDisposable
    {
        private IShellWindowsRegistration? _windows;
        private ShellSelectionBrowser? _browser;
        private int _cookie;
        private IntPtr _hwnd;
        private byte[]? _location;

        internal bool IsRegistered => _cookie != 0;

        internal bool Update(IntPtr hwnd, IntPtr folderPidl, Func<IntPtr> acquireShellView, Action? activate = null,
            Func<bool>? isEnabled = null)
        {
            if (hwnd == IntPtr.Zero || folderPidl == IntPtr.Zero || (isEnabled != null && !isEnabled()))
            {
                Dispose();
                return false;
            }

            try
            {
                if (_hwnd != hwnd) Dispose();
                if (_windows == null)
                {
                    Type type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!;
                    _windows = (IShellWindowsRegistration)Activator.CreateInstance(type)!;
                }
                if (_browser == null)
                    _browser = new ShellSelectionBrowser(hwnd, acquireShellView, activate, isEnabled,
                        () => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(Dispose)));
                else
                    _browser.SetView(acquireShellView, activate, isEnabled);

                byte[] bytes = PidlManager.ToBytes(folderPidl);
                if (_cookie == 0)
                {
                    object pendingLocation = bytes;
                    object emptyRoot = null!;
                    _windows.RegisterPending(unchecked((int)GetCurrentThreadId()), ref pendingLocation, ref emptyRoot,
                        1 /* SWC_BROWSER */, out _cookie);
                    _windows.Register(_browser, unchecked((int)hwnd.ToInt64()), 1, out int registeredCookie);
                    if (_cookie != registeredCookie) _windows.Revoke(_cookie);
                    _cookie = registeredCookie;
                    _hwnd = hwnd;
                }

                if (!Equal(_location, bytes))
                {
                    object location = bytes;
                    _windows.OnNavigate(_cookie, ref location);
                    _location = bytes;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-view-registration-failed", ex);
                Dispose();
                return false;
            }
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static bool Equal(byte[]? left, byte[] right)
        {
            if (left == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        public void Dispose()
        {
            if (_cookie != 0)
            {
                try { _windows?.Revoke(_cookie); }
                catch (Exception ex) { DiagnosticLog.Write("shell-view-revoke-failed", ex); }
            }
            _cookie = 0;
            _hwnd = IntPtr.Zero;
            _location = null;
            _browser?.Detach();
            _browser = null;
            if (_windows != null && Marshal.IsComObject(_windows)) Marshal.ReleaseComObject(_windows);
            _windows = null;
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShellWindowsRegistration
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object? Item([In, MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int windowClass, out int cookie);
        void RegisterPending(int threadId, [In] ref object location, [In] ref object root, int windowClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [In] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In] ref object location, [In] ref object root, int windowClass, out int hwnd, int options);
        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
    }

    // Use a dedicated vtable declaration so QueryActiveShellView can transfer the native
    // AddRef from IExplorerBrowser.GetCurrentView directly, without retaining leaked RCWs.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellSelectionBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr oleMenu, IntPtr activeWindow);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(ref NATIVE_MSG message, ushort id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IntPtr shellView);
        [PreserveSig] int OnViewWindowActive(IntPtr shellView);
        [PreserveSig] int SetToolbarItems(IntPtr buttons, uint count, uint flags);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ShellSelectionBrowser : LeafTab.Interop.IServiceProvider, IShellSelectionBrowser, IShellWebBrowserApp
    {
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int E_FAIL = unchecked((int)0x80004005);
        private readonly IntPtr _hwnd;
        private Func<IntPtr>? _acquireView;
        private Action? _activate;
        private Func<bool>? _isEnabled;
        private readonly Action? _invalidate;

        internal ShellSelectionBrowser(IntPtr hwnd, Func<IntPtr> acquireView, Action? activate,
            Func<bool>? isEnabled = null, Action? invalidate = null)
        {
            _hwnd = hwnd;
            _invalidate = invalidate;
            SetView(acquireView, activate, isEnabled);
        }

        public int HWND { get { RequireAvailable(); return unchecked((int)_hwnd.ToInt64()); } }
        public string FullName => Assembly.GetExecutingAssembly().Location;
        public string Name => "LeafTab";

        internal void SetView(Func<IntPtr> acquireView, Action? activate, Func<bool>? isEnabled)
        { _acquireView = acquireView; _activate = activate; _isEnabled = isEnabled; }
        internal void Detach() { _acquireView = null; _activate = null; _isEnabled = null; }
        private bool IsAvailable()
        {
            if (_acquireView == null) return false;
            if (_isEnabled == null || _isEnabled()) return true;
            _invalidate?.Invoke();
            return false;
        }
        private void RequireAvailable()
        {
            if (!IsAvailable()) throw new COMException("The registered Shell view is no longer available.", E_NOINTERFACE);
        }

        public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (!IsAvailable()) return E_NOINTERFACE;
            if (service == ShellConstants.IID_IFolderView || service == ShellConstants.IID_IFolderView2 || service == ShellConstants.IID_IShellView)
            {
                IntPtr view = _acquireView!();
                if (view == IntPtr.Zero) return E_NOINTERFACE;
                try { return Marshal.QueryInterface(view, ref iid, out result); }
                finally { Marshal.Release(view); }
            }
            if (service != ShellConstants.SID_STopLevelBrowser && service != ShellConstants.IID_IShellBrowser)
                return E_NOINTERFACE;
            IntPtr unknown = Marshal.GetIUnknownForObject(this);
            try { return Marshal.QueryInterface(unknown, ref iid, out result); }
            finally { Marshal.Release(unknown); }
        }

        public int GetWindow(out IntPtr hwnd) { hwnd = _hwnd; return !IsAvailable() ? E_FAIL : 0; }
        public int QueryActiveShellView(out IntPtr shellView)
        {
            shellView = IntPtr.Zero;
            try
            {
                if (!IsAvailable()) return E_FAIL;
                shellView = _acquireView!();
                return shellView == IntPtr.Zero ? E_FAIL : 0;
            }
            catch (Exception ex) { DiagnosticLog.Write("shell-view-query-failed", ex); return Marshal.GetHRForException(ex); }
        }
        public int ContextSensitiveHelp(bool enter) => E_NOTIMPL;
        public int InsertMenusSB(IntPtr menu, IntPtr widths) => E_NOTIMPL;
        public int SetMenuSB(IntPtr menu, IntPtr oleMenu, IntPtr activeWindow) => E_NOTIMPL;
        public int RemoveMenusSB(IntPtr menu) => E_NOTIMPL;
        public int SetStatusTextSB(string? text) => 0;
        public int EnableModelessSB(bool enable) => 0;
        int IShellSelectionBrowser.TranslateAcceleratorSB(ref NATIVE_MSG message, ushort id) => 1;
        public int BrowseObject(IntPtr pidl, uint flags) => E_NOTIMPL;
        public int GetViewStateStream(uint mode, out IntPtr stream) { stream = IntPtr.Zero; return E_NOTIMPL; }
        public int GetControlWindow(uint id, out IntPtr hwnd) { hwnd = IntPtr.Zero; return E_NOTIMPL; }
        public int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result) { result = IntPtr.Zero; return E_NOTIMPL; }
        public int OnViewWindowActive(IntPtr shellView) => 0;
        public int SetToolbarItems(IntPtr buttons, uint count, uint flags) => E_NOTIMPL;

        private static void UnsupportedBrowserOperation() => throw new COMException("This Shell operation is not supported.", E_NOTIMPL);
        void IShellWebBrowserApp.GoBack() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.GoForward() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.GoHome() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.GoSearch() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.Navigate(string url, ref object flags, ref object frame, ref object post, ref object headers) => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.Refresh() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.Refresh2(ref object level) => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.Stop() { }
        object IShellWebBrowserApp.Application => this;
        object IShellWebBrowserApp.Parent => this;
        object IShellWebBrowserApp.Container => this;
        object IShellWebBrowserApp.Document { get { RequireAvailable(); return this; } }
        bool IShellWebBrowserApp.TopLevelContainer => true;
        string IShellWebBrowserApp.Type => "Shell Folder View";
        int IShellWebBrowserApp.Left { get => 0; set => UnsupportedBrowserOperation(); }
        int IShellWebBrowserApp.Top { get => 0; set => UnsupportedBrowserOperation(); }
        int IShellWebBrowserApp.Width { get => 0; set => UnsupportedBrowserOperation(); }
        int IShellWebBrowserApp.Height { get => 0; set => UnsupportedBrowserOperation(); }
        string IShellWebBrowserApp.LocationName => string.Empty;
        string IShellWebBrowserApp.LocationURL => string.Empty;
        bool IShellWebBrowserApp.Busy => false;
        void IShellWebBrowserApp.Quit() => UnsupportedBrowserOperation();
        void IShellWebBrowserApp.ClientToWindow(ref int width, ref int height) { }
        void IShellWebBrowserApp.PutProperty(string name, object value) => UnsupportedBrowserOperation();
        object? IShellWebBrowserApp.GetProperty(string name) => null;
        string IShellWebBrowserApp.Name => Name;
        IntPtr IShellWebBrowserApp.HWND { get { RequireAvailable(); return _hwnd; } }
        string IShellWebBrowserApp.FullName => FullName;
        string IShellWebBrowserApp.Path => System.IO.Path.GetDirectoryName(FullName) ?? string.Empty;
        bool IShellWebBrowserApp.Visible
        {
            get => NativeMethods.IsWindowVisible(_hwnd);
            set { if (value && IsAvailable()) _activate?.Invoke(); }
        }
        bool IShellWebBrowserApp.StatusBar { get => true; set { } }
        string IShellWebBrowserApp.StatusText { get => string.Empty; set { } }
        int IShellWebBrowserApp.ToolBar { get => 0; set { } }
        bool IShellWebBrowserApp.MenuBar { get => false; set { } }
        bool IShellWebBrowserApp.FullScreen { get => false; set => UnsupportedBrowserOperation(); }
    }
}
