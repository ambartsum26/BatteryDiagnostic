using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BatteryDiagnostic;

internal enum CpuTemperatureState
{
    Disabled, Starting, AdministratorRequired, DriverNotInstalled, DriverRejected,
    UnsupportedProcessor, SensorUnavailable, Ready
}

internal sealed record CpuTemperatureReading(double? TemperatureC, CpuTemperatureState State,
    string Source, string Diagnostic, DateTimeOffset? Timestamp = null);

/// <summary>
/// Optional Intel DTS reader. It only attaches to the pinned official, signed PawnIO
/// driver after explicit EnableForProcess(). It never installs, starts, configures,
/// removes or updates a driver. No WinRing0, raw I/O, ACPI thermal-zone substitution,
/// inferred TjMax or MSR writes. Only three documented temperature MSRs are read.
/// The official PnP driver is a separately approved installation, not portable loading.
/// </summary>
internal static class PerformanceCpuTemperature
{
    internal const string SourceDescription = "Optional Intel DTS through official signed PawnIO 2.2.0 and signed IntelMSR module 0.2.11. Explicit activation and administrator required; no driver is installed by sampling. Temperature = hardware-reported TjMax minus DTS distance; no ACPI-zone or invented TjMax fallback. Unsupported/invalid/stale values remain unavailable.";
    const uint LoadBinary = (41394u << 16) | (0x821u << 2);
    const uint Execute = (41394u << 16) | (0x841u << 2);
    const uint TemperatureTarget = 0x1A2, CoreThermalStatus = 0x19C, PackageThermalStatus = 0x1B1;
    const string ModuleResource = "BatteryDiagnostic.Cpu.IntelMSR.bin";
    // Pinned official release assets; separate from the application's version.
    internal const string DriverVersion = "2.2.0";
    internal const string ModuleVersion = "0.2.11";
    internal const string ModuleSha256 = "D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F";
    internal const string DriverSha256 = "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940";
    static readonly object Gate = new();
    static CpuTemperatureReading cached = Missing(CpuTemperatureState.Disabled, "CPU sensor access not activated.");
    static int enabled, pollRunning;
    static long nextPollTick, sampledTick;

    internal static bool Enabled => Volatile.Read(ref enabled) != 0;
    internal static bool IsAdministrator
    {
        get
        {
            try { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    internal static void EnableForProcess()
    {
        Interlocked.Exchange(ref enabled, 1);
        lock (Gate) { nextPollTick = 0; cached = Missing(CpuTemperatureState.Starting, "CPU sensor source queued."); }
    }

    /// <summary>Read-only preflight. Does not open a device or need elevation.</summary>
    internal static CpuTemperatureReading CheckInstalledDriver()
    {
        bool valid = TryVerifyInstalledDriver(out string diagnostic, out bool exists);
        return Missing(valid ? CpuTemperatureState.Ready : exists ? CpuTemperatureState.DriverRejected : CpuTemperatureState.DriverNotInstalled, diagnostic);
    }

    /// <summary>Returns immediately; the UI and other sensors cannot wait on kernel I/O.</summary>
    internal static CpuTemperatureReading ReadCached()
    {
        if (!Enabled) return Missing(CpuTemperatureState.Disabled, "CPU sensor access not activated.");
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (now >= nextPollTick && Interlocked.CompareExchange(ref pollRunning, 1, 0) == 0)
            {
                nextPollTick = now + 2000;
                _ = Task.Run(() =>
                {
                    CpuTemperatureReading result;
                    try { result = ReadOnce(); }
                    catch (Exception ex) { result = Missing(CpuTemperatureState.SensorUnavailable, "CPU reader: " + ex.GetType().Name); }
                    lock (Gate) { cached = result; sampledTick = Environment.TickCount64; }
                    Volatile.Write(ref pollRunning, 0);
                });
            }
            if (cached.TemperatureC.HasValue && (now - sampledTick < 0 || now - sampledTick > 10000))
                return Missing(CpuTemperatureState.SensorUnavailable, "CPU temperature sample is older than 10 seconds.");
            return cached;
        }
    }

    /// <summary>CPUID-only support check; no driver handle, device access or elevation.</summary>
    internal static CpuTemperatureReading CheckProcessorSupport()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !X86Base.IsSupported)
            return Missing(CpuTemperatureState.UnsupportedProcessor, "Windows x64 Intel DTS required.");
        var vendor = X86Base.CpuId(0, 0);
        string vendorText = Encoding.ASCII.GetString(BitConverter.GetBytes(vendor.Ebx)
            .Concat(BitConverter.GetBytes(vendor.Edx)).Concat(BitConverter.GetBytes(vendor.Ecx)).ToArray());
        if (vendorText != "GenuineIntel" || vendor.Eax < 6)
            return Missing(CpuTemperatureState.UnsupportedProcessor, "This source currently supports Intel DTS only.");
        var identity = X86Base.CpuId(1, 0);
        int family = (identity.Eax >> 8) & 15, model = ((identity.Eax >> 4) & 15) | ((identity.Eax >> 12) & 240);
        if (!SupportedIntelModel(family, model))
            return Missing(CpuTemperatureState.UnsupportedProcessor, $"Intel family {family}, model 0x{model:X}: TjMax/DTS mapping not validated.");
        var thermal = X86Base.CpuId(6, 0);
        if ((thermal.Eax & 1) == 0)
            return Missing(CpuTemperatureState.UnsupportedProcessor, "CPUID.06H:EAX[0] does not advertise core DTS.");
        return Missing(CpuTemperatureState.Ready, "Intel model and CPUID core DTS support validated; no sensor read performed.");
    }

