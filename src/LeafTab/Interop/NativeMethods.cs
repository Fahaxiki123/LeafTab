using System;
using System.Runtime.InteropServices;

namespace LeafTab.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public bool Contains(POINT pt) =>
            pt.X >= Left && pt.X < Right && pt.Y >= Top && pt.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NATIVE_MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    // Verified against Windows SDK ShlObj_core.h lines 95, 3101, 3253-3298 (pshpack1, 32 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SHELLSTATE
    {
        public uint BitVector1;
        public uint dwWin95Unused;
        public uint uWin95Unused;
        public int lParamSort;
        public int iSortDirection;
        public uint version;
        public uint uNotUsed;
        public uint BitVector2;

        public bool ShowAllObjects
        {
            get => (BitVector1 & 0x00000001u) != 0;
            set => BitVector1 = value ? (BitVector1 | 0x00000001u) : (BitVector1 & ~0x00000001u);
        }

        public bool ShowExtensions
        {
            get => (BitVector1 & 0x00000002u) != 0;
            set => BitVector1 = value ? (BitVector1 | 0x00000002u) : (BitVector1 & ~0x00000002u);
        }
    }

    internal delegate IntPtr WNDPROC(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    internal delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    internal delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    internal static class NativeMethods
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int ASFW_ANY = -1;
        public const uint GA_ROOT = 2;

        public const uint CLSCTX_INPROC_SERVER = 0x1;
        public const uint CLSCTX_LOCAL_SERVER = 0x4;
        public const uint REGCLS_MULTIPLEUSE = 0x1;

        public const uint WM_SETFOCUS = 0x0007;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;
        public const uint WM_SYSKEYDOWN = 0x0104;
        public const uint WM_SYSKEYUP = 0x0105;
        public const uint WM_CHANGEUISTATE = 0x0127;
        public const uint WM_UPDATEUISTATE = 0x0128;
        public const uint WM_QUERYUISTATE = 0x0129;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_RBUTTONDOWN = 0x0204;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_MBUTTONDOWN = 0x0207;
        public const uint WM_MBUTTONUP = 0x0208;
        public const uint WM_XBUTTONDOWN = 0x020B;
        public const uint WM_XBUTTONUP = 0x020C;
        public const uint WM_NCXBUTTONDOWN = 0x00AB;
        public const uint WM_NCXBUTTONUP = 0x00AC;
        public const uint WM_APPCOMMAND = 0x0319;
        public const uint WM_ERASEBKGND = 0x0014;
        public const uint WM_NCDESTROY = 0x0082;

        public const int XBUTTON1 = 1;
        public const int XBUTTON2 = 2;
        public const int APPCOMMAND_BROWSER_BACKWARD = 1;
        public const int APPCOMMAND_BROWSER_FORWARD = 2;

        public const ushort UIS_SET = 1;
        public const ushort UIS_CLEAR = 2;
        public const ushort UIS_INITIALIZE = 3;
        public const ushort UISF_HIDEFOCUS = 0x1;
        public const ushort UISF_HIDEACCEL = 0x2;

        public static IntPtr MakeUiStateWParam(ushort action, ushort flags) =>
            (IntPtr)(((uint)flags << 16) | action);

        public const int WHITE_BRUSH = 0;

        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_SMALLICON = 0x000000001;
        public const uint SHGFI_PIDL = 0x000000008;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        public const uint SHGFI_DISPLAYNAME = 0x000000200;

        public const uint SSF_SHOWALLOBJECTS = 0x00000001;
        public const uint SSF_SHOWEXTENSIONS = 0x00000002;

        public const int SHCNE_CREATE = 0x00000002;
        public const int SHCNE_MKDIR = 0x00000008;
        public const int SHCNE_UPDATEDIR = 0x00001000;
        public const int SHCNE_ASSOCCHANGED = 0x08000000;
        public const uint SHCNF_IDLIST = 0x0000;
        public const uint SHCNF_PATHW = 0x0005;
        public const uint SHCNF_FLUSH = 0x1000;
        public const uint SHCNF_FLUSHNOWAIT = 0x3000;

        public const uint GR_GDIOBJECTS = 0;
        public const uint GR_USEROBJECTS = 1;

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        public static readonly Guid FOLDERID_Desktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        public static readonly Guid FOLDERID_Documents = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        public static readonly Guid FOLDERID_Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid FOLDERID_Pictures = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB");
        public static readonly Guid FOLDERID_ComputerFolder = new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7");
        public static readonly Guid FOLDERID_RecycleBinFolder = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll")]
        public static extern IntPtr GetStockObject(int i);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("ole32.dll")]
        public static extern int CoRegisterClassObject(
            ref Guid rclsid,
            [MarshalAs(UnmanagedType.Interface)] IClassFactory pUnk,
            uint dwClsContext,
            uint flags,
            out uint lpdwRegister);

        [DllImport("ole32.dll")]
        public static extern int CoRevokeClassObject(uint dwRegister);

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            ref Guid riid,
            out IntPtr ppv);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SUBCLASSPROC pfnSubclass,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            SUBCLASSPROC pfnSubclass,
            UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("shell32.dll")]
        public static extern IntPtr ILClone(IntPtr pidl);

        [DllImport("shell32.dll")]
        public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ILIsEqual(IntPtr pidl1, IntPtr pidl2);

        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ILRemoveLastID(IntPtr pidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(
            string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderIDList(
            ref Guid rfid,
            uint dwFlags,
            IntPtr hToken,
            out IntPtr ppidl);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderPath(
            ref Guid rfid,
            uint dwFlags,
            IntPtr hToken,
            out IntPtr ppszPath);

        [DllImport("shell32.dll")]
        public static extern int SHCreateItemFromIDList(
            IntPtr pidl,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll")]
        public static extern int SHCreateItemWithParent(
            IntPtr pidlParent,
            [MarshalAs(UnmanagedType.Interface)] IShellFolder? psfParent,
            IntPtr pidlChild,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll")]
        public static extern int SHGetIDListFromObject(
            [MarshalAs(UnmanagedType.IUnknown)] object punk,
            out IntPtr ppidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfoW(
            IntPtr pszPath,
            uint dwFileAttributes,
            ref SHFILEINFOW psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfoByNameW(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFOW psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll")]
        public static extern void SHGetSetSettings(
            ref SHELLSTATE lpss,
            uint dwMask,
            [MarshalAs(UnmanagedType.Bool)] bool bSet);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(
            int wEventId,
            uint uFlags,
            IntPtr dwItem1,
            IntPtr dwItem2);

        [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotifyPath(
            int wEventId,
            uint uFlags,
            string? dwItem1,
            string? dwItem2);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool User32DestroyIcon(IntPtr hIcon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            ref int pvAttribute,
            int cbAttribute);

        public static POINT PointFromLParam(IntPtr lParam)
        {
            long raw = lParam.ToInt64();
            int x = unchecked((short)(raw & 0xFFFF));
            int y = unchecked((short)((raw >> 16) & 0xFFFF));
            return new POINT(x, y);
        }
    }
}
