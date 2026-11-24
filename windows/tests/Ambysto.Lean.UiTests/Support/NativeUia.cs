using System;
using System.Runtime.InteropServices;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>The few native UI Automation calls the managed client lacks (ASP-14): the LiveSetting property and the
/// LiveRegionChanged event, which System.Windows.Automation cannot look up, of an element found by its AutomationId,
/// through the COM client <c>CUIAutomation</c>. Read only.</summary>
internal static class NativeUia
{
    private const int AutomationIdPropertyId = 30011;
    private const int LiveSettingPropertyId = 30135;
    private const int DescendantsScope = 4;
    private const int SubtreeScope = 7;
    private const int LiveRegionChangedEventId = 20024;

    /// <summary>The LiveSetting (0 off, 1 polite, 2 assertive) of the first element with <paramref name="automationId"/>
    /// below the window <paramref name="window"/>, or null when there is none.</summary>
    public static int? LiveSettingOf(nint window, string automationId)
    {
        var automation = (IUIAutomation)new CUIAutomation();
        Marshal.ThrowExceptionForHR(automation.ElementFromHandle(window, out var root));
        Marshal.ThrowExceptionForHR(automation.CreatePropertyCondition(AutomationIdPropertyId, automationId, out var condition));
        try
        {
            Marshal.ThrowExceptionForHR(root.FindFirst(DescendantsScope, condition, out var found));
            return found?.GetCurrentPropertyValue(LiveSettingPropertyId) is { } live ? Convert.ToInt32(live, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        finally
        {
            Marshal.Release(condition);
        }
    }

    /// <summary>How many LiveRegionChanged events (20024) the element with <paramref name="automationId"/> below the window
    /// <paramref name="window"/>, or anything in it, raises while <paramref name="watch"/> runs; null when there is no
    /// such element or UI Automation refuses the handler (then the event cannot be observed here).</summary>
    public static int? LiveRegionChanges(nint window, string automationId, Action watch) => Events(window, automationId, LiveRegionChangedEventId, watch);

    /// <summary>How many LiveRegionChanged events (20024) the element with <paramref name="automationId"/> raises while
    /// <paramref name="watch"/> runs, counted from a handler on the window itself (the whole subtree), so that an element that
    /// is not in the tree yet when the watch starts (the collapsed progress row before a refusal) is still heard; the events
    /// are told apart by the sender's AutomationId. Null when UI Automation refuses the handler.</summary>
    public static int? LiveRegionChangesAnywhere(nint window, string automationId, Action watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        var automation = (IUIAutomation)new CUIAutomation();
        Marshal.ThrowExceptionForHR(automation.ElementFromHandle(window, out var root));
        var counter = new SenderCounter(automationId);
        if (automation.AddAutomationEventHandler(LiveRegionChangedEventId, root, SubtreeScope, nint.Zero, counter) < 0)
        {
            return null;
        }

        try
        {
            watch();
        }
        finally
        {
            automation.RemoveAutomationEventHandler(LiveRegionChangedEventId, root, counter);
        }

        return counter.Count;
    }

    /// <summary>How many events <paramref name="eventId"/> the element with <paramref name="automationId"/> below the window
    /// <paramref name="window"/>, or anything in it, raises while <paramref name="watch"/> runs; null as for
    /// <see cref="LiveRegionChanges"/>.</summary>
    public static int? Events(nint window, string automationId, int eventId, Action watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        var automation = (IUIAutomation)new CUIAutomation();
        Marshal.ThrowExceptionForHR(automation.ElementFromHandle(window, out var root));
        Marshal.ThrowExceptionForHR(automation.CreatePropertyCondition(AutomationIdPropertyId, automationId, out var condition));
        IUIAutomationElement? found;
        try
        {
            Marshal.ThrowExceptionForHR(root.FindFirst(DescendantsScope, condition, out found));
        }
        finally
        {
            Marshal.Release(condition);
        }

        if (found is null)
        {
            return null;
        }

        var counter = new Counter();
        if (automation.AddAutomationEventHandler(eventId, found, SubtreeScope, nint.Zero, counter) < 0)
        {
            return null;
        }

        try
        {
            watch();
        }
        finally
        {
            automation.RemoveAutomationEventHandler(eventId, found, counter);
        }

        return counter.Count;
    }

    [ComImport]
    [Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    private class CUIAutomation
    {
    }

    // IUIAutomation, up to CreatePropertyCondition (vtable order of UIAutomationClient.h).
    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        [PreserveSig]
        int ElementFromHandle(nint window, out IUIAutomationElement element);

        void ElementFromPoint();

        void GetFocusedElement();

        void GetRootElementBuildCache();

        void ElementFromHandleBuildCache();

        void ElementFromPointBuildCache();

        void GetFocusedElementBuildCache();

        void CreateTreeWalker();

        void GetControlViewWalker();

        void GetContentViewWalker();

        void GetRawViewWalker();

        void GetRawViewCondition();

        void GetControlViewCondition();

        void GetContentViewCondition();

        void CreateCacheRequest();

        void CreateTrueCondition();

        void CreateFalseCondition();

        [PreserveSig]
        int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out nint condition);

        void CreatePropertyConditionEx();

        void CreateAndCondition();

        void CreateAndConditionFromArray();

        void CreateAndConditionFromNativeArray();

        void CreateOrCondition();

        void CreateOrConditionFromArray();

        void CreateOrConditionFromNativeArray();

        void CreateNotCondition();

        [PreserveSig]
        int AddAutomationEventHandler(int eventId, IUIAutomationElement element, int scope, nint cacheRequest, IUIAutomationEventHandler handler);

        [PreserveSig]
        int RemoveAutomationEventHandler(int eventId, IUIAutomationElement element, IUIAutomationEventHandler handler);
    }

    // IUIAutomationEventHandler, implemented here so UI Automation can call it back.
    [ComImport]
    [Guid("146C3C17-F12E-4E22-8C27-F894B9B79C69")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationEventHandler
    {
        void HandleAutomationEvent(IUIAutomationElement sender, int eventId);
    }

    /// <summary>Counts the events it receives.</summary>
    [ComVisible(true)]
    private sealed class Counter : IUIAutomationEventHandler
    {
        private int count;

        public int Count => System.Threading.Volatile.Read(ref count);

        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) => System.Threading.Interlocked.Increment(ref count);
    }

    /// <summary>Counts the events whose sender has one AutomationId.</summary>
    [ComVisible(true)]
    private sealed class SenderCounter(string automationId) : IUIAutomationEventHandler
    {
        private int count;

        public int Count => System.Threading.Volatile.Read(ref count);

        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId)
        {
            try
            {
                if (sender.GetCurrentPropertyValue(AutomationIdPropertyId) is string id && string.Equals(id, automationId, StringComparison.Ordinal))
                {
                    System.Threading.Interlocked.Increment(ref count);
                }
            }
            catch (COMException)
            {
            }
        }
    }

    // IUIAutomationElement, up to GetCurrentPropertyValue.
    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        [PreserveSig]
        int FindFirst(int scope, nint condition, out IUIAutomationElement? found);

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