    static CpuTemperatureReading ReadOnce()
    {
        var support = CheckProcessorSupport();
        if (support.State != CpuTemperatureState.Ready) return support;
        var thermal = X86Base.CpuId(6, 0);
        if (!IsAdministrator) return Missing(CpuTemperatureState.AdministratorRequired, "Read-only CPU DTS requires an elevated process.");
        if (!TryVerifyInstalledDriver(out string diagnostic, out bool exists))
            return Missing(exists ? CpuTemperatureState.DriverRejected : CpuTemperatureState.DriverNotInstalled, diagnostic);
        // Avoid displaying one socket/group as a whole-machine temperature.
        if (GetActiveProcessorGroupCount() != 1 || !OneProcessorPackage())
            return Missing(CpuTemperatureState.UnsupportedProcessor, "This source currently requires one CPU package and one processor group.");
        if (!GetProcessAffinityMask(GetCurrentProcess(), out nuint allowed, out _) || allowed == 0)
            return Missing(CpuTemperatureState.SensorUnavailable, "Process processor affinity unavailable.");
        byte[]? module = ReadResource(ModuleResource, ModuleSha256);
        if (module == null) return Missing(CpuTemperatureState.DriverRejected, "Pinned signed IntelMSR module is missing or its SHA-256 mismatches.");
        using SafeFileHandle handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return Missing(CpuTemperatureState.SensorUnavailable, "PawnIO device unavailable: Windows error " + Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, LoadBinary, module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
            return Missing(CpuTemperatureState.DriverRejected, "Signed IntelMSR module rejected: Windows error " + Marshal.GetLastWin32Error());
        // Restrict this worker to an allowed logical processor for package/target reads.
        nuint first = allowed & (~allowed + 1);
        nuint previous = SetThreadAffinityMask(GetCurrentThread(), first);
        if (previous == 0) return Missing(CpuTemperatureState.SensorUnavailable, "Could not pin the temperature worker to a CPU.");
        try
        {
            bool packageSupported = (thermal.Eax & 0x40) != 0;
            if (packageSupported && ReadMsr(handle, TemperatureTarget, out ulong target) &&
                ReadMsr(handle, PackageThermalStatus, out ulong status) && DecodeIntelTemperature(target, status, false) is { } package)
                return new(package, CpuTemperatureState.Ready, "Intel package DTS / PawnIO", "CPU package temperature. TjMax and DTS read from CPU MSRs; no default TjMax.", DateTimeOffset.Now);
            // Core status requires bit 31 valid. Read all process-accessible logical
            // processors only when the process mask equals the system mask.
            if (!GetProcessAffinityMask(GetCurrentProcess(), out allowed, out nuint system) || allowed != system)
                return Missing(CpuTemperatureState.SensorUnavailable, "Package DTS unavailable and process affinity excludes CPU cores.");
            var coreValues = new List<double>();
            for (int cpu = 0; cpu < 64; cpu++)
            {
                nuint bit = (nuint)1 << cpu;
                if ((allowed & bit) == 0) continue;
                if (SetThreadAffinityMask(GetCurrentThread(), bit) == 0 || !ReadMsr(handle, TemperatureTarget, out target) ||
                    !ReadMsr(handle, CoreThermalStatus, out status) || DecodeIntelTemperature(target, status, true) is not { } core)
                    return Missing(CpuTemperatureState.SensorUnavailable, "At least one core DTS/TjMax reading is invalid or unavailable.");
                coreValues.Add(core);
            }
            return coreValues.Count == 0 ? Missing(CpuTemperatureState.SensorUnavailable, "No valid Intel DTS readings.")
                : new(coreValues.Max(), CpuTemperatureState.Ready, "Intel hottest core DTS / PawnIO", "Maximum valid temperature of all accessible CPU cores; package DTS unavailable.", DateTimeOffset.Now);
        }
        finally { SetThreadAffinityMask(GetCurrentThread(), previous); }
    }

    // Modern Intel family 6 processors where IA32_TEMPERATURE_TARGET reports TjMax.
    // Deliberately no fixed-value fallback for Core 2 / older Atom / unknown CPUs.
    internal static bool SupportedIntelModel(int family, int model) => family == 6 && model is
        0x1A or 0x1E or 0x1F or 0x25 or 0x2C or 0x2E or 0x2F or 0x2A or 0x2D or 0x3A or 0x3E or
        0x3C or 0x3F or 0x45 or 0x46 or 0x3D or 0x47 or 0x4F or 0x56 or 0x4E or 0x5E or 0x55 or
        0x8E or 0x9E or 0x66 or 0x7D or 0x7E or 0x6A or 0x6C or 0xA5 or 0xA6 or 0x8C or 0x8D or
        0x97 or 0x9A or 0xBE or 0xB7 or 0xBA or 0xBF or 0xAC or 0xAA or 0xA7 or 0xB5 or 0xC5 or
        0xC6 or 0xBD or 0x8F or 0xCC;

    internal static double? DecodeIntelTemperature(ulong targetMsr, ulong statusMsr, bool core)
    {
        int tjMax = (int)((targetMsr >> 16) & 255);
        if (tjMax is < 50 or > 125 || targetMsr >> 32 != 0 || statusMsr >> 32 != 0) return null;
        // Conservatively require Reading Valid for both core and package DTS.
        // Older package layouts can leave this bit reserved: then use valid core
        // readings instead. An all-zero response must never look like TjMax.
        if ((statusMsr & 0x80000000) == 0) return null;
        int delta = (int)((statusMsr >> 16) & 127);
        int value = tjMax - delta;
        return value is >= -20 and <= 125 ? value : null;
    }

    static bool ReadMsr(SafeFileHandle handle, uint address, out ulong value)
    {
        value = 0;
        if (address is not (TemperatureTarget or CoreThermalStatus or PackageThermalStatus)) return false;
        byte[] input = new byte[40], output = new byte[8];
        Encoding.ASCII.GetBytes("ioctl_read_msr").CopyTo(input, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), address);
        if (!DeviceIoControl(handle, Execute, input, (uint)input.Length, output, 8, out uint returned, IntPtr.Zero) || returned != 8) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(output);
        return true;
    }

