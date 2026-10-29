using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BatteryDiagnostic;

internal sealed record PerformanceInventory(CpuInventory Cpu, MemoryInventory Memory,
    IReadOnlyList<GpuInventory> Gpus, IReadOnlyList<string> Notes);
internal sealed record CpuInventory(string? Model, int? PhysicalCores, int? LogicalProcessors, uint? MaxClockMHz);
internal enum GpuKind { Unknown, Integrated, Dedicated, Software }
internal sealed record GpuInventory(string Id, string? Model, GpuKind Kind, ulong? DedicatedMemoryBytes,
    ulong? SharedMemoryBytes, string? DriverVersion);
internal sealed record PerformanceLive(DateTimeOffset Timestamp, double? CpuUsagePercent,
    double? CpuFrequencyMHz, double? CpuTemperatureC, MemoryLive Memory,
    IReadOnlyList<GpuLive> Gpus, IReadOnlyList<string> Notes);
internal sealed record MemoryLive(ulong? TotalBytes, ulong? AvailableBytes, ulong? UsedBytes);
internal sealed record GpuLive(string Id, double? UsagePercent, ulong? DedicatedUsedBytes,
    ulong? SharedUsedBytes, double? FrequencyMHz, double? TemperatureC);

/// <summary>
/// Read-only Windows sources. No external utility, network, stress load or ACPI-zone-as-CPU
/// substitution. Optional CPU DTS attaches to an already installed signed driver only
/// after explicit activation. Slow sensor access stays in independent background jobs.
/// </summary>
internal static class PerformanceSensors
{
    static readonly object CpuGate = new();
    static (ulong Idle, ulong Kernel, ulong User)? previousCpu;
    static readonly object GpuGate = new();
    static GpuInventory[] knownGpus = [];
    static GpuLive[] gpuCache = [];
    static long gpuCacheTick;
    static long nextGpuPoll;
    static int gpuPollRunning;
    static readonly Regex LuidPattern = new(@"luid_0x([0-9a-f]{1,8})_0x([0-9a-f]{1,8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static PerformanceInventory ReadInventory()
    {
        var notes = new List<string> {
            "CPU: GetSystemTimes; processor frequency: CallNtPowerInformation, Windows-reported throttle-adjusted MHz, not an independent effective-clock measurement.",
            PerformanceCpuTemperature.SourceDescription,
            ExternalCpuTemperature.SourceDescription,
            "Memory: GetSystemFirmwareTable(RSMB), firmware-declared modules/transfer rates; memory-device count can include soldered memory. DRAM chip manufacturer is not inferred from module brand.",
            "GPU: DXGI adapter descriptor; D3D12 UMA architecture when supported. UMA classification describes the memory architecture; unsupported architecture remains unknown.",
            "GPU memory: native PDH global counters with optional NVIDIA driver/WMI fallback, not process-specific DXGI budgets. PDH load is the busiest engine; NVAPI fallback is the graphics domain and may differ from Task Manager.",
            PerformanceTemperatures.SourceDescription
        };
        CpuInventory cpu;
        try { cpu = ReadCpuInventory(); }
        catch (Exception ex) { cpu = new(null, null, null, null); notes.Add("CPU inventory unavailable: " + ex.GetType().Name); }
        MemoryInventory memory;
        try { memory = ReadMemoryInventory(); }
        catch (Exception ex)
        {
            memory = new([], null);
            notes.Add(ex is System.ComponentModel.Win32Exception native
                ? "Memory firmware inventory unavailable: Windows error " + native.NativeErrorCode
                : "Memory firmware inventory unavailable: " + ex.GetType().Name);
        }
        if(memory.Modules.Count==0||memory.Modules.Any(m=>m.MemoryType is null))
        {
            var fields=new[]{"DeviceLocator","Capacity","ConfiguredClockSpeed","Speed","Manufacturer","PartNumber","SMBIOSMemoryType"};
            var rows=QueryWmi("SELECT "+string.Join(",",fields)+" FROM Win32_PhysicalMemory",fields,4096);
            var modules=rows.Select(row=>
            {
                object? Value(string key)=>row.TryGetValue(key,out var value)?value:null;
                uint? Rate(string key)=>AsUInt64(Value(key)) is >0 and <=1000000 ? (uint)AsUInt64(Value(key))!.Value:null;
                byte? code=AsUInt64(Value("SMBIOSMemoryType")) is {} n&&n<=255?(byte)n:null;
                return new MemoryModule(Clean(Value("DeviceLocator")),AsUInt64(Value("Capacity")),Rate("ConfiguredClockSpeed"),Rate("Speed"),
                    Clean(Value("Manufacturer")),Clean(Value("PartNumber")),null,MemoryType:code.HasValue?MemoryCompatibility.TypeName(code.Value):null);
            }).ToArray();
            memory=MemoryCompatibility.Merge(memory,modules);
            notes.Add("Memory fallback: Win32_PhysicalMemory SMBIOSMemoryType; exact slot/capacity matching; no DDR generation inferred from speed.");
        }
        GpuInventory[] gpus;
        try { gpus = ReadDxgiAdapters(); }
        catch (Exception ex) { gpus = []; notes.Add("DXGI inventory unavailable: " + ex.GetType().Name); }
        lock (GpuGate) { knownGpus = gpus; }
        return new(cpu, memory, gpus, notes);
    }

    internal static PerformanceLive ReadLive()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        double? usage = null;
        try
        {
            // GetSystemTimes is limited to the calling group on >64-processor systems.
            // Avoid presenting a partial group as the whole-machine utilization.
            if (GetActiveProcessorCount(0xffff) <= 64 && GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var current = (idle.Value, kernel.Value, user.Value);
                lock (CpuGate)
                {
                    if (previousCpu is { } previous)
                        usage = CpuUsage(previous.Idle, previous.Kernel, previous.User, current.Item1, current.Item2, current.Item3);
                    previousCpu = current;
                }
            }
        }
        catch { /* Unsupported native source is represented by null, never by zero. */ }
        var memory = new MemoryLive(null, null, null);
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status)) memory = ValidateMemory(status.TotalPhysical, status.AvailablePhysical);
        }
        catch { }
        double? frequency = null;
        try
        {
            var powers = ReadProcessorPower();
            var valid = powers.Where(p => p.CurrentMHz is > 0 and <= 100000).ToArray();
            if (valid.Length != 0) frequency = valid.Average(p => (double)p.CurrentMHz);
        }
        catch { }
        GpuInventory[] adapters;
        GpuLive[] values;
        lock (GpuGate)
        {
            adapters = knownGpus;
            values = Environment.TickCount64 - gpuCacheTick is >= 0 and <= 10000 ? gpuCache : [];
        }
        QueueGpuPoll(adapters);
        var gpus = adapters.Select(g => values.FirstOrDefault(x => x.Id == g.Id)
            ?? new GpuLive(g.Id, null, null, null, null, null)).ToArray();
        var cpuTemperature=PerformanceCpuTemperature.ReadCached();
        var library=LibrarySensors.ReadCached(adapters);
        var external=ExternalCpuTemperature.ReadCached();
        double? cpuReading=cpuTemperature.TemperatureC??library.Cpu??external.TemperatureC;
        string cpuSource=cpuTemperature.TemperatureC.HasValue?cpuTemperature.Source:
            library.Cpu.HasValue?"LibreHardwareMonitor CPU Package / Die":
            external.TemperatureC.HasValue?external.Source:"Unavailable";
        gpus=gpus.Select(g=>{var extra=library.Gpus.FirstOrDefault(x=>x.Id==g.Id);return g with {TemperatureC=g.TemperatureC??extra?.TemperatureC,FrequencyMHz=g.FrequencyMHz??extra?.FrequencyMHz,UsagePercent=g.UsagePercent??extra?.UsagePercent};}).ToArray();
        return new(now, usage, frequency, cpuReading, memory, gpus, [
            "CPU frequency is Windows-reported, not a measured effective clock. GPU temperature and clock are driver-reported when supported.",
            "CPU temperature: "+cpuTemperature.State+"; "+cpuTemperature.Source+"; "+cpuTemperature.Diagnostic,
            "CPU temperature source: "+cpuSource,
            "External CPU sensors: "+external.Source+"; "+external.Diagnostic,
            "RAM available includes reclaimable memory; used = usable physical total minus available.",
            "Optional GPU counters refresh in background; samples older than 10 seconds become unavailable."
            , PerformanceGpuCounters.Diagnostic,LibrarySensors.Diagnostic
        ]);
    }

    static CpuInventory ReadCpuInventory()
    {
        string? model = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            model = Clean(key?.GetValue("ProcessorNameString"));
        int? cores = null, threads = null;
        uint bytes = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref bytes);
        if (bytes is > 0 and <= 1048576)
        {
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (GetLogicalProcessorInformationEx(0, buffer, ref bytes))
                {
                    int offset = 0, coreCount = 0, threadCount = 0;
                    while (offset + 32 <= bytes)
                    {
                        int relation = Marshal.ReadInt32(buffer, offset);
                        int size = Marshal.ReadInt32(buffer, offset + 4);
                        if (size < 32 || (long)offset + size > bytes) break;
                        if (relation == 0)
                        {
                            ushort groups = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                            if (groups == 0 || 32L + groups * 16L > size) break;
                            coreCount++;
                            for (int group = 0; group < groups; group++)
                                threadCount += BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer, offset + 32 + group * 16));
                        }
                        offset += size;
                    }
                    if (offset == bytes && coreCount > 0 && threadCount >= coreCount)
                    { cores = coreCount; threads = threadCount; }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        uint? max = null;
        try
        {
            var powers = ReadProcessorPower().Where(p => p.MaxMHz is > 0 and <= 100000).ToArray();
            if (powers.Length != 0) max = powers.Max(p => p.MaxMHz);
        }
        catch { }
        return new(model, cores, threads, max);
    }

    static ProcessorPower[] ReadProcessorPower()
    {
        uint count = GetActiveProcessorCount(0xffff);
        // This power-information layout has no group identifier. Do not average an
        // ambiguous partial group on large systems.
        if (count is 0 or > 64) return [];
        int size = Marshal.SizeOf<ProcessorPower>();
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * size));
        try
        {
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buffer, checked(count * (uint)size)) != 0) return [];
            var result = new ProcessorPower[count];
            for (int i = 0; i < result.Length; i++) result[i] = Marshal.PtrToStructure<ProcessorPower>(buffer + i * size);
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static MemoryInventory ReadMemoryInventory()
    {
        const uint rsmb = 0x52534d42; // 'RSMB' as required by the Windows firmware API.
        uint size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (size is < 8 or > 2097152) return new([], null);
        byte[] buffer = new byte[size];
        uint read = GetSystemFirmwareTable(rsmb, 0, buffer, size);
        if (read == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (read != size) return new([], null);
        return ParseSmbiosMemory(buffer);
    }

    // RawSMBIOSData header is 8 bytes. Do not retain or expose serial numbers/asset tags.
    internal static MemoryInventory ParseSmbiosMemory(byte[] raw)
    {
        if (raw.Length < 8) return new([], null);
        uint length = BitConverter.ToUInt32(raw, 4);
        if (length > raw.Length - 8) return new([], null);
        int end = (int)length + 8;
        var entries = new List<(int Offset, int Size, string[] Strings)>();
        for (int offset = 8; offset + 4 <= end;)
        {
            int size = raw[offset + 1];
            if (size < 4 || offset + size > end) return new([], null);
            int next = offset + size;
            var strings = new List<string>();
            while (next + 1 < end && !(raw[next] == 0 && raw[next + 1] == 0))
            {
                int start = next;
                while (next < end && raw[next] != 0) next++;
                if (next == end) return new([], null);
                strings.Add(Encoding.ASCII.GetString(raw, start, next - start));
                // The string terminator also starts the terminating double zero after the last string.
                if (next + 1 < end && raw[next + 1] == 0) break;
                next++;
            }
            if (next + 1 >= end || raw[next] != 0 || raw[next + 1] != 0) return new([], null);
            entries.Add((offset, size, strings.ToArray()));
            if (raw[offset] == 127) break;
            offset = next + 2;
        }
        var arrays = new HashSet<ushort>();
        int slots = 0;
        foreach (var entry in entries)
        {
            int o = entry.Offset;
            if (raw[o] != 16 || entry.Size < 15 || raw[o + 5] != 3) continue; // system memory
            arrays.Add(BitConverter.ToUInt16(raw, o + 2));
            int count = BitConverter.ToUInt16(raw, o + 13);
            if (count is > 0 and <= 4096) slots += count;
        }
        var modules = new List<MemoryModule>();
        foreach (var entry in entries)
        {
            int o = entry.Offset;
            if (raw[o] != 17 || entry.Size < 21) continue;
            if (arrays.Count != 0 && !arrays.Contains(BitConverter.ToUInt16(raw, o + 4))) continue;
            ushort size = BitConverter.ToUInt16(raw, o + 12);
            if (size == 0) continue; // explicitly empty slot
            ulong? capacity = null;
            if (size == 0x7fff)
            {
                if (entry.Size >= 32)
                {
                    uint extended = BitConverter.ToUInt32(raw, o + 28) & 0x7fffffff;
                    if (extended > 0) capacity = (ulong)extended * 1024 * 1024;
                }
            }
            else if (size != 0xffff)
                capacity = (ulong)(size & 0x7fff) * ((size & 0x8000) != 0 ? 1024UL : 1048576UL);
            string? S(int field) => field < entry.Size ? SmbiosString(entry.Strings, raw[o + field]) : null;
            uint? Speed(int field, int extendedField)
            {
                if (field + 2 > entry.Size) return null;
                uint value = BitConverter.ToUInt16(raw, o + field);
                if (value == 0xffff) value = extendedField + 4 <= entry.Size ? BitConverter.ToUInt32(raw, o + extendedField) : 0;
                return value is > 0 and <= 1000000 ? value : null;
            }
            // SMBIOS 3.1 clarified these fields as transfer rates (MT/s). Older versions
            // label them MHz, so keep the unit uncertain rather than silently relabeling.
            bool transferUnits = raw[1] > 3 || (raw[1] == 3 && raw[2] >= 1);
            modules.Add(new(S(16) ?? S(17), capacity,
                transferUnits ? Speed(32, 88) : null, transferUnits ? Speed(21, 84) : null,
                S(23), S(26), null, transferUnits ? null : Speed(32, 88), transferUnits ? null : Speed(21, 84), MemoryTypeName(raw[o + 18])));
        }
        int? totalSlots = slots is > 0 and <= 4096 && slots >= modules.Count ? slots : null;
        return new(modules, totalSlots);
    }

    // DMTF DSP0134, Memory Device (Type 17), Memory Type byte at offset 0x12.
    // Do not infer generation from transfer rate: DDR generations overlap.
    static string? MemoryTypeName(byte code) => MemoryCompatibility.TypeName(code);

    static string? SmbiosString(string[] strings, byte index) => index > 0 && index <= strings.Length ? Clean(strings[index - 1]) : null;

    static GpuInventory[] ReadDxgiAdapters()
    {
        IntPtr factory = IntPtr.Zero;
        var result = new List<GpuInventory>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        try
        {
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return [];
            var enumerate = VTable<EnumAdapters1>(factory, 12);
            for (uint index = 0; index < 32; index++)
            {
                int hr = enumerate(factory, index, out IntPtr adapter);
                if (hr < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var desc = new AdapterDesc1();
                    if (VTable<GetDesc1>(adapter, 10)(adapter, out desc) < 0) continue;
                    string id = "luid_0x" + ((uint)desc.Luid.High).ToString("x8", CultureInfo.InvariantCulture)
                        + "_0x" + desc.Luid.Low.ToString("x8", CultureInfo.InvariantCulture);
                    var kind = (desc.Flags & 2) != 0 ? GpuKind.Software : ReadGpuArchitecture(adapter);
                    result.Add(new(id, Clean(desc.Description), kind, desc.DedicatedVideoMemory,
                        desc.SharedSystemMemory, null));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        return result.ToArray();
    }

    static GpuKind ReadGpuArchitecture(IntPtr adapter)
    {
        IntPtr device = IntPtr.Zero;
        try
        {
            var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
            if (D3D12CreateDevice(adapter, 0xb000, ref iid, out device) < 0 || device == IntPtr.Zero) return GpuKind.Unknown;
            var data = new Architecture();
            int hr = VTable<CheckArchitecture>(device, 13)(device, 1, ref data, (uint)Marshal.SizeOf<Architecture>());
            return hr < 0 ? GpuKind.Unknown : data.Uma != 0 ? GpuKind.Integrated : GpuKind.Dedicated;
        }
        catch (DllNotFoundException) { return GpuKind.Unknown; }
        catch (EntryPointNotFoundException) { return GpuKind.Unknown; }
        finally { if (device != IntPtr.Zero) Marshal.Release(device); }
    }

    static T VTable<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    static void QueueGpuPoll(GpuInventory[] adapters)
    {
        long now = Environment.TickCount64;
        if (adapters.Length == 0 || now < Interlocked.Read(ref nextGpuPoll) || Interlocked.CompareExchange(ref gpuPollRunning, 1, 0) != 0) return;
        Interlocked.Exchange(ref nextGpuPoll, now + 3000);
        _ = Task.Run(() =>
        {
            try
            {
                var values = ReadGpuCounters(adapters);
                lock (GpuGate) { gpuCache = values; gpuCacheTick = Environment.TickCount64; }
            }
            catch { /* Failed refresh becomes unavailable once the last actual sample expires. */ }
            finally { Volatile.Write(ref gpuPollRunning, 0); }
        });
    }

    static GpuLive[] ReadGpuCounters(GpuInventory[] adapters)
    {
        var thermals=PerformanceTemperatures.Read(adapters);
        var native=PerformanceGpuCounters.Read(adapters);
        GpuLive[] WithThermals(IEnumerable<GpuLive> counters)=>counters.Select(c=>
        {
            var sensor=thermals.FirstOrDefault(t=>t.Id==c.Id);
            return c with {UsagePercent=c.UsagePercent??sensor?.UsagePercent,DedicatedUsedBytes=c.DedicatedUsedBytes??sensor?.DedicatedUsedBytes,SharedUsedBytes=c.SharedUsedBytes??sensor?.SharedUsedBytes,FrequencyMHz=sensor?.FrequencyMHz,TemperatureC=sensor?.TemperatureC};
        }).ToArray();
        // Native PDH avoids dependence on WMI's optional performance class bridge.
        // An installed driver's sensor must not wait behind a broken WMI provider.
        if(native!=null)return WithThermals(native);
        if(thermals.Any(t=>t.TemperatureC.HasValue||t.FrequencyMHz.HasValue||t.DedicatedUsedBytes.HasValue||t.UsagePercent.HasValue))
            return WithThermals(adapters.Select(a=>new GpuLive(a.Id,null,null,null,null,null)));
        var memory = QueryWmi("SELECT Name,DedicatedUsage,SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", ["Name", "DedicatedUsage", "SharedUsage"], 128);
        var engine = QueryWmi("SELECT Name,UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", ["Name", "UtilizationPercentage"], 8192);
        return WithThermals(AggregateWmiGpu(adapters, memory, engine));
    }

    internal static GpuLive[] AggregateWmiGpu(IReadOnlyList<GpuInventory> adapters,
        IReadOnlyList<Dictionary<string, object?>> memory, IReadOnlyList<Dictionary<string, object?>> engine)
    {
        var result = new List<GpuLive>();
        foreach (var adapter in adapters)
        {
            var matches = memory.Where(r => CounterGpuId(r.GetValueOrDefault("Name")) == adapter.Id).ToArray();
            ulong? dedicated = SumCounters(matches, "DedicatedUsage");
            ulong? shared = SumCounters(matches, "SharedUsage");
            // Firmware/driver reservations can exceed DXGI's dedicated descriptor slightly;
            // show unknown for impossible values rather than claiming negative free memory.
            if (adapter.DedicatedMemoryBytes is { } limit && dedicated > limit) dedicated = null;
            if (adapter.SharedMemoryBytes is { } sharedLimit && shared > sharedLimit) shared = null;
            // Share strict process/physical-engine validation with the native path.
            var readings = engine.Select(row => new PerformanceGpuCounters.Reading(
                Clean(row.GetValueOrDefault("Name")) ?? "",
                ValidPercentage(row.GetValueOrDefault("UtilizationPercentage")), null)).ToArray();
            double? usage = PerformanceGpuCounters.BusiestEngine(readings, adapter.Id);
            result.Add(new(adapter.Id, usage, dedicated, shared, null, null));
        }
        return result.ToArray();
    }

    static ulong? SumCounters(Dictionary<string, object?>[] rows, string field)
    {
        if (rows.Length == 0) return null;
        ulong sum = 0;
        foreach (var row in rows)
        {
            ulong? value = AsUInt64(row.GetValueOrDefault(field));
            if (value == null || ulong.MaxValue - sum < value) return null;
            sum += value.Value;
        }
        return sum;
    }

    // Scripting WMI is optional and executes only in the one GPU worker. A broken WMI
    // provider cannot block the UI or create repeated concurrent workers. Connection
    // requests use wbemConnectFlagUseMaxWait; iteration also has a row/time budget.
    // The COM provider itself can exceed that budget; last good readings still expire.
    static List<Dictionary<string, object?>> QueryWmi(string query, string[] fields, int rowLimit)
    {
        object? locator = null, service = null, set = null;
        IEnumerator? enumerator = null;
        var rows = new List<Dictionary<string, object?>>();
        long deadline = Environment.TickCount64 + 2000;
        try
        {
            Type? type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return rows;
            locator = Activator.CreateInstance(type);
            service = ((dynamic)locator!).ConnectServer(".", @"root\cimv2", null, null, null, null, 0x80, null);
            set = ((dynamic)service).ExecQuery(query, "WQL", 0x30, null);
            enumerator = ((IEnumerable)set).GetEnumerator();
            IEnumerable<Dictionary<string, object?>> EnumerateRows()
            {
                while (enumerator.MoveNext())
                {
                    object item = enumerator.Current;
                    try
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (string field in fields)
                        {
                            object? properties = null, property = null;
                            try
                            {
                                properties = ((dynamic)item).Properties_;
                                property = ((dynamic)properties).Item(field, 0);
                                row[field] = ((dynamic)property).Value;
                            }
                            catch { row[field] = null; }
                            finally { ReleaseCom(property); ReleaseCom(properties); }
                        }
                        yield return row;
                    }
                    finally { ReleaseCom(item); }
                }
            }
            rows = CollectWmiRows(EnumerateRows(), rowLimit, () => Environment.TickCount64 > deadline);
        }
        catch { rows.Clear(); }
        finally { (enumerator as IDisposable)?.Dispose(); ReleaseCom(enumerator); ReleaseCom(set); ReleaseCom(service); ReleaseCom(locator); }
        return rows;
    }

    internal static List<Dictionary<string, object?>> CollectWmiRows(
        IEnumerable<Dictionary<string, object?>> source, int rowLimit, Func<bool> expired)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var row in source)
            {
                rows.Add(row);
                if (rows.Count >= rowLimit || expired()) return [];
            }
            return rows;
        }
        catch { return []; } // A failed enumeration cannot certify an adapter-wide total.
    }

    static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    internal static double? CpuUsage(ulong previousIdle, ulong previousKernel, ulong previousUser,
        ulong idle, ulong kernel, ulong user)
    {
        // Kernel already includes idle. Counter resets/sleep/provider errors are unknown.
        if (idle < previousIdle || kernel < previousKernel || user < previousUser) return null;
        ulong idleDelta = idle - previousIdle, kernelDelta = kernel - previousKernel, userDelta = user - previousUser;
        double total = (double)kernelDelta + userDelta;
        if (total <= 0 || idleDelta > total) return null;
        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    internal static MemoryLive ValidateMemory(ulong total, ulong available) =>
        total == 0 || available > total ? new(null, null, null) : new(total, available, total - available);

    internal static string? CounterGpuId(object? value)
    {
        var match = LuidPattern.Match(value?.ToString() ?? "");
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high)
            || !uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low)) return null;
        return "luid_0x" + high.ToString("x8", CultureInfo.InvariantCulture) + "_0x" + low.ToString("x8", CultureInfo.InvariantCulture);
    }

    static string? Clean(object? value)
    {
        string? text = value?.ToString()?.Trim().Trim('\0').Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || text.Any(char.IsControl)) return null;
        return text.ToLowerInvariant() is "unknown" or "not specified" or "none" or "default string" or "to be filled by o.e.m." ? null : text;
    }
    static ulong? AsUInt64(object? value) => ulong.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong number) ? number : null;
    static double? ValidPercentage(object? value) => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
        && double.IsFinite(number) && number is >= 0 and <= 100 ? number : null;

    internal static void SelfTest()
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Performance sensor self-test: " + message); }
        Check(CpuUsage(100, 200, 100, 120, 260, 140) == 80, "kernel includes idle");
        Check(CpuUsage(100, 200, 100, 100, 200, 100) == null, "zero elapsed time");
        Check(CpuUsage(100, 200, 100, 99, 260, 140) == null, "counter reset");
        Check(CpuUsage(0, 0, 0, 200, 10, 10) == null, "inconsistent counters");
        Check(ValidateMemory(16, 4).UsedBytes == 12, "available versus used");
        Check(ValidateMemory(8, 9).UsedBytes == null, "invalid available memory");
        Check(CounterGpuId("pid_2_luid_0x0_0x000a_phys_0_eng_0") == "luid_0x00000000_0x0000000a", "GPU LUID canonical mapping");
        Check(CounterGpuId("GPU 0") == null, "GPU identity cannot be guessed");
        Check(ParseSmbiosMemory(new byte[7]).Modules.Count == 0, "short firmware data");
        byte[] raw = new byte[8 + 15 + 2 + 40 + 2 + 4 + 2];
        raw[1] = 3; raw[2] = 2;
        BitConverter.GetBytes((uint)(raw.Length - 8)).CopyTo(raw, 4);
        int array = 8; raw[array] = 16; raw[array + 1] = 15; raw[array + 2] = 1; raw[array + 5] = 3; raw[array + 13] = 2;
        int module = 25; raw[module] = 17; raw[module + 1] = 40; raw[module + 4] = 1;
        BitConverter.GetBytes((ushort)8192).CopyTo(raw, module + 12);
        BitConverter.GetBytes((ushort)3200).CopyTo(raw, module + 21);
        BitConverter.GetBytes((ushort)2933).CopyTo(raw, module + 32);
        int terminator = 67; raw[terminator] = 127; raw[terminator + 1] = 4;
        var memory = ParseSmbiosMemory(raw);
        Check(memory.Modules.Count == 1 && memory.TotalSlots == 2, "SMBIOS installed versus slots");
        Check(memory.Modules[0].CapacityBytes == 8UL * 1024 * 1024 * 1024, "SMBIOS capacity units");
        Check(memory.Modules[0].ConfiguredSpeedMTs == 2933 && memory.Modules[0].RatedSpeedMTs == 3200, "configured versus rated transfer rate");
        Check(memory.Modules[0].ChipManufacturer == null, "chip manufacturer is not module manufacturer");
        raw[2] = 0;
        Check(ParseSmbiosMemory(raw).Modules[0].ConfiguredSpeedMTs == null && ParseSmbiosMemory(raw).Modules[0].ConfiguredClockMHz == 2933, "older SMBIOS MHz units retained without conversion");
        raw[module + 1] = 255;
        Check(ParseSmbiosMemory(raw).Modules.Count == 0, "truncated SMBIOS structure");
    }

    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessorPower { public uint Number, MaxMHz, CurrentMHz, LimitMHz, MaxIdle, CurrentIdle; }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid Luid;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct Architecture { public uint NodeIndex; public int TileBased, Uma, CacheCoherentUma; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1(IntPtr self, out AdapterDesc1 desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CheckArchitecture(IntPtr self, uint feature, ref Architecture data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetLogicalProcessorInformationEx(uint relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(uint level, IntPtr input, uint inputLength, IntPtr output, uint outputLength);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetSystemFirmwareTable(uint provider, uint table, byte[]? buffer, uint size);
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d12.dll")] static extern int D3D12CreateDevice(IntPtr adapter, uint minimumFeatureLevel, ref Guid iid, out IntPtr device);
}
