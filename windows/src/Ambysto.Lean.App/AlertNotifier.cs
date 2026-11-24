using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.UI.Notifications;

namespace Ambysto.Lean.App;

/// <summary>The low-disk alert's notice (docs/spec/06 Windows note; plan group 13). A Windows notification through the
/// app id the installer gives the Start Menu shortcut (<c>System.AppUserModel.ID</c>), shown with the classic notifier,
/// which writes nothing at run time; when notifications are off for the app, or there is no installed shortcut (a
/// development build), an overlay on the taskbar button instead, cleared when the window is next activated. Informs only.</summary>
internal static partial class AlertNotifier
{
    /// <summary>The app id the installer writes on the shortcut (windows/installer/Package.wxs).</summary>
    internal const string AppId = "Ambysto.Lean";

    private const int SiidWarning = 78;
    private const uint ShgsiIcon = 0x100;
    private const uint ShgsiSmallIcon = 0x1;
    private const uint ClsctxInprocServer = 1;
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    /// <summary>Gives this process the shortcut's app id, so its window groups with the shortcut and its notifications
    /// carry the app's name. Changes nothing outside the process.</summary>
    internal static void UseAppId() => _ = SetCurrentProcessExplicitAppUserModelID(AppId);

    /// <summary>Shows a notification; false when Windows will not show one for this app (off, or no identity).</summary>
    internal static bool TryNotify(string title, string body)
    {
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                return false;
            }

            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(body));
            notifier.Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Puts the stock warning icon on the window's taskbar button, with <paramref name="description"/> for
    /// screen readers; false when the taskbar cannot be reached.</summary>
    internal static bool ShowOverlay(nint window, string description) => SetOverlay(window, description, show: true);

    /// <summary>Removes the overlay, if any.</summary>
    internal static void ClearOverlay(nint window) => _ = SetOverlay(window, null, show: false);

    private static unsafe bool SetOverlay(nint window, string? description, bool show)
    {
        if (CoCreateInstance(in ClsidTaskbarList, 0, ClsctxInprocServer, in IidTaskbarList3, out var pointer) < 0)
        {
            return false;
        }

        var wrappers = new StrategyBasedComWrappers();
        var taskbar = (ITaskbarList3)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        Marshal.Release(pointer);
        nint icon = 0;
        try
        {
            if (taskbar.HrInit() < 0)
            {
                return false;
            }

            if (show)
            {
                var info = new StockIconInfo { Size = (uint)sizeof(StockIconInfo) };
                if (SHGetStockIconInfo(SiidWarning, ShgsiIcon | ShgsiSmallIcon, ref info) < 0)
                {
                    return false;
                }

                icon = info.Icon;
            }

            return taskbar.SetOverlayIcon(window, icon, description) >= 0;
        }
        finally
        {
            // The taskbar keeps its own copy of the icon.
            if (icon != 0)
            {
                _ = DestroyIcon(icon);
            }

            ((ComObject)(object)taskbar).FinalRelease();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct StockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SystemImageIndex;
        public int IconIndex;
        public fixed char Path[260];
    }

    // The vtable order of shobjidl_core.h (ITaskbarList, ITaskbarList2, ITaskbarList3); only the members used are typed.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    internal partial interface ITaskbarList3
    {
        [PreserveSig] int HrInit();

        [PreserveSig] int AddTab(nint window);

        [PreserveSig] int DeleteTab(nint window);

        [PreserveSig] int ActivateTab(nint window);

        [PreserveSig] int SetActiveAlt(nint window);

        [PreserveSig] int MarkFullscreenWindow(nint window, int fullscreen);

        [PreserveSig] int SetProgressValue(nint window, ulong completed, ulong total);

        [PreserveSig] int SetProgressState(nint window, int flags);

        [PreserveSig] int RegisterTab(nint tab, nint mdi);

        [PreserveSig] int UnregisterTab(nint tab);

        [PreserveSig] int SetTabOrder(nint tab, nint insertBefore);

        [PreserveSig] int SetTabActive(nint tab, nint mdi, uint reserved);

        [PreserveSig] int ThumbBarAddButtons(nint window, uint count, nint buttons);

        [PreserveSig] int ThumbBarUpdateButtons(nint window, uint count, nint buttons);

        [PreserveSig] int ThumbBarSetImageList(nint window, nint imageList);

        [PreserveSig] int SetOverlayIcon(nint window, nint icon, string? description);

        [PreserveSig] int SetThumbnailTooltip(nint window, string? tip);

        [PreserveSig] int SetThumbnailClip(nint window, nint clip);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(int stockIcon, uint flags, ref StockIconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
