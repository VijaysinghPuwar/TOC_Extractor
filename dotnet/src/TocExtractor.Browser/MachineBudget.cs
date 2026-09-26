using System.Globalization;
using System.Runtime.InteropServices;

namespace TocExtractor.Browser;

/// <summary>How much of this computer the browser may use, worked out from the computer itself.</summary>
/// <remarks>
/// <para>
/// Every browser tab costs memory, and the app used to allow the same 48 on
/// any machine. That is comfortable on a large Mac and enough to push an 8 GB
/// one into swap, where the window stops answering. So the ceiling now comes
/// from the machine: its memory and its cores. A large Mac keeps the full 48;
/// a small one gets fewer, and work waits for a free tab instead.
/// </para>
/// <para>
/// The ceiling is only half of it. What else the person is running changes
/// by the minute, so before opening another tab the browser also asks how
/// much memory is free right now, and while that is low it reuses the tabs it
/// has rather than open more, and closes the idle ones.
/// </para>
/// </remarks>
public static partial class MachineBudget
{
    /// <summary>The most tabs on any machine, however large.</summary>
    public const int MostTabsAnywhere = 48;

    /// <summary>The fewest tabs the ceiling allows, so a small machine still runs several books.</summary>
    public const int FewestTabs = 4;

    /// <summary>Below this share of memory free, no new tabs are opened.</summary>
    public const int TightBelowPercent = 20;

    /// <summary>Memory left for the system and the person's other apps before any tabs.</summary>
    private const double ReservedGigabytes = 4;

    /// <summary>Tabs allowed per gigabyte above the reserve. Measured: a working tab is 150 to 400 MB.</summary>
    private const double TabsPerGigabyte = 2;

    /// <summary>Tabs allowed per core. Past this, tabs only queue for the processor.</summary>
    private const int TabsPerCore = 4;

    /// <summary>This machine's physical memory, in bytes, or 0 if it cannot be read.</summary>
    public static long MemoryBytes { get; } = ReadMemoryBytes();

    /// <summary>This machine's logical processors.</summary>
    public static int Cores { get; } = Environment.ProcessorCount;

    /// <summary>The most tabs this machine should have open at once.</summary>
    public static int MostTabs { get; } = MostTabsFor(MemoryBytes, Cores);

    /// <summary>
    /// A read of free memory, replaceable in tests. Returns the share of
    /// memory free, 0 to 100, or null where the system does not say.
    /// </summary>
    internal static Func<int?> FreePercentReader { get; set; } = ReadFreePercent;

    /// <summary>One line for the log: what this machine has and what the app will use of it.</summary>
    public static string Describe()
    {
        var machine = string.Create(
            CultureInfo.InvariantCulture,
            $"{MemoryBytes / (1024.0 * 1024 * 1024):0.#} GB memory, {Cores} cores, up to {MostTabs} browser tabs");
        return FreeMemoryPercent() is { } free
            ? machine + string.Create(CultureInfo.InvariantCulture, $", {free}% memory free now")
            : machine;
    }

    /// <summary>The tab ceiling for a machine with this much memory and this many cores.</summary>
    public static int MostTabsFor(long memoryBytes, int cores)
    {
        if (memoryBytes <= 0)
        {
            // Unknown memory: the cores alone, never more than before.
            return Math.Clamp(cores * TabsPerCore, FewestTabs, MostTabsAnywhere);
        }

        var gigabytes = memoryBytes / (1024.0 * 1024 * 1024);
        var byMemory = (int)((gigabytes - ReservedGigabytes) * TabsPerGigabyte);
        var byCores = Math.Max(1, cores) * TabsPerCore;
        return Math.Clamp(Math.Min(byMemory, byCores), FewestTabs, MostTabsAnywhere);
    }

    /// <summary>The share of memory free right now, 0 to 100, or null where the system does not say.</summary>
    public static int? FreeMemoryPercent()
    {
        try
        {
            return FreePercentReader() is { } free ? Math.Clamp(free, 0, 100) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether memory is short enough right now that no more tabs should open.</summary>
    /// <remarks>Unknown counts as plenty, so a system that does not say behaves as before.</remarks>
    public static bool MemoryTight() => FreeMemoryPercent() is { } free && free < TightBelowPercent;

    private static long ReadMemoryBytes()
    {
        // For testing on a large machine how the app behaves on a small one.
        if (double.TryParse(Environment.GetEnvironmentVariable("TOC_SIMULATE_MEMORY_GB"), NumberStyles.Float, CultureInfo.InvariantCulture, out var simulated) && simulated > 0)
        {
            return (long)(simulated * 1024 * 1024 * 1024);
        }

        if (OperatingSystem.IsMacOS() && SysctlLong("hw.memsize") is { } bytes and > 0)
        {
            return bytes;
        }

        // Elsewhere the runtime knows the machine's memory once it has
        // collected once; before that it can report 0.
        var reported = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (reported <= 0)
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            reported = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }

        return reported;
    }

    private static int? ReadFreePercent()
    {
        if (OperatingSystem.IsMacOS())
        {
            // The kernel's own measure of memory pressure: the share of memory
            // it could hand out without compressing or swapping. This is what
            // Activity Monitor's pressure graph follows.
            return SysctlInt("kern.memorystatus_level");
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxFreePercent();
        }

        // Windows: not read yet, so memory is never counted as tight there
        // and tabs are limited by the machine's size alone.
        return null;
    }

    private static int? LinuxFreePercent()
    {
        long? total = null;
        long? available = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = Kilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = Kilobytes(line);
            }

            if (total is not null && available is not null)
            {
                break;
            }
        }

        return total is > 0 && available is { } free ? (int)(100 * free / total.Value) : null;

        static long? Kilobytes(string line) =>
            long.TryParse(line.AsSpan(line.IndexOf(':') + 1).Trim().TrimEnd("kB").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static long? SysctlLong(string name)
    {
        long value = 0;
        nint size = sizeof(long);
        return Sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0) == 0 ? value : null;
    }

    private static int? SysctlInt(string name)
    {
        int value = 0;
        nint size = sizeof(int);
        return Sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0) == 0 ? value : null;
    }

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Sysctlbyname(string name, ref long value, ref nint size, IntPtr newValue, nint newSize);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Sysctlbyname(string name, ref int value, ref nint size, IntPtr newValue, nint newSize);
}
