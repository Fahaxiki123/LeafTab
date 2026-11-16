using System;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        // Caller receives the reference obtained from GetCurrentView and must release it
        // or transfer it to an out-interface parameter of a COM method.
        internal IntPtr AcquireShellViewPointer()
        {
            if (_isDestroyed || _isShowingSearchResults || _browser == null) return IntPtr.Zero;
            Guid iid = ShellConstants.IID_IShellView;
            return _browser.GetCurrentView(ref iid, out IntPtr view) >= 0 ? view : IntPtr.Zero;
        }
    }
}
