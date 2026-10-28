using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BatteryDiagnostic;

/// <summary>
/// Optional native PDH fallback for the Windows WDDM GPU performance providers.
/// Called by the existing background sampler. It retains the query between calls;
/// engine rate counters are never manufactured from a single sample.
/// </summary>
internal static class PerformanceGpuCounters
{
    const uint MoreData = 0x800007d2, FmtDouble = 0x200, FmtLarge = 0x400;
    const uint NoScale = 0x1000, NoCap100 = 0x8000, RefreshCounters = 4;
    const int MaxBufferBytes = 8 * 1024 * 1024, MaxPaths = 16384;
    static readonly object Gate = new();
    static readonly Regex Engine = new(@"(?:^|_)phys_(\d+)_eng_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static IntPtr query;
    static long retryAfter;
    static long previousCollection;
    static string diagnostic="PDH:NotSampled";
    internal static string Diagnostic=>Volatile.Read(ref diagnostic);
    static readonly Family Dedicated = new(@"\GPU Adapter Memory(*)\Dedicated Usage", false);
    static readonly Family Shared = new(@"\GPU Adapter Memory(*)\Shared Usage", false);
    static readonly Family Engines = new(@"\GPU Engine(*)\Utilization Percentage", true);
    static readonly Family[] Families = [Dedicated, Shared, Engines];

    sealed class Family(string englishPath, bool rate)
    {
        internal readonly string EnglishPath = englishPath;
        internal readonly bool Rate = rate;
        internal string? LocalizedPath;
        internal readonly Dictionary<string, Counter> Counters = new(StringComparer.OrdinalIgnoreCase);
        internal bool Complete;
    }

    sealed class Counter(IntPtr handle)
    {
        internal readonly IntPtr Handle = handle;
        internal int Collections;
    }

    internal readonly record struct Reading(string Name, double? Percent, ulong? Bytes);

    internal static GpuLive[]? Read(IReadOnlyList<GpuInventory> adapters)
    {
        if (adapters.Count == 0 || !OperatingSystem.IsWindows()) return null;
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (now < retryAfter) return null;
            try
            {
                if (query == IntPtr.Zero && PdhOpenQueryW(null, UIntPtr.Zero, out query) != 0) return BackOff(now);
                foreach (var family in Families) Refresh(family);
                if (!Families.Any(f => f.Complete && f.Counters.Count > 0)) return null;
                // Do not describe an interval containing a long suspension as current load.
                if (previousCollection != 0 && now - previousCollection > 30000)
                    foreach (var counter in Families.SelectMany(f => f.Counters.Values)) counter.Collections = 0;
                uint collected=PdhCollectQueryData(query);
                if (collected != 0)
                {
                    Volatile.Write(ref diagnostic,"PDH:0x"+collected.ToString("X8"));
                    foreach (var counter in Families.SelectMany(f => f.Counters.Values)) counter.Collections = 0;
                    return null;
                }
                previousCollection = now;
                foreach (var counter in Families.SelectMany(f => f.Counters.Values))
                    counter.Collections = Math.Min(counter.Collections + 1, 2);
                Reading[]? dedicated = Values(Dedicated), shared = Values(Shared), engines = Values(Engines);
                if (dedicated == null && shared == null && engines == null) return null;
                Volatile.Write(ref diagnostic,"PDH:Available");
                return adapters.Select(adapter =>
                {
                    string? id = PerformanceSensors.CounterGpuId(adapter.Id);
                    ulong? d = SumBytes(dedicated, id), s = SumBytes(shared, id);
                    if (adapter.DedicatedMemoryBytes is { } limit && d > limit) d = null;
                    if (adapter.SharedMemoryBytes is { } sharedLimit && s > sharedLimit) s = null;
                    return new GpuLive(adapter.Id, BusiestEngine(engines, id), d, s, null, null);
                }).ToArray();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                or System.ComponentModel.Win32Exception or OverflowException or ArgumentException)
            { return BackOff(now); }
        }
    }

