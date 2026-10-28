using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BatteryDiagnostic;

// Optional native CrystalDiskInfo backend: a1ive/nwinfo/libcdi.
// The verified native DLL stays external to the single-file EXE.  It is loaded
// only from the application's own directory, not from PATH or the current dir.
internal static class LibCdiStorage
{
    const string Name = "libcdi.dll";
    const string Source = "LibCdi";
    // x64 DLL from the SHA-256-verified official NWinfo v1.6.6 archive.
    const string ExpectedSha256 = "861D53BBB54D7251B8BC26EB84F7477C965437666EB801F7DDFA2B36E4837077";
    static readonly string[] RequiredExports = ["cdi_get_version","cdi_create_smart","cdi_destroy_smart","cdi_init_smart","cdi_get_disk_count","cdi_get_int","cdi_get_dword","cdi_get_string","cdi_free_string","cdi_get_smart_id","cdi_get_smart_value"];
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr VersionGetter();

    static bool VerifiedFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))==ExpectedSha256;
    static string VerifyExports(IntPtr library)
    {
        foreach(string export in RequiredExports)_=NativeLibrary.GetExport(library,export);
        var version=Marshal.GetDelegateForFunctionPointer<VersionGetter>(NativeLibrary.GetExport(library,"cdi_get_version"));
        return Marshal.PtrToStringAnsi(version())??"unknown";
    }
    // Loads the verified DLL and reads its version only. Never creates a SMART
    // object or enumerates hardware; safe to exercise on the Windows CI runner.
    internal static string CheckNativeApi(string path)
    {
        if(!VerifiedFile(path))throw new InvalidDataException("libcdi SHA256 mismatch");
        IntPtr library=NativeLibrary.Load(Path.GetFullPath(path));
        try{return VerifyExports(library);}finally{NativeLibrary.Free(library);}
    }
    internal static string StatusKey(StorageSnapshot? snapshot,StorageDisk? disk,bool fresh)
    {
        if(snapshot is null)return "cdiWaiting";
        if(!fresh)return "storageStale";
        var notes=snapshot.Diagnostics;
        if(notes.Contains("libcdi:NotInstalled"))return "cdiMissing";
        if(notes.Contains("libcdi:HashMismatch"))return "cdiRejected";
        if(notes.Contains("libcdi:AdministratorRequired"))return "cdiAdministrator";
        if(disk?.Diagnostics.Contains("libcdi:MatchedByPhysicalDriveAndModel")==true)
            return disk.Diagnostics.Contains("libcdi:NoUsableData")?"cdiNoData":"cdiMatched";
        if(notes.Any(n=>n.StartsWith("libcdi:Enumerated=",StringComparison.Ordinal)))return "cdiNoMatch";
        if(notes.Any(n=>n.StartsWith("libcdi:",StringComparison.Ordinal)&&!n.StartsWith("libcdi:Version=",StringComparison.Ordinal)))return "cdiFailed";
        return "cdiWaiting";
    }
    const int DiskId = 0, DiskStatus = 1, PowerHours = 5, Temperature = 6,
              HostWrites = 8, HostReads = 9, RemainingLife = 13;
    const int AttributeCount = 4, PowerCycles = 5;
    const int ModelString = 2;
    // Mirrors upstream CDI_FLAG_DEFAULT. The upstream initializer may enable SMART.
    const ulong DefaultFlags = (1UL << 0) | (1UL << 3) | (1UL << 4) |
        (1UL << 7) | (1UL << 10) | (1UL << 11) | (1UL << 12) |
        (1UL << 13) | (1UL << 14) | (1UL << 15) | (1UL << 16) |
        (1UL << 19) | (1UL << 20) | (1UL << 21) | (1UL << 22) |
        (1UL << 23) | (1UL << 27) | (1UL << 28);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Destroy(IntPtr ptr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Initialize(IntPtr ptr, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int DiskCountGetter(IntPtr ptr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int IntGetter(IntPtr ptr, int disk, int attribute);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint DwordGetter(IntPtr ptr, int disk, int attribute);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr StringGetter(IntPtr ptr, int disk, int attribute);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void FreeString(IntPtr ptr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate byte SmartIdGetter(IntPtr ptr, int disk, int attr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr SmartValueGetter(IntPtr ptr, int disk, int attr, int hex);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr pvReserved, uint coInit);
    [DllImport("ole32.dll")] static extern void CoUninitialize();

    sealed record Sample(int DriveId, string Model, int Status, double? Temperature,
        int? Life, ulong? Hours, ulong? Cycles, ulong? Reads, ulong? Writes,
        IReadOnlyList<StorageSmartAttribute> Smart);

    // Does not infer identity from enumeration order or model name alone.
    internal static bool IdentityMatches(StorageDisk disk, int cdiDriveId, string cdiModel)
    {
        if (disk.Index is not int n || n < 0 || cdiDriveId != n) return false;
        static string Normalize(string name) => Regex.Replace(name.Trim().ToUpperInvariant(), "[^A-Z0-9]", "");
        return !string.IsNullOrWhiteSpace(cdiModel) &&
            Normalize(disk.Model).Length > 0 && Normalize(disk.Model) == Normalize(cdiModel);
    }

    // libcdi smart-value format for conventional 6-byte SATA records:
    // " 99   1 --- 000000000063". Other vendor layouts are not guessed.
    internal static StorageSmartAttribute? ParseAtaAttribute(byte id, string? value)
    {
        if (id is 0 or 255 || string.IsNullOrWhiteSpace(value)) return null;
        var m = Regex.Match(value, @"^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3}|---)\s+([0-9A-Fa-f]{12})\s*$", RegexOptions.CultureInvariant);
        if (!m.Success || !byte.TryParse(m.Groups[1].Value, out byte current) ||
            !byte.TryParse(m.Groups[2].Value, out byte worst) ||
            !ulong.TryParse(m.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)) return null;
        byte? threshold = m.Groups[3].Value == "---" ? null : byte.TryParse(m.Groups[3].Value, out byte parsed) ? parsed : null;
        if (m.Groups[3].Value != "---" && !threshold.HasValue) return null;
        return new StorageSmartAttribute(id, current, worst, threshold, raw, false);
    }

    internal static void Enrich(List<StorageDisk> disks, List<string> notes)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { notes.Add("libcdi:UnsupportedPlatform"); return; }
        var path = Path.Combine(AppContext.BaseDirectory, Name);
        if (!File.Exists(path)) { notes.Add("libcdi:NotInstalled"); return; }
        if(!PerformanceCpuTemperature.IsAdministrator){notes.Add("libcdi:AdministratorRequired");return;}
        IntPtr library = IntPtr.Zero, handle = IntPtr.Zero;
        bool releaseCom = false;
        try
        {
            if(!VerifiedFile(path)){notes.Add("libcdi:HashMismatch");return;}
            library = NativeLibrary.Load(path);
            notes.Add("libcdi:Version="+VerifyExports(library));
            T Api<T>(string export) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, export));
            var create = Api<Create>("cdi_create_smart");
            var initialize = Api<Initialize>("cdi_init_smart");
            var count = Api<DiskCountGetter>("cdi_get_disk_count");
            var getInt = Api<IntGetter>("cdi_get_int");
            var getDword = Api<DwordGetter>("cdi_get_dword");
            var getString = Api<StringGetter>("cdi_get_string");
            var freeString = Api<FreeString>("cdi_free_string");
            var getId = Api<SmartIdGetter>("cdi_get_smart_id");
            var getValue = Api<SmartValueGetter>("cdi_get_smart_value");
            string? ReadString(IntPtr p, int disk, int attr)
            {
                IntPtr stringPtr = getString(p, disk, attr);
                if (stringPtr == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(stringPtr); }
                finally { freeString(stringPtr); }
            }
            string? ReadSmartValue(IntPtr p, int disk, int attr)
            {
                IntPtr stringPtr = getValue(p, disk, attr, 1);
                if (stringPtr == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(stringPtr); }
                finally { freeString(stringPtr); }
            }
            int hr = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
            releaseCom = hr is 0 or 1;
            if (hr < 0 && hr != unchecked((int)0x80010106))
            {
                notes.Add("libcdi:ComInitializationFailed:0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                return;
            }
            handle = create();
            if (handle == IntPtr.Zero) { notes.Add("libcdi:CreateFailed"); return; }
            // cdi_init_smart enumerates drives and may be slow. ReadCore's single
            // background worker and 8-second bounded public wait remain in force.
            initialize(handle, DefaultFlags);
            int found = count(handle);
            if (found is < 0 or > 128) { notes.Add("libcdi:InvalidCount"); return; }
            var samples = new List<Sample>();
            for (int i = 0; i < found; i++)
            {
                string? model = ReadString(handle, i, ModelString);
                if (string.IsNullOrWhiteSpace(model)) continue;
                int driveId = getInt(handle, i, DiskId);
                // Only read attributes after confirming a possible matching disk.
                if (!disks.Any(d => IdentityMatches(d, driveId, model))) continue;
                var attributes = new List<StorageSmartAttribute>();
                int attrCount = (int)getDword(handle, i, AttributeCount);
                if (attrCount is > 0 and <= 64)
                    for (int a = 0; a < attrCount; a++)
                    {
                        byte attrId = getId(handle, i, a);
                        var decoded = ParseAtaAttribute(attrId, ReadSmartValue(handle, i, a));
                        if (decoded is not null && attributes.All(x => x.Id != attrId)) attributes.Add(decoded);
                    }
                int temp = getInt(handle, i, Temperature), life = getInt(handle, i, RemainingLife), hours = getInt(handle, i, PowerHours);
                int reads = getInt(handle, i, HostReads), writes = getInt(handle, i, HostWrites);
                uint cycles = getDword(handle, i, PowerCycles);
                samples.Add(new Sample(driveId, model, getInt(handle, i, DiskStatus),
                    temp is >= -10 and <= 100 ? temp : null, life is >= 0 and <= 100 ? life : null,
                    hours >= 0 ? (ulong)hours : null, cycles, reads >= 0 ? (ulong)reads * 1073741824UL : null,
                    writes >= 0 ? (ulong)writes * 1073741824UL : null, attributes));
            }
            Apply(disks, samples, notes);
            notes.Add("libcdi:Enumerated=" + found.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The existing Windows/DiskInfoToolkit backends remain available.
            notes.Add("libcdi:" + e.GetType().Name);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var destroy = Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(library, "cdi_destroy_smart"));
                    destroy(handle);
                }
                catch (Exception) { notes.Add("libcdi:CleanupFailed"); }
            }
            if (releaseCom) CoUninitialize();
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }
    }

    static void Apply(List<StorageDisk> disks, List<Sample> samples, List<string> notes)
    {
        int enriched = 0;
        for (int i = 0; i < disks.Count; i++)
        {
            var disk = disks[i];
            var hits = samples.Where(s => IdentityMatches(disk, s.DriveId, s.Model)).ToArray();
            if (hits.Length != 1)
            {
                if (hits.Length > 1) notes.Add("libcdi:AmbiguousIdentity");
                continue;
            }
            var data = hits[0];
            bool sc311 = disk.Model.StartsWith("SK hynix ", StringComparison.OrdinalIgnoreCase) &&
                (disk.Model.Contains("SC311", StringComparison.OrdinalIgnoreCase) ||
                 disk.Model.Contains("SC401", StringComparison.OrdinalIgnoreCase));
            var decoded = PerformanceStorage.LibCdiDecodedSensors(disk.Model, disk.MediaType, data.Smart);
            // On SC311 compare libcdi's reported life with decoded raw E9.
            bool inconsistency = decoded.LifeInvalid || decoded.LifeConflict ||
                (sc311 && decoded.RemainingLifePercent.HasValue && data.Life.HasValue &&
                 decoded.RemainingLifePercent.Value != data.Life.Value);
            if (inconsistency) notes.Add("libcdi:LifeInvalidOrConflicting");
            double? life = disk.MediaType == "HDD" || inconsistency ? null : decoded.RemainingLifePercent ?? data.Life;
            // Respect existing confirmed ATA SMART, but override bogus Windows
            // SC311 Wear only when CDI has a matched manufacturer-specific result.
            bool keepExisting = disk.RemainingLifePercent.HasValue &&
                disk.RemainingLifeSource is not ("None" or "WindowsStorageMethod" or "WindowsStorageAssociation");
            bool independentConflict = keepExisting && life.HasValue && Math.Abs(disk.RemainingLifePercent!.Value - life.Value) > 10;
            if (independentConflict)
            {
                life = null;
                notes.Add("libcdi:ConflictingIndependentLife");
            }
            string code = data.Status switch { 1 => "ProviderHealthy", 2 => "Warning", 3 => "Unhealthy", _ => "Unknown" };
            bool existingFailure = disk.HealthCode is "FailurePredicted" or "Unhealthy" or "Warning" or
                "AtaThresholdExceeded" or "AtaThresholdAtLimit" or "AtaSectorWarning";
            var diag = disk.Diagnostics.ToList();
            diag.Add("libcdi:MatchedByPhysicalDriveAndModel");
            if (inconsistency) diag.Add("libcdi:LifeInvalidOrConflicting");
            if (inconsistency) diag.Add("ata-life:InvalidOrConflictingLibCdi");
            if (independentConflict) diag.Add("ata-life:ConflictingSources");
            // Do not promote library-only 'good' to verified health if it did not
            // return SMART data, and never override existing fault warnings.
            bool hasAttributes = data.Smart.Count > 0;
            if (!hasAttributes) diag.Add("libcdi:NoParsedAtaAttributes");
            if(!hasAttributes&&data.Temperature is null&&data.Life is null&&data.Hours is null&&data.Reads is null&&data.Writes is null)
                diag.Add("libcdi:NoUsableData");
            var effectiveLife = keepExisting ? disk.RemainingLifePercent : life;
            if (inconsistency || independentConflict) effectiveLife = null;
            string newLifeSource = effectiveLife.HasValue ? keepExisting ? disk.RemainingLifeSource : Source : "None";
            disks[i] = disk with
            {
                SmartAttributes = disk.SmartAttributes.Count > 0 ? disk.SmartAttributes : data.Smart,
                AtaSmartSource = hasAttributes && disk.SmartAttributes.Count == 0 ? Source : disk.AtaSmartSource,
                AtaProfile = decoded.Profile != "None" ? decoded.Profile : disk.AtaProfile,
                TemperatureC = disk.TemperatureC ?? data.Temperature,
                TemperatureSource = disk.TemperatureC.HasValue ? disk.TemperatureSource : data.Temperature.HasValue ? Source : "None",
                PowerOnHours = disk.PowerOnHours ?? data.Hours,
                PowerOnHoursSource = disk.PowerOnHours.HasValue ? disk.PowerOnHoursSource : data.Hours.HasValue ? Source : "None",
                PowerOnCount = disk.PowerOnCount ?? data.Cycles,
                HostReadsBytes = disk.HostReadsBytes ?? data.Reads,
                HostWritesBytes = disk.HostWritesBytes ?? data.Writes,
                RemainingLifePercent = effectiveLife,
                RemainingLifeSource = newLifeSource,
                WearPercent = inconsistency || independentConflict ? null : effectiveLife.HasValue ? 100 - (int)effectiveLife.Value : disk.WearPercent,
                WearSource = inconsistency || independentConflict ? "None" : effectiveLife.HasValue ? newLifeSource : disk.WearSource,
                HealthCode = !existingFailure && code != "Unknown" && hasAttributes ? code : disk.HealthCode,
                HealthSource = !existingFailure && code != "Unknown" && hasAttributes ? Source : disk.HealthSource,
                Diagnostics = diag.Distinct().ToArray()
            };
            enriched++;
        }
        notes.Add("libcdi:Bound=" + enriched.ToString(CultureInfo.InvariantCulture));
    }

    internal static void SelfTest()
    {
        static void Check(bool v, string msg) { if (!v) throw new Exception("libcdi: " + msg); }
        var snapshot=new StorageSnapshot(DateTimeOffset.Now,[],["libcdi:NotInstalled"]);
        Check(StatusKey(snapshot,null,true)=="cdiMissing","missing DLL distinct from success");
        Check(StatusKey(snapshot with {Diagnostics=["libcdi:DllNotFoundException"]},null,true)=="cdiFailed","load error distinct from success");
        Check(StatusKey(snapshot with {Diagnostics=["libcdi:Enumerated=2"]},new StorageDisk(),true)=="cdiNoMatch","loaded without matched disk");
        var matched=new StorageDisk {Diagnostics=["libcdi:MatchedByPhysicalDriveAndModel"]};
        Check(StatusKey(snapshot with {Diagnostics=[]},matched,true)=="cdiMatched","matched disk status");
        Check(StatusKey(snapshot with {Diagnostics=[]},matched,false)=="storageStale","stale binding never presented as live");
        Check(StatusKey(snapshot with {Diagnostics=[]},matched with {Diagnostics=["libcdi:MatchedByPhysicalDriveAndModel","libcdi:NoUsableData"]},true)=="cdiNoData","matching identity alone is not data");
        var sc = new StorageDisk { Index = 0, Model = "SK hynix SC311 SATA 256GB" };
        Check(IdentityMatches(sc, 0, "SK hynix SC311 SATA 256GB"), "exact disk identity");
        Check(!IdentityMatches(sc, 1, sc.Model), "identical models, different physical disks");
        Check(!IdentityMatches(sc, -1, sc.Model) && !IdentityMatches(sc with { Index = null }, 0, sc.Model), "unknown disk number");
        Check(!IdentityMatches(sc, 0, "Different SSD"), "different disk model");
        var e9 = ParseAtaAttribute(0xE9, "  2   1 --- 000000000063");
        Check(e9 is { Current: 2, Worst: 1, Raw: 99 }, "SC311 raw E9, not normalized E9");
        Check(ParseAtaAttribute(0xE9, "  2   1 --- NOTSMART") is null, "invalid SMART data");
        Check(ParseAtaAttribute(0xE9, "  2   1 --- 000000000063 ") is not null, "valid padded SMART");
        Check(ParseAtaAttribute(0xE9, "  2   1 000 000000000063")?.Threshold == 0, "valid threshold decoding");
    }
}
