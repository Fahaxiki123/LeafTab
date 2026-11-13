using System;
using System.Runtime.InteropServices;

namespace LeafTab.Interop
{
    [Flags]
    internal enum EXPLORER_BROWSER_OPTIONS : uint
    {
        EBO_NONE = 0x00,
        EBO_NAVIGATEONCE = 0x01,
        EBO_SHOWFRAMES = 0x02,
        EBO_ALWAYSNAVIGATE = 0x04,
        EBO_NOTRAVELLOG = 0x08,
        EBO_NOWRAPPERWINDOW = 0x10,
        EBO_HTMLSHAREPOINTVIEW = 0x20,
        EBO_NOBORDER = 0x40,
        EBO_NOPERSISTVIEWSTATE = 0x80
    }

    internal enum FOLDERVIEWMODE : uint
    {
        FVM_AUTO = unchecked((uint)-1),
        FVM_FIRST = 1,
        FVM_ICON = 1,
        FVM_SMALLICON = 2,
        FVM_LIST = 3,
        FVM_DETAILS = 4,
        FVM_THUMBNAIL = 5,
        FVM_TILE = 6,
        FVM_THUMBSTRIP = 7,
        FVM_CONTENT = 8
    }

    [Flags]
    internal enum FOLDERFLAGS : uint
    {
        FWF_NONE = 0x00000000,
        FWF_AUTOARRANGE = 0x00000001,
        FWF_SHOWSELALWAYS = 0x00002000,
        FWF_NOWEBVIEW = 0x00010000,
        FWF_ALIGNLEFT = 0x00800000
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FOLDERSETTINGS
    {
        public FOLDERVIEWMODE ViewMode;
        public FOLDERFLAGS fFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY : IEquatable<PROPERTYKEY>
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }

        public bool Equals(PROPERTYKEY other) => fmtid == other.fmtid && pid == other.pid;
        public override bool Equals(object? obj) => obj is PROPERTYKEY other && Equals(other);
        public override int GetHashCode() => (fmtid.GetHashCode() * 397) ^ (int)pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SORTCOLUMN
    {
        public PROPERTYKEY propkey;
        public int direction; // SORT_ASCENDING = 1, SORT_DESCENDING = -1
    }

    // Verified against ShObjIdl_core.h lines 2146-2163 (pshpack8)
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    internal static class PropertyKeys
    {
        private static readonly Guid StorageFmtId = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC");

        public static readonly PROPERTYKEY ItemTypeText = new PROPERTYKEY(StorageFmtId, 4);
        public static readonly PROPERTYKEY ItemNameDisplay = new PROPERTYKEY(StorageFmtId, 10);
        public static readonly PROPERTYKEY Size = new PROPERTYKEY(StorageFmtId, 12);
        public static readonly PROPERTYKEY DateModified = new PROPERTYKEY(StorageFmtId, 14);
    }

    internal enum SIGDN : int
    {
        SIGDN_NORMALDISPLAY = 0,
        SIGDN_PARENTRELATIVEPARSING = unchecked((int)0x80018001),
        SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000),
        SIGDN_PARENTRELATIVEEDITING = unchecked((int)0x80031001),
        SIGDN_DESKTOPABSOLUTEEDITING = unchecked((int)0x8004c000),
        SIGDN_FILESYSPATH = unchecked((int)0x80058000),
        SIGDN_URL = unchecked((int)0x80068000),
        SIGDN_PARENTRELATIVEFORADDRESSBAR = unchecked((int)0x8007c001),
        SIGDN_PARENTRELATIVE = unchecked((int)0x80080001),
        SIGDN_PARENTRELATIVEFORUI = unchecked((int)0x80094001)
    }

    internal static class ShellConstants
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;

        public const int SORT_ASCENDING = 1;
        public const int SORT_DESCENDING = -1;

        public const uint SBSP_DEFBROWSER = 0x0000;
        public const uint SBSP_SAMEBROWSER = 0x0001;
        public const uint SBSP_ABSOLUTE = 0x0000;
        public const uint SBSP_RELATIVE = 0x1000;
        public const uint SBSP_PARENT = 0x2000;
        public const uint SBSP_NAVIGATEBACK = 0x4000;
        public const uint SBSP_NAVIGATEFORWARD = 0x8000;

        public const uint SVGIO_BACKGROUND = 0x0;
        public const uint SVGIO_SELECTION = 0x1;
        public const uint SVGIO_ALLVIEW = 0x2;

