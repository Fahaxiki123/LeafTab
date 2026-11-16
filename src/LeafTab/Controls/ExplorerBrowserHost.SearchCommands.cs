using System;
using System.Runtime.InteropServices;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        // ResultsFolder browsers stay at their initial location. Handle the default
        // action explicitly so opening one result folder still uses this LeafTab tab.
        private sealed class SearchResultsSite : LeafTab.Interop.IServiceProvider,
            IExplorerPaneVisibility, ICommDlgBrowser
        {
            private const uint EPS_DEFAULT_OFF = 0x2;
            private const uint EPS_FORCE = 0x20000;
            private const int E_NOINTERFACE = unchecked((int)0x80004002);
            private static readonly Guid SID_ExplorerPaneVisibility = new Guid("e07010ec-bc17-44c0-97b0-46c7c95b9edc");
            // ShObjIdl_core.h defines SID_SExplorerBrowserFrame as IID_ICommDlgBrowser.
            private static readonly Guid SID_SExplorerBrowserFrame = new Guid("000214f1-0000-0000-c000-000000000046");
            private readonly WeakReference<ExplorerBrowserHost> _ownerRef;

            public SearchResultsSite(ExplorerBrowserHost owner) =>
                _ownerRef = new WeakReference<ExplorerBrowserHost>(owner);

            public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
            {
                result = IntPtr.Zero;
                if (service != SID_ExplorerPaneVisibility && service != SID_SExplorerBrowserFrame)
                    return E_NOINTERFACE;

                IntPtr unknown = Marshal.GetIUnknownForObject(this);
                try { return Marshal.QueryInterface(unknown, ref iid, out result); }
                finally { Marshal.Release(unknown); }
            }

            public int GetPaneState(ref Guid pane, out uint state)
            {
                state = EPS_DEFAULT_OFF | EPS_FORCE;
                return ShellConstants.S_OK;
            }

            public int OnDefaultCommand(IntPtr shellView)
            {
                if (shellView == IntPtr.Zero || !_ownerRef.TryGetTarget(out ExplorerBrowserHost? owner) ||
                    owner._isDestroyed || !owner._isShowingSearchResults ||
                    owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished)
                    return ShellConstants.S_FALSE;

                IntPtr folderPidl = IntPtr.Zero;
                try
                {
                    folderPidl = TryGetSingleSelectedFolderPidl(shellView);
                    if (folderPidl == IntPtr.Zero || PidlManager.AreEqual(folderPidl, owner._searchResultsPidl))
                        return ShellConstants.S_FALSE;

                    // Queue only managed bytes: closing the tab or shutting down the
                    // Dispatcher before this runs cannot leak an owned native PIDL.
                    byte[] targetBytes = PidlManager.ToBytes(folderPidl);
                    if (!PidlManager.IsValidPidlBytes(targetBytes))
                        return ShellConstants.S_FALSE;

                    int generation = owner._searchGeneration;
                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (owner._isDestroyed || !owner._isShowingSearchResults ||
                            generation != owner._searchGeneration)
                            return;

                        IntPtr target = PidlManager.FromBytes(targetBytes);
                        try
                        {
                            if (target != IntPtr.Zero)
                                owner.NavigateToPidl(target);
                        }
                        finally { PidlManager.Free(ref target); }
                    }));
                    return ShellConstants.S_OK;
                }
                catch
                {
                    // A failed or unsupported selection keeps Shell's normal action.
                    return ShellConstants.S_FALSE;
                }
                finally { PidlManager.Free(ref folderPidl); }
            }

            public int OnStateChange(IntPtr shellView, uint change) => ShellConstants.S_OK;

            public int IncludeObject(IntPtr shellView, IntPtr childPidl) => ShellConstants.S_OK;

            private static IntPtr TryGetSingleSelectedFolderPidl(IntPtr shellView)
            {
                Guid iid = ShellConstants.IID_IFolderView2;
                IntPtr viewPointer = IntPtr.Zero;
                IFolderView2? view = null;
                IShellItemArray? selection = null;
                IShellItem? item = null;
                try
                {
                    if (Marshal.QueryInterface(shellView, ref iid, out viewPointer) != ShellConstants.S_OK ||
                        viewPointer == IntPtr.Zero)
                        return IntPtr.Zero;

                    view = (IFolderView2)Marshal.GetObjectForIUnknown(viewPointer);
                    if (view.GetSelection(false, out selection) != ShellConstants.S_OK || selection == null ||
                        selection.GetCount(out uint count) != ShellConstants.S_OK || count != 1 ||
                        selection.GetItemAt(0, out item) != ShellConstants.S_OK || item == null)
                        return IntPtr.Zero;

                    uint mask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                    // S_FALSE means that only some requested attributes are set;
                    // an ordinary folder has FOLDER but intentionally lacks STREAM.
                    if (item.GetAttributes(mask, out uint attributes) < 0 ||
                        (attributes & ShellConstants.SFGAO_FOLDER) == 0 ||
                        (attributes & ShellConstants.SFGAO_STREAM) != 0)
                        return IntPtr.Zero;

                    // ResultsFolder items can expose a container-relative identity.
                    // Reparse real paths first; retain namespace navigation for virtual folders.
                    string? path = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                    if (!string.IsNullOrEmpty(path))
                    {
                        IntPtr fileSystemPidl = PidlManager.TryParseLocalOrShellFast(path);
                        if (fileSystemPidl != IntPtr.Zero)
                            return fileSystemPidl;
                    }

                    int hr = NativeMethods.SHGetIDListFromObject(item, out IntPtr itemPidl);
                    if (hr == ShellConstants.S_OK)
                        return itemPidl;
                    PidlManager.Free(ref itemPidl);
                    return IntPtr.Zero;
                }
                finally
                {
                    if (item != null) Marshal.ReleaseComObject(item);
                    if (selection != null) Marshal.ReleaseComObject(selection);
                    if (view != null) Marshal.ReleaseComObject(view);
                    if (viewPointer != IntPtr.Zero) Marshal.Release(viewPointer);
                }
            }
        }
    }
}