    static bool TryVerifyInstalledDriver(out string diagnostic, out bool exists)
    {
        exists = false;
        diagnostic = "Official signed PawnIO 2.2.0 is not installed.";
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (service == null) return false;
            exists = true;
            string? raw = service.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(raw)) { diagnostic = "PawnIO service has no image path."; return false; }
            string path = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) { diagnostic = "Installed PawnIO driver image cannot be verified."; return false; }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!hash.Equals(DriverSha256, StringComparison.OrdinalIgnoreCase))
            { diagnostic = "Installed PawnIO is not the pinned official signed 2.2.0 driver; unrestricted or other versions are not accepted."; return false; }
            diagnostic = "Pinned official signed PawnIO 2.2.0 driver hash verified.";
            return true;
        }
        catch (Exception ex) { diagnostic = "Cannot verify installed PawnIO: " + ex.GetType().Name; return false; }
    }

    internal static bool VerifyModulePayload() => ReadResource(ModuleResource, ModuleSha256) != null;

    static byte[]? ReadResource(string name, string expectedHash)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null || stream.Length is <= 0 or > 16777216) return null;
            using var memory = new MemoryStream(); stream.CopyTo(memory); byte[] data = memory.ToArray();
            return Convert.ToHexString(SHA256.HashData(data)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ? data : null;
        }
        catch { return null; }
    }

    static bool OneProcessorPackage()
    {
        uint bytes = 0;
        _ = GetLogicalProcessorInformationEx(3, IntPtr.Zero, ref bytes);
        if (bytes is < 8 or > 1048576) return false;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (!GetLogicalProcessorInformationEx(3, buffer, ref bytes)) return false;
            int count = 0, offset = 0;
            while (offset + 8 <= bytes)
            {
                int relation = Marshal.ReadInt32(buffer, offset), size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > bytes) return false;
                if (relation == 3) count++;
                offset += size;
            }
            return offset == bytes && count == 1;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static CpuTemperatureReading Missing(CpuTemperatureState state, string message) => new(null, state, "Intel DTS / PawnIO", message);

    internal static void SelfTest()
    {
        static void Check(bool valid, string description) { if (!valid) throw new InvalidOperationException("CPU temperature self-test: " + description); }
        Check(VerifyModulePayload(), "embedded official IntelMSR module hash verified");
        Check(CheckProcessorSupport().TemperatureC == null, "CPUID preflight cannot claim a temperature");
        Check(SupportedIntelModel(6, 0x9E), "Coffee Lake i3-8100 supported");
        Check(!SupportedIntelModel(6, 0x0F) && !SupportedIntelModel(15, 0x9E) && !SupportedIntelModel(6, 0xFF), "unknown TjMax mapping rejected");
        Check(DecodeIntelTemperature(100UL << 16, (65UL << 16) | 0x80000000, true) == 35, "core DTS uses hardware TjMax");
        Check(DecodeIntelTemperature(100UL << 16, 65UL << 16, true) == null, "invalid core DTS never becomes a temperature");
        Check(DecodeIntelTemperature(100UL << 16, (65UL << 16) | 0x80000000, false) == 35, "valid package DTS decoded");
        Check(DecodeIntelTemperature(100UL << 16, 0, false) == null && DecodeIntelTemperature(100UL << 16, 65UL << 16, false) == null, "invalid or zero package status must not become TjMax");
        Check(DecodeIntelTemperature(0, 0x80000000, true) == null && DecodeIntelTemperature(255UL << 16, 0x80000000, true) == null, "missing TjMax not replaced by 100");
        Check(DecodeIntelTemperature(100UL << 16, 0x80000000, true) == 100, "zero distance is a valid hot reading");
        Check(DecodeIntelTemperature(50UL << 16, (127UL << 16) | 0x80000000, true) == null, "implausible temperatures rejected");
        Check(DecodeIntelTemperature(1UL << 48, 0x80000000, true) == null, "malformed MSR rejected");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern nuint SetThreadAffinityMask(IntPtr thread, nuint mask);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);
    [DllImport("kernel32.dll")] static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}