        public const uint SVSI_DESELECT = 0x0;
        public const uint SVSI_SELECT = 0x1;
        public const uint SVSI_EDIT = 0x3;
        public const uint SVSI_DESELECTOTHERS = 0x4;
        public const uint SVSI_ENSUREVISIBLE = 0x8;
        public const uint SVSI_FOCUSED = 0x10;

        public const uint SHCONTF_FOLDERS = 0x0020;
        public const uint SHCONTF_NONFOLDERS = 0x0040;
        public const uint SHCONTF_INCLUDEHIDDEN = 0x0080;

        public const uint CMF_NORMAL = 0x00000000;
        public const uint CMF_DEFAULTONLY = 0x00000001;
        public const uint CMF_CANRENAME = 0x00000010;

        public const uint SFGAO_CANRENAME = 0x00000010;
        public const uint SFGAO_CANDELETE = 0x00000020;
        public const uint SFGAO_STREAM = 0x00400000;
        public const uint SFGAO_FOLDER = 0x20000000;
        public const uint SFGAO_FILESYSTEM = 0x40000000;

        public static readonly Guid CLSID_ExplorerBrowser = new Guid("71f96385-ddd6-48d3-a0c1-ae06e8b055fb");
        public static readonly Guid IID_IShellView = new Guid("000214E3-0000-0000-C000-000000000046");
        public static readonly Guid IID_IFolderView = new Guid("cde725b0-ccc9-4519-917e-325d72fab4ce");
        public static readonly Guid IID_IFolderView2 = new Guid("1af3a467-214f-4298-908e-06b03e0b39f9");
        public static readonly Guid IID_IPersistFolder2 = new Guid("1AC3D9F0-175C-11d1-95BE-00609797EA4F");
        public static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        public static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        public static readonly Guid IID_IShellItem2 = new Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
        public static readonly Guid IID_IShellItemArray = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
        public static readonly Guid IID_IEnumIDList = new Guid("000214F2-0000-0000-C000-000000000046");
        public static readonly Guid IID_IContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");
        public static readonly Guid IID_IResultsFolder = new Guid("96E5AE6D-6AE1-4b1c-900C-C6480EAA8828");
        public static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        public static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    }

    // Verified slot-for-slot against Windows SDK ShObjIdl_core.h lines 16447-16553
    [ComImport]
    [Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerBrowser
    {
        [PreserveSig]
        int Initialize(IntPtr hwndParent, [In] ref RECT prc, [In] ref FOLDERSETTINGS pfs);

        [PreserveSig]
        int Destroy();

        [PreserveSig]
        int SetRect([In, Out] ref IntPtr phdwp, RECT rcBrowser);

        [PreserveSig]
        int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);

        [PreserveSig]
        int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);

        [PreserveSig]
        int SetFolderSettings([In] ref FOLDERSETTINGS pfs);

        [PreserveSig]
        int Advise([MarshalAs(UnmanagedType.Interface)] IExplorerBrowserEvents psbe, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);

        [PreserveSig]
        int SetOptions(EXPLORER_BROWSER_OPTIONS dwFlag);

        [PreserveSig]
        int GetOptions(out EXPLORER_BROWSER_OPTIONS pdwFlag);

        [PreserveSig]
        int BrowseToIDList(IntPtr pidl, uint uFlags);

        [PreserveSig]
        int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);

        [PreserveSig]
        int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object? punk, uint dwFlags);

        [PreserveSig]
        int RemoveAll();

        [PreserveSig]
        int GetCurrentView(ref Guid riid, out IntPtr ppv);
    }

    // Verified against ShObjIdl_core.h lines 16254-16294
    [ComImport]
    [Guid("361bbdc7-e6ee-4e13-be58-58e2240c810f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerBrowserEvents
    {
        [PreserveSig]
        int OnNavigationPending(IntPtr pidlFolder);

        [PreserveSig]
        int OnViewCreated([MarshalAs(UnmanagedType.Interface)] IShellView? psv);

        [PreserveSig]
        int OnNavigationComplete(IntPtr pidlFolder);

        [PreserveSig]
        int OnNavigationFailed(IntPtr pidlFolder);
    }

    // Verified against ShObjIdl_core.h lines 5470-5564 (inherits IOleWindow)
    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig]
        int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

        [PreserveSig]
        int TranslateAccelerator(ref NATIVE_MSG pmsg);

        [PreserveSig]
        int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);

        [PreserveSig]
        int UIActivate(uint uState);

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int CreateViewWindow(
            [MarshalAs(UnmanagedType.Interface)] IShellView? psvPrevious,
            ref FOLDERSETTINGS pfs,
            IntPtr psb,
            ref RECT prcView,
            out IntPtr phWnd);

        [PreserveSig]
        int DestroyViewWindow();

        [PreserveSig]
        int GetCurrentInfo(out FOLDERSETTINGS pfs);

        [PreserveSig]
        int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);

        [PreserveSig]
        int SaveViewState();

        [PreserveSig]
        int SelectItem(IntPtr pidlItem, uint uFlags);

        [PreserveSig]
        int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
    }

    // Verified against ShObjIdl_core.h lines 5976-6075
    [ComImport]
    [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView
    {
        [PreserveSig]
        int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig]
        int SetCurrentViewMode(uint ViewMode);

        [PreserveSig]
        int GetFolder(ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int Item(int iItemIndex, out IntPtr ppidl);

        [PreserveSig]
        int ItemCount(uint uFlags, out int pcItems);

        [PreserveSig]
        int Items(uint uFlags, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetSelectionMarkedItem(out int piItem);

        [PreserveSig]
        int GetFocusedItem(out int piItem);

        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out POINT ppt);

        [PreserveSig]
        int GetSpacing(ref POINT ppt);

        [PreserveSig]
        int GetDefaultSpacing(out POINT ppt);

        [PreserveSig]
        int GetAutoArrange();

        [PreserveSig]
        int SelectItem(int iItem, uint dwFlags);

        [PreserveSig]
        int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    }

    // Verified slot-for-slot against ShObjIdl_core.h lines 6199-6520 (14 IFolderView slots + 21 IFolderView2 slots)
    [ComImport]
    [Guid("1af3a467-214f-4298-908e-06b03e0b39f9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView2
    {
        // --- IFolderView base slots (14) ---
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(ref POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);

        // --- IFolderView2 own slots (21) ---
        [PreserveSig] int SetGroupBy([In] ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] bool fAscending);
        [PreserveSig] int GetGroupBy(out PROPERTYKEY pkey, [MarshalAs(UnmanagedType.Bool)] out bool pfAscending);
        [PreserveSig] int SetViewProperty(IntPtr pidl, [In] ref PROPERTYKEY propkey, IntPtr propvar);
        [PreserveSig] int GetViewProperty(IntPtr pidl, [In] ref PROPERTYKEY propkey, IntPtr ppropvar);
        [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
        [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
        [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
        [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
        [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
        [PreserveSig] int GetSortColumnCount(out int pcColumns);
        [PreserveSig] int SetSortColumns([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] SORTCOLUMN[] rgSortColumns, int cColumns);
        [PreserveSig] int GetSortColumns([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] SORTCOLUMN[] rgSortColumns, int cColumns);
        [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetVisibleItem(int iStart, [MarshalAs(UnmanagedType.Bool)] bool fPrevious, out int piItem);
        [PreserveSig] int GetSelectedItem(int iStart, out int piItem);
        [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Bool)] bool fNoneImpliesFolder, [MarshalAs(UnmanagedType.Interface)] out IShellItemArray? ppsia);
        [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
        [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string? pszVerb);
        [PreserveSig] int SetViewModeAndIconSize(FOLDERVIEWMODE uViewMode, int iImageSize);
        [PreserveSig] int GetViewModeAndIconSize(out FOLDERVIEWMODE puViewMode, out int piImageSize);
        [PreserveSig] int SetGroupSubsetCount(uint cVisibleRows);
        [PreserveSig] int GetGroupSubsetCount(out uint pcVisibleRows);
        [PreserveSig] int SetRedraw([MarshalAs(UnmanagedType.Bool)] bool fRedrawOn);
        [PreserveSig] int IsMoveInSameFolder();
        [PreserveSig] int DoRename();
    }

    // Verified against ShObjIdl_core.h lines 3426-3461
    [ComImport]
    [Guid("1AC3D9F0-175C-11d1-95BE-00609797EA4F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFolder2
    {
        [PreserveSig]
        int GetClassID(out Guid pClassID);

        [PreserveSig]
        int Initialize(IntPtr pidl);

        [PreserveSig]
        int GetCurFolder(out IntPtr ppidl);
    }

    // Verified against ShObjIdl_core.h lines 4505-4540
    [ComImport]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIDList? ppenum);
    }

    // Verified against ShObjIdl_core.h lines 4643-4750
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten,
            out IntPtr ppidl,
            ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [In] ref IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint cidl,
            [In] ref IntPtr apidl,
            ref Guid riid,
            IntPtr rgfReserved,
            out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig]
        int SetNameOf(
            IntPtr hwnd,
            IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags,
            out IntPtr ppidlOut);
    }

    // Verified against ShObjIdl_core.h lines 8898-8951
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsi);

        [PreserveSig]
        int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem? psi, uint hint, out int piOrder);
    }

    // Verified against ShObjIdl_core.h lines 9057-9120
    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem2
    {
        // --- IShellItem base slots (5) ---
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem? psi, uint hint, out int piOrder);

        // --- IShellItem2 own slots (13) ---
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, [MarshalAs(UnmanagedType.IUnknown)] object? punkCreateObject, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList([In] ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty([In] ref PROPERTYKEY key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID([In] ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime([In] ref PROPERTYKEY key, out System.Runtime.InteropServices.ComTypes.FILETIME pft);
        [PreserveSig] int GetInt32([In] ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString([In] ref PROPERTYKEY key, out IntPtr ppsz);
        [PreserveSig] int GetUInt32([In] ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64([In] ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool([In] ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] out bool pf);
    }

    // Verified against ShObjIdl_core.h lines 11008-11043
    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList([In] ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    // Verified against ShObjIdl_core.h lines 2229-2258
    [ComImport]
    [Guid("000214e4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    // Verified against ShObjIdl.h lines 4213-4234
    [ComImport]
    [Guid("96E5AE6D-6AE1-4b1c-900C-C6480EAA8828")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IResultsFolder
    {
        [PreserveSig]
        int AddItem([MarshalAs(UnmanagedType.Interface)] IShellItem? psi);

        [PreserveSig]
        int AddIDList(IntPtr pidl, out IntPtr ppidlAdded);

        [PreserveSig]
        int RemoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem? psi);

        [PreserveSig]
        int RemoveIDList(IntPtr pidl);

        [PreserveSig]
        int RemoveAll();
    }

    // Verified against OCIdl.h lines 6106-6117
    [ComImport]
    [Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectWithSite
    {
        [PreserveSig]
        int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);

        [PreserveSig]
        int GetSite(ref Guid riid, out IntPtr ppvSite);
    }

    // Verified against ServProv.h lines 140-152
    [ComImport]
    [Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    // Verified against ShObjIdl_core.h lines 25675-25701
    [ComImport]
    [Guid("e07010ec-bc17-44c0-97b0-46c7c95b9edc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerPaneVisibility
    {
        [PreserveSig]
        int GetPaneState(ref Guid ep, out uint peps);
    }

    // Verified against Unknwnbase.h IClassFactory
    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    // Verified against ShObjIdl_core.h lines 2859-2883
    [ComImport]
    [Guid("7F9185B0-CB92-43c5-80A9-92277A4F7B54")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExecuteCommand
    {
        [PreserveSig]
        int SetKeyState(uint grfKeyState);

        [PreserveSig]
        int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string? pszParameters);

        [PreserveSig]
        int SetPosition(POINT pt);

        [PreserveSig]
        int SetShowWindow(int nShow);

        [PreserveSig]
        int SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool fNoShowUI);

        [PreserveSig]
        int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string? pszDirectory);

        [PreserveSig]
        int Execute();
    }

    // Verified against ShObjIdl_core.h lines 11278-11289
    [ComImport]
    [Guid("1c9cd5bb-98e9-4491-a60f-31aacc72b83c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectWithSelection
    {
        [PreserveSig]
        int SetSelection([MarshalAs(UnmanagedType.Interface)] IShellItemArray? psia);

        [PreserveSig]
        int GetSelection(ref Guid riid, out IntPtr ppv);
    }

    // Verified slot-for-slot against ShObjIdl_core.h lines 8350-8414 (inherits IOleWindow)
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        // IOleWindow (2 slots)
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

        // IShellBrowser (13 slots)
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int TranslateAcceleratorSB(ref NATIVE_MSG pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? ppshv);
        [PreserveSig] int OnViewWindowActive([MarshalAs(UnmanagedType.Interface)] IShellView? pshv);
        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    // Verified slot-for-slot against ShObjIdl_core.h IShellLinkW (18 slots)
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
