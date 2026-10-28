using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryDiagnostic;

/// <summary>
/// Read-only optional sensor access through the NVIDIA display driver already installed
/// in Windows. Call from the existing GPU worker, never the UI thread. No utility,
/// service, kernel driver, overclock setting or thermal policy is installed or changed.
/// CPU temperature is intentionally not obtained from arbitrary ACPI thermal zones.
/// Win32_TemperatureProbe does not populate CurrentReading on current Windows WMI.
/// </summary>
internal static class PerformanceTemperatures
{
    internal const string SourceDescription = "NVIDIA NVAPI from the installed display driver: GPU temperature/current graphics clock, graphics-engine utilization and allocatable framebuffer usage when its total exactly matches DXGI; uniquely matched by model. CPU temperature has no supported source. Missing, ambiguous and failed readings remain unavailable.";
    static readonly object Gate = new();
    static readonly Lazy<NvApi?> Api = new(CreateApi);
    static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant);

    // Primary API definitions (documented public entry points only):
    // https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h
    // https://docs.nvidia.com/nvapi/group__gputhermal.html
    // https://docs.nvidia.com/nvapi/group__gpuclock.html
    // https://docs.nvidia.com/nvapi/group__gpupstate.html
    // https://docs.nvidia.com/nvapi/structNV__GPU__MEMORY__INFO__EX__V1.html
    // https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe
    internal static IReadOnlyList<GpuLive> Read(IReadOnlyList<GpuInventory> adapters)
    {
        var unknown = adapters.Select(a => new GpuLive(a.Id, null, null, null, null, null)).ToArray();
        if (adapters.Count == 0 || !OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return unknown;
        lock (Gate)
        {
            try
            {
                NvApi? api = Api.Value;
                if (api == null) return unknown;
                var handles = new IntPtr[64];
                if (api.Enumerate(handles, out uint count) != 0 || count is 0 or > 64) return unknown;
                var physical = new List<(IntPtr Handle, string Name)>();
                for (int i = 0; i < count; i++)
                {
                    var name = new StringBuilder(64);
                    // Unknown names prevent proving a unique model match, including
                    // a second identical physical GPU whose name call has failed.
                    if (handles[i] == IntPtr.Zero || physical.Any(g => g.Handle == handles[i]) ||
                        api.GetName(handles[i], name) != 0 || NormalizeModel(name.ToString()).Length == 0)
                        return unknown;
                    physical.Add((handles[i], name.ToString()));
                }
                string[] physicalNames = physical.Select(p => p.Name).ToArray();
                for (int i = 0; i < adapters.Count; i++)
                {
                    int match = UniqueModelMatch(adapters, i, physicalNames);
                    if (match < 0) continue;
                    IntPtr handle = physical[match].Handle;
                    double? temperature = api.ReadTemperature(handle);
                    double? frequency = api.ReadFrequency(handle);
                    double? usage = api.ReadUsage(handle);
                    ulong? memory = adapters[i].Kind == GpuKind.Dedicated
                        ? api.ReadDedicatedUsage(handle, adapters[i].DedicatedMemoryBytes) : null;
                    unknown[i] = unknown[i] with { TemperatureC = temperature, FrequencyMHz = frequency,
                        UsagePercent = usage, DedicatedUsedBytes = memory };
                }
            }
            catch (Exception) { return unknown; }
        }
        return unknown;
    }

    internal static string NormalizeModel(string? name)
    {
        string value = Space.Replace((name ?? "").Trim(), " ").ToUpperInvariant();
        if (value.StartsWith("NVIDIA ", StringComparison.Ordinal)) value = value[7..].Trim();
        return value;
    }

    internal static int UniqueModelMatch(IReadOnlyList<GpuInventory> adapters, int adapterIndex,
        IReadOnlyList<string> physicalNames)
    {
        if (adapterIndex < 0 || adapterIndex >= adapters.Count) return -1;
        var adapter = adapters[adapterIndex];
        string key = NormalizeModel(adapter.Model);
        if (adapter.Kind == GpuKind.Software || key.Length == 0 || string.IsNullOrWhiteSpace(adapter.Id)) return -1;
        if (adapters.Count(a => a.Kind != GpuKind.Software && NormalizeModel(a.Model) == key) != 1 ||
            adapters.Count(a => a.Id == adapter.Id) != 1) return -1;
        int match = -1;
        for (int i = 0; i < physicalNames.Count; i++)
        {
            if (NormalizeModel(physicalNames[i]) != key) continue;
            if (match >= 0) return -1;
            match = i;
        }
        return match;
    }

    static double? Temperature(ThermalSettings data, bool signed)
    {
        if (data.Count is 0 or > 3) return null;
        ThermalSensor[] sensors = [data.Sensor0, data.Sensor1, data.Sensor2];
        double? hottest = null;
        for (int i = 0; i < data.Count; i++)
        {
            var sensor = sensors[i];
            // NVAPI_THERMAL_TARGET_GPU = 1. Memory, board and PSU temperatures
            // must never be relabelled as GPU core temperature.
            if (sensor.Target != 1 || sensor.Controller <= 0 ||
                sensor.Current < (signed ? -40 : 0) || sensor.Current > 150) continue;
            hottest = hottest.HasValue ? Math.Max(hottest.Value, sensor.Current) : sensor.Current;
        }
        return hottest;
    }

    static double? GraphicsClock(ClockFrequencies data)
    {
        if (data.Domains == null || data.Domains.Length != 32) return null;
        ClockDomain graphics = data.Domains[0]; // NVAPI_GPU_PUBLIC_CLOCK_GRAPHICS
        return (graphics.Present & 1) != 0 && graphics.KHz is > 0 and <= 10000000
            ? graphics.KHz / 1000.0 : null;
    }

    static double? GpuUsage(DynamicPstates data)
    {
        if (data.Utilization == null || data.Utilization.Length != 8) return null;
        // NVAPI's GPU domain (0) is the graphics engine. FB activity (domain 1)
        // is a utilization percentage, never an amount of allocated VRAM.
        var gpu = data.Utilization[0];
        return (gpu.Present & 1) != 0 && gpu.Percent <= 100 ? gpu.Percent : null;
    }

    static ulong? DedicatedUsage(ulong physical, ulong allocatable, ulong currentFree, ulong? dxgiTotal)
    {
        // All inputs are bytes here. The available maximum excludes driver
        // reservations; current available is free allocatable framebuffer.
        // Reject differing totals rather than deducting free from physical VRAM
        // and counting reservations as application/OS allocations.
        return physical > 0 && allocatable > 0 && allocatable <= physical &&
            currentFree <= allocatable && dxgiTotal == allocatable
            ? allocatable - currentFree : null;
    }

    static NvApi? CreateApi()
    {
        IntPtr module = IntPtr.Zero;
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
            // Never search the USB/app/current directory for a DLL with this name.
            string path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            module = LoadLibraryEx(path, IntPtr.Zero, 0x00000800); // LOAD_LIBRARY_SEARCH_SYSTEM32
            if (module == IntPtr.Zero) return null;
            IntPtr entry = GetProcAddress(module, "nvapi_QueryInterface");
            if (entry == IntPtr.Zero) return null;
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(entry);
            T? Resolve<T>(uint id) where T : Delegate
            {
                IntPtr function = query(id);
                return function == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(function);
            }
            var initialize = Resolve<Initialize>(0x0150e828);
            var enumerate = Resolve<EnumPhysicalGpus>(0xe5ac921f);
            var getName = Resolve<GetFullName>(0xceee8e9f);
            var thermal = Resolve<GetThermalSettings>(0xe3640a56);
            var clocks = Resolve<GetClockFrequencies>(0xdcb616c3);
            var utilization = Resolve<GetDynamicPstates>(0x60ded2ed);
            var memoryEx = Resolve<GetMemoryInfoEx>(0xc0599498);
            var memoryLegacy = Resolve<GetMemoryInfo>(0x07f9b368);
            if (initialize == null || enumerate == null || getName == null ||
                (thermal == null && clocks == null && utilization == null && memoryEx == null && memoryLegacy == null) ||
                initialize() != 0) return null;
            var api = new NvApi(module, enumerate, getName, thermal, clocks, utilization, memoryEx, memoryLegacy);
            module = IntPtr.Zero; // Retained for the process lifetime; delegates remain valid.
            return api;
        }
        catch (Exception) { return null; }
        finally { if (module != IntPtr.Zero) FreeLibrary(module); }
    }

    sealed class NvApi(IntPtr module, EnumPhysicalGpus enumerate, GetFullName getName,
        GetThermalSettings? thermal, GetClockFrequencies? clocks, GetDynamicPstates? utilization,
        GetMemoryInfoEx? memoryEx, GetMemoryInfo? memoryLegacy)
    {
        // Keep a reference to the loaded module for the process lifetime. There is
        // one initialization and no per-poll load/unload or settings mutation.
        readonly IntPtr Module = module;
        internal readonly EnumPhysicalGpus Enumerate = enumerate;
        internal readonly GetFullName GetName = getName;

        internal double? ReadTemperature(IntPtr gpu)
        {
            if (thermal == null) return null;
            var settings = new ThermalSettings { Version = (uint)Marshal.SizeOf<ThermalSettings>() | (2u << 16) };
            int status = thermal(gpu, 15, ref settings); // NVAPI_THERMAL_TARGET_ALL
            bool signed = true;
            if (status == -9) // NVAPI_INCOMPATIBLE_STRUCT_VERSION; V1 has unsigned values.
            {
                settings = new ThermalSettings { Version = (uint)Marshal.SizeOf<ThermalSettings>() | (1u << 16) };
                status = thermal(gpu, 15, ref settings);
                signed = false;
            }
            return status == 0 ? Temperature(settings, signed) : null;
        }

        internal double? ReadFrequency(IntPtr gpu)
        {
            if (clocks == null) return null;
            // V2 layout also implements VER_3. ClockType 0 asks for CURRENT_FREQ.
            foreach (uint version in new uint[] { 3, 2, 1 })
            {
                var values = new ClockFrequencies {
                    Version = (uint)Marshal.SizeOf<ClockFrequencies>() | (version << 16),
                    ClockTypeAndReserved = 0, Domains = new ClockDomain[32]
                };
                int status = clocks(gpu, ref values);
                if (status == 0) return GraphicsClock(values);
                if (status != -9) return null;
            }
            return null;
        }

        internal double? ReadUsage(IntPtr gpu)
        {
            if (utilization == null) return null;
            var data = new DynamicPstates { Version = (uint)Marshal.SizeOf<DynamicPstates>() | (1u << 16),
                Utilization = new UtilizationDomain[8] };
            return utilization(gpu, ref data) == 0 ? GpuUsage(data) : null;
        }

        internal ulong? ReadDedicatedUsage(IntPtr gpu, ulong? dxgiTotal)
        {
            if (!dxgiTotal.HasValue || dxgiTotal == 0) return null;
            if (memoryEx != null)
            {
                var data = new MemoryInfoEx { Version = (uint)Marshal.SizeOf<MemoryInfoEx>() | (1u << 16) };
                if (memoryEx(gpu, ref data) == 0)
                    return DedicatedUsage(data.Physical, data.Available, data.CurrentAvailable, dxgiTotal);
            }
            if (memoryLegacy != null)
            {
                var data = new MemoryInfo { Version = (uint)Marshal.SizeOf<MemoryInfo>() | (3u << 16) };
                if (memoryLegacy(gpu, ref data) == 0)
                    return DedicatedUsage((ulong)data.PhysicalKiB * 1024, (ulong)data.AvailableKiB * 1024,
                        (ulong)data.CurrentAvailableKiB * 1024, dxgiTotal);
            }
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ThermalSensor { public int Controller, DefaultMin, DefaultMax, Current, Target; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ThermalSettings
    {
        public uint Version, Count;
        public ThermalSensor Sensor0, Sensor1, Sensor2;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ClockDomain { public uint Present, KHz; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ClockFrequencies
    {
        public uint Version, ClockTypeAndReserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ClockDomain[] Domains;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct UtilizationDomain { public uint Present, Percent; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct DynamicPstates
    {
        public uint Version, Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public UtilizationDomain[] Utilization;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct MemoryInfoEx
    {
        public uint Version;
        public ulong Physical, Available, SystemVideo, SharedSystem, CurrentAvailable;
        public ulong EvictionsSize, EvictionCount, PromotionsSize, PromotionCount;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct MemoryInfo
    {
        public uint Version, PhysicalKiB, AvailableKiB, SystemVideoKiB, SharedSystemKiB, CurrentAvailableKiB;
        public uint EvictionsSizeKiB, EvictionCount;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumPhysicalGpus(
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    delegate int GetFullName(IntPtr gpu, [Out] StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetThermalSettings(IntPtr gpu, uint sensorIndex, ref ThermalSettings settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetClockFrequencies(IntPtr gpu, ref ClockFrequencies clocks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetDynamicPstates(IntPtr gpu, ref DynamicPstates data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetMemoryInfoEx(IntPtr gpu, ref MemoryInfoEx data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetMemoryInfo(IntPtr gpu, ref MemoryInfo data);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeLibrary(IntPtr module);

    internal static void SelfTest()
    {
        static void Require(bool value, string name)
        { if (!value) throw new InvalidOperationException("Temperature self-test: " + name); }
        Require(Marshal.SizeOf<ThermalSensor>() == 20 && Marshal.SizeOf<ThermalSettings>() == 68 &&
            Marshal.OffsetOf<ThermalSettings>(nameof(ThermalSettings.Sensor0)).ToInt32() == 8 &&
            Marshal.SizeOf<ClockDomain>() == 8 && Marshal.SizeOf<ClockFrequencies>() == 264 &&
            Marshal.OffsetOf<ClockFrequencies>(nameof(ClockFrequencies.Domains)).ToInt32() == 8,
            "NVAPI ABI sizes and offsets");
        Require(Marshal.SizeOf<DynamicPstates>() == 72 && Marshal.SizeOf<UtilizationDomain>() == 8 &&
            Marshal.SizeOf<MemoryInfoEx>() == 80 && Marshal.OffsetOf<MemoryInfoEx>(nameof(MemoryInfoEx.Physical)).ToInt32() == 8 &&
            Marshal.SizeOf<MemoryInfo>() == 32, "NVAPI usage and memory ABI");
        GpuInventory G(string id, string? name) => new(id, name, GpuKind.Dedicated, null, null, null);
        var adapters = new[] { G("a", "NVIDIA Quadro P2000"), G("b", "Intel UHD Graphics 630") };
        Require(UniqueModelMatch(adapters, 0, ["Quadro P2000"]) == 0 &&
            UniqueModelMatch(adapters, 1, ["Quadro P2000"]) == -1,
            "vendor prefix normalization and unrelated adapter exclusion");
        Require(UniqueModelMatch(adapters, 0, ["GeForce RTX 4090", "  Quadro   P2000  "]) == 1,
            "model identity independent of enumeration order");
        Require(UniqueModelMatch(adapters, 0, ["Quadro P2000", "NVIDIA Quadro P2000"]) == -1 &&
            UniqueModelMatch([adapters[0], G("c", "Quadro P2000")], 0, ["Quadro P2000"]) == -1 &&
            UniqueModelMatch([adapters[0], G("a", "Other")], 0, ["Quadro P2000"]) == -1,
            "ambiguous physical models, adapter models and duplicate identities");
        var settings = new ThermalSettings { Count = 2,
            Sensor0 = new() { Controller = 1, Target = 1, Current = 47 },
            Sensor1 = new() { Controller = 1, Target = 2, Current = 95 }
        };
        Require(Temperature(settings, true) == 47, "memory temperature is not GPU core temperature");
        settings.Sensor0.Target = 8;
        Require(Temperature(settings, true) == null, "no core sensor is unknown");
        settings.Sensor0.Target = 1; settings.Sensor0.Current = 151;
        Require(Temperature(settings, true) == null, "invalid sensor range");
        settings.Sensor0.Current = -1;
        Require(Temperature(settings, false) == null, "V1 unsigned sentinel is not a negative temperature");
        settings.Sensor0.Current = 0;
        Require(Temperature(settings, true) == 0, "successful zero Celsius is a real value");
        settings.Count = 4;
        Require(Temperature(settings, true) == null, "oversized native sensor count");
        var clock = new ClockFrequencies { Domains = new ClockDomain[32] };
        clock.Domains[0] = new() { Present = 1, KHz = 1392000 };
        Require(GraphicsClock(clock) == 1392, "graphics clock kHz to MHz");
        clock.Domains[0].Present = 0;
        Require(GraphicsClock(clock) == null, "unsupported frequency is unknown");
        clock.Domains[0] = new() { Present = 1, KHz = uint.MaxValue };
        Require(GraphicsClock(clock) == null, "invalid native clock rejected");
        var pstate = new DynamicPstates { Utilization = new UtilizationDomain[8] };
        pstate.Utilization[0] = new() { Present = 1, Percent = 0 };
        pstate.Utilization[1] = new() { Present = 1, Percent = 73 };
        Require(GpuUsage(pstate) == 0, "successful zero usage and FB domain exclusion");
        pstate.Utilization[0].Percent = 101;
        Require(GpuUsage(pstate) == null, "invalid usage rejected");
        pstate.Utilization[0] = new() { Present = 0, Percent = 21 };
        Require(GpuUsage(pstate) == null, "unsupported GPU utilization is unknown");
        Require(DedicatedUsage(8192, 7168, 2048, 7168) == 5120 &&
            DedicatedUsage(8192, 7168, 2048, 8192) == null &&
            DedicatedUsage(8192, 7168, 8000, 7168) == null &&
            DedicatedUsage(8192, 0, 0, 8192) == null &&
            DedicatedUsage(8192, 8192, 8192, 8192) == 0,
            "allocatable memory basis, reserved framebuffer, bounds and genuine zero usage");
    }
}
