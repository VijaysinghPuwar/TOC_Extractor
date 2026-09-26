using System.Runtime.InteropServices;

namespace TocExtractor.Desktop.Services;

/// <summary>The Windows way to say the person is needed: the taskbar button flashes until they look.</summary>
/// <remarks>
/// A Mac shows a notification. Windows has no equivalent a plain desktop app
/// can raise without registering itself, and the flashing taskbar button is
/// what Windows people already read as "this window wants you".
/// </remarks>
internal static class WindowsAttention
{
    private const uint FlashAll = 0x3;
    private const uint FlashUntilForeground = 0xC;

    public static void Flash(nint window)
    {
        if (!OperatingSystem.IsWindows() || window == 0)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    // The project has no unsafe code, which the source-generated form needs.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
#pragma warning restore SYSLIB1054
}
