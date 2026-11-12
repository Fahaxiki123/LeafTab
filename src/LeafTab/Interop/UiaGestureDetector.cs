using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LeafTab.Interop
{
    // Verified against Windows SDK UIAutomationClient.h lines 19092-19194
    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement? el1, IUIAutomationElement? el2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
        [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
        [PreserveSig] int GetRootElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int GetRootElementBuildCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPointBuildCache(POINT pt, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElementBuildCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
        [PreserveSig] int CreateTreeWalker(IntPtr pCondition, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_ControlViewWalker([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_ContentViewWalker([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_RawViewWalker([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
    }

    // Verified against Windows SDK UIAutomationClient.h lines 3449-3476
    [ComImport]
    [Guid("4042c624-389c-4afc-a630-9df854a541fc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? parent);
        [PreserveSig] int GetFirstChildElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? first);
        [PreserveSig] int GetLastChildElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? last);
        [PreserveSig] int GetNextSiblingElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? next);
        [PreserveSig] int GetPreviousSiblingElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? previous);
        [PreserveSig] int NormalizeElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement? element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? normalized);
    }

    // Verified against Windows SDK UIAutomationClient.h lines 1869-2114
    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? updatedElement);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? retVal);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object? retVal);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? retVal);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object? retVal);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        [PreserveSig] int GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        [PreserveSig] int GetCachedParent([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? parent);
        [PreserveSig] int GetCachedChildren(out IntPtr children);
        [PreserveSig] int get_CurrentProcessId(out int retVal);
        [PreserveSig] int get_CurrentControlType(out int retVal);
        [PreserveSig] int get_CurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentHasKeyboardFocus([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentIsKeyboardFocusable([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentIsEnabled([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentCulture(out int retVal);
        [PreserveSig] int get_CurrentIsControlElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentIsContentElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentIsPassword([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentNativeWindowHandle(out IntPtr retVal);
        [PreserveSig] int get_CurrentItemType([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentIsOffscreen([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentOrientation(out int retVal);
        [PreserveSig] int get_CurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentIsRequiredForForm([MarshalAs(UnmanagedType.Bool)] out bool retVal);
        [PreserveSig] int get_CurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentBoundingRectangle(out RECT retVal);
    }

    internal enum GestureRequestKind
    {
        MiddleClickFolder,
        DoubleClickBlank
    }

    internal enum UiaHitOutcome
    {
        IgnoredOrAmbiguous,
        HitItem,
        HitBlankView
    }

    internal sealed class UiaHitResult
    {
        public UiaHitOutcome Outcome { get; set; }
        public string RawLeafClassName { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string ItemAutomationId { get; set; } = string.Empty;
        public int PositionInSet { get; set; } = -1;
        public double ElapsedMs { get; set; }
    }

    /// <summary>
    /// Runs all UI Automation ElementFromPoint queries strictly on a dedicated background MTA thread.
    /// Per Microsoft UIA threading guidelines, querying own-process HWNDs from the UI STA thread risks
    /// reentrancy/deadlock. Here, the UI thread only posts screen coordinates (non-blocking) and receives
    /// verified results asynchronously; any timeout (>220ms) or ambiguous element safely fails closed.
    /// </summary>
    internal sealed class UiaGestureDetector : IDisposable
    {
        private static readonly Guid CLSID_CUIAutomation8 = new Guid("e22ad333-b25f-460c-83d0-0581107395c9");
        private static readonly Guid CLSID_CUIAutomation = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
        private const int UIA_PositionInSetPropertyId = 30152;
        private const double MaxAllowedGestureAgeMs = 240.0;

        private sealed class PendingQuery
        {
            public GestureRequestKind Kind;
            public IntPtr DirectUiHwnd;
            public POINT ScreenPoint;
            public int ViewGeneration;
            public long TimestampTicks;
            public Action<UiaHitResult, int>? Callback;
        }

        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _mtaThread;
        private PendingQuery? _latestQuery;
        private volatile bool _disposed;

        public static UiaGestureDetector Shared { get; } = new UiaGestureDetector();

        private UiaGestureDetector()
        {
            _mtaThread = new Thread(WorkerLoop)
            {
                Name = "LeafTab.UIA.MTA",
                IsBackground = true
            };
            _mtaThread.SetApartmentState(ApartmentState.MTA);
            _mtaThread.Start();
        }

        public void EnqueueGestureQuery(
            GestureRequestKind kind,
            IntPtr directUiHwnd,
            POINT screenPoint,
            int viewGeneration,
            Action<UiaHitResult, int> onCompleted)
        {
            if (_disposed || directUiHwnd == IntPtr.Zero)
                return;

            var query = new PendingQuery
            {
                Kind = kind,
                DirectUiHwnd = directUiHwnd,
                ScreenPoint = screenPoint,
                ViewGeneration = viewGeneration,
                TimestampTicks = Stopwatch.GetTimestamp(),
                Callback = onCompleted
            };

            Interlocked.Exchange(ref _latestQuery, query);
            _signal.Set();
        }

        /// <summary>
        /// Direct MTA probe helper used by automated self-tests (must never be called synchronously on the UI thread
        /// without pumping messages; self-test calls it via Task.Run on ThreadPool MTA while UI thread pumps normally).
        /// </summary>
        public UiaHitResult ProbePointFromMta(GestureRequestKind kind, IntPtr directUiHwnd, POINT screenPoint)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                throw new InvalidOperationException("UIA queries must not be invoked synchronously on an STA thread.");

            long start = Stopwatch.GetTimestamp();
            IUIAutomation? uia = CreateUiaInstance();
            if (uia == null)
                return new UiaHitResult { Outcome = UiaHitOutcome.IgnoredOrAmbiguous };

            try
            {
                uia.get_RawViewWalker(out IUIAutomationTreeWalker? walker);
                try
                {
                    return EvaluatePoint(uia, walker, kind, directUiHwnd, screenPoint, start);
                }
                finally
                {
                    if (walker != null) Marshal.ReleaseComObject(walker);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(uia);
            }
        }

        private void WorkerLoop()
        {
            IUIAutomation? uia = CreateUiaInstance();
            IUIAutomationTreeWalker? walker = null;
            uia?.get_RawViewWalker(out walker);

            try
            {
                while (!_disposed)
                {
                    _signal.WaitOne();
                    if (_disposed) break;

                    PendingQuery? query = Interlocked.Exchange(ref _latestQuery, null);
                    if (query == null || uia == null)
                        continue;

                    double queuedAgeMs = GetElapsedMs(query.TimestampTicks);
                    if (queuedAgeMs > MaxAllowedGestureAgeMs || !NativeMethods.IsWindow(query.DirectUiHwnd))
                        continue;

                    UiaHitResult result = EvaluatePoint(
                        uia,
                        walker,
                        query.Kind,
                        query.DirectUiHwnd,
                        query.ScreenPoint,
                        query.TimestampTicks);

                    if (result.ElapsedMs <= MaxAllowedGestureAgeMs && result.Outcome != UiaHitOutcome.IgnoredOrAmbiguous)
                    {
                        query.Callback?.Invoke(result, query.ViewGeneration);
                    }
                }
            }
            finally
            {
                if (walker != null) Marshal.ReleaseComObject(walker);
                if (uia != null) Marshal.ReleaseComObject(uia);
            }
        }

        private static UiaHitResult EvaluatePoint(
            IUIAutomation uia,
            IUIAutomationTreeWalker? walker,
            GestureRequestKind kind,
            IntPtr directUiHwnd,
            POINT ptScreen,
            long startTicks)
        {
            var res = new UiaHitResult { Outcome = UiaHitOutcome.IgnoredOrAmbiguous };

            try
            {
                if (!NativeMethods.GetWindowRect(directUiHwnd, out RECT viewRect) || !viewRect.Contains(ptScreen))
                {
                    res.ElapsedMs = GetElapsedMs(startTicks);
                    return res;
                }

                int hr = uia.ElementFromPoint(ptScreen, out IUIAutomationElement? hitElement);
                if (hr != ShellConstants.S_OK || hitElement == null)
                {
                    res.ElapsedMs = GetElapsedMs(startTicks);
                    return res;
                }

                try
                {
                    hitElement.get_CurrentClassName(out string? leafClass);
                    res.RawLeafClassName = leafClass ?? string.Empty;

                    if (kind == GestureRequestKind.DoubleClickBlank)
                    {
                        // Strict safety rule: ONLY treat as blank area if the leaf element itself is "UIItemsView".
                        // This automatically excludes UIItem, UIProperty, UIImage, UIGroupItem, UIColumnHeader, and ScrollBars.
                        if (string.Equals(res.RawLeafClassName, "UIItemsView", StringComparison.Ordinal))
                        {
                            res.Outcome = UiaHitOutcome.HitBlankView;
                        }
                        res.ElapsedMs = GetElapsedMs(startTicks);
                        return res;
                    }

                    // MiddleClickFolder: walk up from leaf (e.g. UIImage / UIProperty) up to UIItem (max 6 hops)
                    IUIAutomationElement? current = hitElement;
                    bool ownsCurrent = false;

                    try
                    {
                        for (int depth = 0; depth < 6 && current != null; depth++)
                        {
                            current.get_CurrentClassName(out string? cls);
                            if (string.Equals(cls, "UIItem", StringComparison.Ordinal))
                            {
                                current.get_CurrentBoundingRectangle(out RECT itemBounds);
                                if (itemBounds.Width > 0 && itemBounds.Height > 0 && itemBounds.Contains(ptScreen))
                                {
                                    current.get_CurrentName(out string? name);
                                    current.get_CurrentAutomationId(out string? autoId);

                                    int posInSet = -1;
                                    if (current.GetCurrentPropertyValue(UIA_PositionInSetPropertyId, out object? posObj) == ShellConstants.S_OK &&
                                        posObj is int p && p > 0)
                                    {
                                        posInSet = p;
                                    }

                                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(autoId))
                                    {
                                        res.Outcome = UiaHitOutcome.HitItem;
                                        res.ItemName = name ?? string.Empty;
                                        res.ItemAutomationId = autoId ?? string.Empty;
                                        res.PositionInSet = posInSet;
                                    }
                                }
                                break;
                            }

                            if (string.Equals(cls, "UIItemsView", StringComparison.Ordinal) ||
                                string.Equals(cls, "DirectUIHWND", StringComparison.Ordinal) ||
                                walker == null)
                            {
                                break;
                            }

                            int phr = walker.GetParentElement(current, out IUIAutomationElement? parent);
                            if (ownsCurrent)
                            {
                                Marshal.ReleaseComObject(current);
                                ownsCurrent = false;
                            }

                            if (phr != ShellConstants.S_OK || parent == null)
                            {
                                current = null;
                                break;
                            }

                            current = parent;
                            ownsCurrent = true;
                        }
                    }
                    finally
                    {
                        if (ownsCurrent && current != null)
                        {
                            Marshal.ReleaseComObject(current);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(hitElement);
                }
            }
            catch
            {
                res.Outcome = UiaHitOutcome.IgnoredOrAmbiguous;
            }

            res.ElapsedMs = GetElapsedMs(startTicks);
            return res;
        }

        private static IUIAutomation? CreateUiaInstance()
        {
            try
            {
                Type? t8 = Type.GetTypeFromCLSID(CLSID_CUIAutomation8, throwOnError: false);
                if (t8 != null && Activator.CreateInstance(t8) is IUIAutomation uia8)
                    return uia8;
            }
            catch
            {
                // Fallback to classic CLSID_CUIAutomation
            }

            try
            {
                Type? t = Type.GetTypeFromCLSID(CLSID_CUIAutomation, throwOnError: false);
                if (t != null && Activator.CreateInstance(t) is IUIAutomation uia)
                    return uia;
            }
            catch
            {
            }

            return null;
        }

        private static double GetElapsedMs(long startTicks) =>
            (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _signal.Set();
        }
    }
}