    static GpuLive[]? BackOff(long now)
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
        previousCollection = 0;
        foreach (var family in Families)
        {
            family.Counters.Clear(); family.LocalizedPath = null; family.Complete = false;
        }
        retryAfter = now + 30000;
        return null;
    }

    static void Refresh(Family family)
    {
        family.Complete = false;
        family.LocalizedPath ??= Localize(family.EnglishPath);
        if (family.LocalizedPath == null) return;
        string[]? paths = Expand(family.LocalizedPath);
        if (paths == null) return;
        var current = paths.Where(path => PerformanceSensors.CounterGpuId(path) != null
            && (family.Rate || !path.Contains("pid_", StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string removed in family.Counters.Keys.Where(key => !current.Contains(key)).ToArray())
        {
            PdhRemoveCounter(family.Counters[removed].Handle);
            family.Counters.Remove(removed);
        }
        bool complete = true;
        foreach (string path in current)
        {
            if (family.Counters.ContainsKey(path)) continue;
            if (PdhAddCounterW(query, path, UIntPtr.Zero, out IntPtr handle) == 0)
                family.Counters[path] = new(handle);
            else complete = false;
        }
        family.Complete = complete;
    }

    // Microsoft documents this localization + expansion sequence for English wildcards.
    // CounterInfoPrefix deliberately ends at szFullPath: the trailing union is not read.
    static string? Localize(string englishPath)
    {
        uint status=PdhAddEnglishCounterW(query, englishPath, UIntPtr.Zero, out IntPtr template);
        if (status != 0){Volatile.Write(ref diagnostic,"PDH:0x"+status.ToString("X8"));return null;}
        try
        {
            uint bytes = 0;
            if (PdhGetCounterInfoW(template, false, ref bytes, IntPtr.Zero) != MoreData
                || bytes < Marshal.SizeOf<CounterInfoPrefix>() || bytes > MaxBufferBytes) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint capacity = bytes;
                if (PdhGetCounterInfoW(template, false, ref bytes, buffer) != 0 || bytes > capacity) return null;
                IntPtr path = Marshal.PtrToStructure<CounterInfoPrefix>(buffer).FullPath;
                return ReadBoundedString(buffer, (int)capacity, path);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { PdhRemoveCounter(template); }
    }

    static string[]? Expand(string path)
    {
        // Instances change while querying. Requery the required length after MORE_DATA;
        // the failed nonzero-size call's returned length is not reliable per PDH docs.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint characters = 0;
            uint status = PdhExpandWildCardPathW(null, path, IntPtr.Zero, ref characters, RefreshCounters);
            if (status == 0 && characters == 0) return [];
            if (status != MoreData || characters is < 2 || characters > MaxBufferBytes / 2) return null;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)characters * 2));
            try
            {
                uint capacity = characters;
                status = PdhExpandWildCardPathW(null, path, buffer, ref characters, RefreshCounters);
                if (status == MoreData) continue;
                if (status != 0 || characters > capacity) return null;
                char[] data = new char[characters];
                Marshal.Copy(buffer, data, 0, data.Length);
                return ParseMultiString(data);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    static Reading[]? Values(Family family)
    {
        if (!family.Complete) return null;
        var result = new List<Reading>();
        foreach (var entry in family.Counters)
        {
            uint format = (family.Rate ? FmtDouble | NoCap100 : FmtLarge) | NoScale;
            double? percent = null;
            ulong? bytes = null;
            if ((!family.Rate || entry.Value.Collections >= 2)
                && PdhGetFormattedCounterValue(entry.Value.Handle, format, IntPtr.Zero, out var value) == 0
                && value.Status is 0 or 1)
            {
                if (family.Rate && double.IsFinite(value.Double) && value.Double is >= 0 and <= 100)
                    percent = value.Double;
                if (!family.Rate && value.Large >= 0) bytes = (ulong)value.Large;
            }
            result.Add(new(entry.Key, percent, bytes));
        }
        return result.ToArray();
    }

    internal static ulong? SumBytes(IReadOnlyList<Reading>? readings, string? id)
    {
        if (readings == null || id == null) return null;
        var matching = readings.Where(row => PerformanceSensors.CounterGpuId(row.Name) == id).ToArray();
        if (matching.Length == 0) return null;
        ulong sum = 0;
        foreach (var row in matching)
        {
            if (row.Bytes is not { } bytes || ulong.MaxValue - sum < bytes) return null;
            sum += bytes;
        }
        return sum;
    }

    internal static double? BusiestEngine(IReadOnlyList<Reading>? readings, string? id)
    {
        if (readings == null || id == null) return null;
        var engines = new Dictionary<string, double>();
        foreach (var row in readings.Where(row => PerformanceSensors.CounterGpuId(row.Name) == id))
        {
            var match = Engine.Match(row.Name);
            if (!match.Success || row.Percent is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100)
                return null; // Partial process lists cannot certify an adapter-wide total.
            string key = match.Groups[1].Value + ":" + match.Groups[2].Value;
            engines[key] = engines.GetValueOrDefault(key) + percent;
        }
        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    internal static string[]? ParseMultiString(char[] data)
    {
        var paths = new List<string>();
        int start = 0;
        for (int offset = 0; offset < data.Length; offset++)
        {
            if (data[offset] != '\0') continue;
            if (offset == start) return paths.ToArray();
            if (paths.Count >= MaxPaths) return null;
            paths.Add(new string(data, start, offset - start));
            start = offset + 1;
        }
        return null; // Missing double terminator: never accept a truncated instance list.
    }

    static string? ReadBoundedString(IntPtr buffer, int bytes, IntPtr pointer)
    {
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= bytes || (offset & 1) != 0) return null;
        int length = 0, capacity = (bytes - (int)offset) / 2;
        while (length < capacity && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
        return length == capacity ? null : Marshal.PtrToStringUni(pointer, length);
    }

    internal static void SelfTest()
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("GPU PDH self-test: " + message); }
        const string id = "luid_0x00000000_0x0000000a";
        Check(ParseMultiString("one\0two\0\0".ToCharArray())?.SequenceEqual(new[] { "one", "two" }) == true, "multistring paths");
        Check(ParseMultiString("one\0two".ToCharArray()) == null && ParseMultiString("one\0".ToCharArray()) == null, "truncated instances");
        Check(SumBytes([new("luid_0x0_0xa_phys_0", null, 10), new("luid_0x0_0xa_phys_1", null, 20)], id) == 30, "adapter physical nodes");
        Check(SumBytes([new("luid_0x0_0xa_phys_0", null, null)], id) == null, "missing bytes are unknown");
        Check(SumBytes([new("luid_0x0_0xa_phys_0", null, ulong.MaxValue), new("luid_0x0_0xa_phys_1", null, 1)], id) == null, "overflow rejected");
        Check(BusiestEngine([
            new("pid_1_luid_0x0_0xa_phys_0_eng_0_engtype_3D", 30, null),
            new("pid_2_luid_0x0_0xa_phys_0_eng_0_engtype_3D", 25, null),
            new("pid_2_luid_0x0_0xa_phys_1_eng_0_engtype_3D", 40, null),
            new("pid_2_luid_0x0_0xb_phys_0_eng_0_engtype_3D", 90, null)], id) == 55, "sum processes, max physical engine, match LUID");
        Check(BusiestEngine([new("pid_1_luid_0x0_0xa_phys_0_eng_0", null, null)], id) == null, "rate warmup is unknown");
        Check(Marshal.SizeOf<FormattedValue>() == 16 && Marshal.OffsetOf<FormattedValue>(nameof(FormattedValue.Large)).ToInt32() == 8, "native union layout");
        Check(Marshal.OffsetOf<CounterInfoPrefix>(nameof(CounterInfoPrefix.FullPath)).ToInt32() == 24 + 2 * IntPtr.Size, "native counter-info pointer layout");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterInfoPrefix
    {
        public uint Length, Type, Version, Status;
        public int Scale, DefaultScale;
        public UIntPtr UserData, QueryUserData;
        public IntPtr FullPath;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct FormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public long Large;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)] static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)] static extern uint PdhRemoveCounter(IntPtr counter);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhAddCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhGetCounterInfoW(IntPtr counter, [MarshalAs(UnmanagedType.Bool)] bool explain, ref uint bytes, IntPtr buffer);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhExpandWildCardPathW(string? source, string path, IntPtr buffer, ref uint characters, uint flags);
    [DllImport("pdh.dll", ExactSpelling = true)] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FormattedValue value);
}
