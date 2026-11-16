using System;
using System.Runtime.InteropServices;

namespace LeafTab.Interop
{
    // Flattened IWebBrowser + IWebBrowserApp vtable, checked against Windows SDK
    // ExDisp.Idl. SHOpenFolderAndSelectItems queries this before IShellBrowser.
    [ComImport, Guid("0002DF05-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShellWebBrowserApp
    {
        [DispId(100)] void GoBack();
        [DispId(101)] void GoForward();
        [DispId(102)] void GoHome();
        [DispId(103)] void GoSearch();
        [DispId(104)] void Navigate([MarshalAs(UnmanagedType.BStr)] string url, ref object flags, ref object frame, ref object post, ref object headers);
        [DispId(-550)] void Refresh();
        [DispId(105)] void Refresh2(ref object level);
        [DispId(106)] void Stop();
        [DispId(200)] object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(201)] object Parent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(202)] object Container { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(203)] object Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(204)] bool TopLevelContainer { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        [DispId(205)] string Type { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(206)] int Left { get; set; }
        [DispId(207)] int Top { get; set; }
        [DispId(208)] int Width { get; set; }
        [DispId(209)] int Height { get; set; }
        [DispId(210)] string LocationName { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(211)] string LocationURL { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(212)] bool Busy { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        [DispId(300)] void Quit();
        [DispId(301)] void ClientToWindow(ref int width, ref int height);
        [DispId(302)] void PutProperty([MarshalAs(UnmanagedType.BStr)] string name, object value);
        [DispId(303)] object? GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
        [DispId(0)] string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(-515)] IntPtr HWND { get; }
        [DispId(400)] string FullName { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(401)] string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
        [DispId(402)] bool Visible { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        [DispId(403)] bool StatusBar { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        [DispId(404)] string StatusText { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        [DispId(405)] int ToolBar { get; set; }
        [DispId(406)] bool MenuBar { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        [DispId(407)] bool FullScreen { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    }
}
