using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Text.RegularExpressions;

namespace BatteryDiagnostic;

// Read-only Windows storage provider + documented disk-class IOCTLs. The only
// ATA commands are IDENTIFY / SMART_READ_DATA / READ_THRESHOLDS. No SMART enable,
// self-test, surface writes, firmware changes or additional driver.
internal static partial class PerformanceStorage
{
    const string StorageNamespace = @"ROOT\Microsoft\Windows\Storage";
    const uint IoctlStorageQueryProperty = 0x002D1400;
    const uint IoctlStoragePredictFailure = 0x002D1100;
    const uint IoctlSmartReceiveDriveData = 0x0007C088;
    const uint IoctlAtaPassThrough = 0x0004D02C;
    static readonly object gate = new();
    static Task<StorageSnapshot>? pending;
    static StorageSnapshot? last;

    internal static StorageSnapshot Read()
    {
        if (!OperatingSystem.IsWindows()) return new(DateTimeOffset.Now, [], ["storage:Unsupported"]);
        Task<StorageSnapshot> task;
        lock (gate)
        {
            // A broken WMI provider or device driver cannot create an accumulating
            // series of abandoned workers. The caller has an eight-second bound.
            pending ??= Task.Factory.StartNew(ReadCore, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task = pending;
        }
        if (!task.Wait(TimeSpan.FromSeconds(8)))
        {
            lock (gate)
                return last is null ? new(DateTimeOffset.Now, [], ["storage:Timeout"]) :
                    last with { Diagnostics = last.Diagnostics.Concat(["storage:Timeout", "storage:Stale"]).Distinct().ToArray() };
        }
        lock (gate)
        {
            if (ReferenceEquals(pending, task)) { last = task.Result; pending = null; }
            return task.Result;
        }
    }

    static StorageSnapshot ReadCore()
    {
        var notes = new List<string>();
        try
        {
            using var cim = new DiskWmiSession(@"ROOT\CIMV2", notes);
            using var storage = new DiskWmiSession(StorageNamespace, notes);
            var raw = cim.Query("SELECT Index,DeviceID,PNPDeviceID,Model,Manufacturer,Size,InterfaceType,SerialNumber FROM Win32_DiskDrive", "Index", "DeviceID", "PNPDeviceID", "Model", "Manufacturer", "Size", "InterfaceType", "SerialNumber");
            var partitions = cim.Query("SELECT DeviceID,DiskIndex FROM Win32_DiskPartition", "DeviceID", "DiskIndex");
            var volumeRows = cim.Query("SELECT DeviceID,FileSystem,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=2 OR DriveType=3", "DeviceID", "FileSystem", "Size", "FreeSpace");
            var links = cim.Query("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition", "Antecedent", "Dependent");
            var physical = storage.Query("SELECT * FROM MSFT_PhysicalDisk", "__RELPATH", "DeviceId", "FriendlyName", "Model", "Manufacturer", "SerialNumber", "UniqueId", "HealthStatus", "OperationalStatus", "MediaType", "BusType", "Size");
            var disks = storage.Query("SELECT * FROM MSFT_Disk", "__RELPATH", "Number", "UniqueId", "SerialNumber", "PartitionStyle", "BusType", "Size", "FriendlyName");
            var reliability = storage.Query("SELECT * FROM MSFT_StorageReliabilityCounter", "__RELPATH", "DeviceId", "Temperature", "Wear", "PowerOnHours", "ReadErrorsUncorrected", "WriteErrorsUncorrected");
            var physicalCounterLinks = storage.Query("SELECT * FROM MSFT_PhysicalDiskToStorageReliabilityCounter", "PhysicalDisk", "StorageReliabilityCounter");
            var diskCounterLinks = storage.Query("SELECT * FROM MSFT_DiskToStorageReliabilityCounter", "Disk", "StorageReliabilityCounter");

            var volumeOwners = MapVolumeOwners(partitions, links);
            if (raw.Count == 0) raw = ReadNativeDisks(notes);
            // This fallback also works when WMI's association provider is blocked.
            // The native volume extents, never drive-letter order, identify owners.
            if (volumeOwners.Count == 0) ReadNativeVolumes(volumeRows, volumeOwners, notes);
            var usedPhysical = new HashSet<DiskWmiRow>();
            var result = new List<StorageDisk>();
            foreach (var row in raw)
            {
                int? index = Int(row, "Index");
                var systemDisk = disks.FirstOrDefault(x => Int(x, "Number") == index && index.HasValue);
                var candidate = MatchPhysical(row, systemDisk, physical.Where(x => !usedPhysical.Contains(x)).ToArray());
                if (candidate is not null) usedPhysical.Add(candidate);
                var volumes = volumeRows.Where(v => volumeOwners.TryGetValue(Text(v, "DeviceID") ?? "", out var owners) && index.HasValue && owners.Contains(index.Value))
                    .Select(v => MakeVolume(v, volumeOwners)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                result.Add(BuildDisk(row, systemDisk, candidate, reliability, physicalCounterLinks, diskCounterLinks, volumes, storage));
            }
            // Storage Spaces and providers may expose additional physical media
            // that have no Win32 disk/volume mapping. Keep them visible, without
            // assigning another disk's filesystem space or a guessed drive index.
            foreach (var item in physical.Where(x => !usedPhysical.Contains(x)))
                result.Add(BuildDisk(null, null, item, reliability, physicalCounterLinks, diskCounterLinks, [], storage));
            // Prefer the optional, CrystalDiskInfo-derived native backend.
            // The legacy readers stay as fallbacks for unsupported controllers.
            LibCdiStorage.Enrich(result, notes);
            if(result.Count==0||result.Any(d=>d.SmartAttributes.Count==0&&d.NvmeCriticalWarning is null))EnrichWithToolkit(result,notes);
            // The built-in root\WMI failure prediction provider works on many
            // Intel RST setups where both ATA pass-through and toolkit probes fail.
            // Bind by exact PnP instance path; never assign by disk index or model.
            if (result.Any(d => (d.BusType is "ATA" or "SATA") && d.SmartAttributes.Count == 0))
            {
                using var smartWmi = new DiskWmiSession(@"ROOT\WMI", notes);
                EnrichWithWmiSmart(result, raw, smartWmi, notes);
            }
            SmartctlStorage.Enrich(result,notes);
            return new(DateTimeOffset.Now, result.OrderBy(x => x.Index ?? int.MaxValue).ThenBy(x => x.Model).ToArray(), notes.Distinct().ToArray());
        }
        catch (Exception ex)
        {
            notes.Add("storage:" + ErrorCode(ex));
            return new(DateTimeOffset.Now, [], notes.Distinct().ToArray());
        }
    }

    static StorageDisk BuildDisk(DiskWmiRow? raw, DiskWmiRow? disk, DiskWmiRow? physical, List<DiskWmiRow> reliability, List<DiskWmiRow> physicalCounterLinks, List<DiskWmiRow> diskCounterLinks, StorageVolume[] volumes, DiskWmiSession? storage = null)
    {
        int? index = Int(raw, "Index");
        string path = Text(raw, "DeviceID") ?? "";
        string model = Text(physical, "Model") ?? Text(raw, "Model") ?? Text(physical, "FriendlyName") ?? Text(disk, "FriendlyName") ?? "";
        string? manufacturer = Brand(Text(physical, "Manufacturer")) ?? Brand(Text(raw, "Manufacturer"));
        string? bus = Bus(Int(physical, "BusType")) ?? Bus(Int(disk, "BusType")) ?? Text(raw, "InterfaceType");
        string media = Int(physical, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
        string mediaSource = media == "Unknown" ? "None" : "WindowsStorage";
        string? physicalId = Text(physical, "DeviceId");
        var diagnostics = new List<string>();
        // Get-StorageReliabilityCounter invokes a read-only static method on
        // PS_StorageCmdlets with the exact embedded disk object. Enumerating the
        // counter class alone is insufficient on many Windows storage drivers.
        var counter = storage?.ReadReliability(physical, "MSFT_PhysicalDisk", "PhysicalDisk", diagnostics)
            ?? storage?.ReadReliability(disk, "MSFT_Disk", "Disk", diagnostics);
        string reliabilitySource = counter is null ? "None" : "WindowsStorageMethod";
        // DeviceId is not a cross-class identity: one counter can be relative to
        // MSFT_Disk.Number, another to MSFT_PhysicalDisk.DeviceId. Follow actual
        // provider association references, or leave the counters unmeasured.
        if (counter is null)
        {
            counter = AssociatedCounter(physical, "MSFT_PhysicalDisk", "PhysicalDisk", physicalCounterLinks, reliability)
                ?? AssociatedCounter(disk, "MSFT_Disk", "Disk", diskCounterLinks, reliability);
            if (counter is not null) reliabilitySource = "WindowsStorageAssociation";
        }
        if (physical is null) diagnostics.Add("physical-provider:Unmatched");
        if (counter is null) diagnostics.Add("reliability:UnavailableOrUnmapped");
        var health = UShort(physical, "HealthStatus");
        var operational = UShortArray(physical, "OperationalStatus");
        int? temperature = Int(counter, "Temperature");
        double? temperatureC = temperature is > 0 and < 255 ? temperature.Value : null;
        string temperatureSource = temperatureC.HasValue ? reliabilitySource : "None";
        int? wear = Int(counter, "Wear");
        if (wear is < 0 or > 255) { wear = null; diagnostics.Add("reliability:InvalidWear"); }
        // Preserve the storage provider's original wear estimate for comparison
        // against independently decoded vendor-specific SMART attributes.
        int? providerWear = wear;
        ulong? hours = ULong(counter, "PowerOnHours");
        ulong? powerOnCount = null, hostReadsBytes = null, hostWritesBytes = null;
        string wearSource = wear.HasValue ? reliabilitySource : "None";
        double? remainingLife = media == "SSD" && wear.HasValue ? Math.Max(0, 100 - wear.Value) : null;
        string remainingSource = remainingLife.HasValue ? wearSource : "None";
        string hoursSource = hours.HasValue ? reliabilitySource : "None";
        // For these controllers Windows Wear is derived from a vendor-normalized
        // E9 value, not from the SC311/SC401 raw endurance byte. Never present
        // 98% wear as 2% remaining when ATA SMART is inaccessible.
        if (IsSc311Family(SelectSsdProfile(model, media)?.Name))
        {
            wear = null; remainingLife = null;
            wearSource = remainingSource = "None";
            if (providerWear.HasValue) diagnostics.Add("reliability:Sc311WindowsWearNotAuthoritative");
        }
        string ataSource = "None", ataProfile = "None";
        ulong? readErrors = ULong(counter, "ReadErrorsUncorrected");
        ulong? writeErrors = ULong(counter, "WriteErrorsUncorrected");
        bool? predicted = null;
        NvmeLog? nvme = null;
        AtaSmart? ata = null;
        if (path.StartsWith(@"\\.\PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var descriptor = Query(handle, IoctlStorageQueryProperty, Property(0), 4096, out var descriptorError);
                if (descriptor is { Length: >= 36 })
                {
                    bus = Bus((int)UInt(descriptor, 28)) ?? bus;
                    manufacturer ??= Brand(AsciiAt(descriptor, UInt(descriptor, 12)));
                    if (model.Length == 0) model = AsciiAt(descriptor, UInt(descriptor, 16)) ?? "";
                }
                else diagnostics.Add("device-descriptor:" + descriptorError);
                var temperatureBytes = Query(handle, IoctlStorageQueryProperty, Property(52), 4096, out var temperatureError);
                double? deviceTemperature = DecodeStorageTemperature(temperatureBytes);
                if (deviceTemperature.HasValue)
                {
                    temperatureC = deviceTemperature;
                    temperatureSource = "StorageDeviceTemperatureProperty";
                }
                else diagnostics.Add("device-temperature:" + (temperatureBytes is null ? temperatureError : "UnavailableOrInvalidResponse"));
                if (media == "Unknown")
                {
                    var seek = Query(handle, IoctlStorageQueryProperty, Property(7), 64, out var seekError);
                    bool? incursSeekPenalty = DecodeSeekPenalty(seek);
                    if (incursSeekPenalty.HasValue)
                    {
                        // A nonrotating device can also be virtual or tiered.
                        // Expose the reported behavior instead of guessing SSD
                        // from a marketing model string or from TRIM support.
                        media = incursSeekPenalty.Value ? "HDD" : "NonRotating";
                        mediaSource = "SeekPenaltyDescriptor";
                    }
                    else diagnostics.Add("seek-penalty:" + (seek is null ? seekError : "InvalidResponse"));
                }
                if (bus == "NVMe")
                {
                    var nvmeBytes = Query(handle, IoctlStorageQueryProperty, NvmeHealthQuery(), 560, out var nvmeError);
                    nvme = DecodeNvme(nvmeBytes);
                    if (nvme is null) diagnostics.Add("nvme-health:" + (nvmeBytes is null ? nvmeError : "InvalidResponse"));
                    else
                    {
                        temperatureC = nvme.TemperatureC ?? temperatureC;
                        if (nvme.TemperatureC.HasValue) temperatureSource = "NvmeHealthLog";
                        wear = nvme.WearPercent;
                        wearSource = "NvmeHealthLog";
                        remainingLife = Math.Max(0, 100 - nvme.WearPercent);
                        remainingSource = "NvmeHealthLog:PercentageUsed";
                        powerOnCount = nvme.PowerOnCount;
                        hostReadsBytes = nvme.HostReadsBytes;
                        hostWritesBytes = nvme.HostWritesBytes;
                        hours = nvme.PowerOnHours ?? hours;
                        if (nvme.PowerOnHours.HasValue) hoursSource = "NvmeHealthLog";
                        reliabilitySource = "NvmeHealthLog";
                    }
                }
                var prediction = Query(handle, IoctlStoragePredictFailure, [], 516, out var predictionError);
                if (prediction is { Length: >= 516 }) predicted = UInt(prediction, 0) != 0;
                else diagnostics.Add("failure-prediction:" + predictionError);

                if (bus is "ATA" or "ATAPI" or "SATA" or "USB")
                {
                    // The class driver requires READ|WRITE access for this IOCTL,
                    // although the whitelisted commands only read data. Never
                    // request elevation, enable SMART or fall back to write tests.
                    using var smartHandle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (!smartHandle.IsInvalid)
                    {
                        bool sat=bus=="USB";
                        var identify = DecodeAtaIdentify(ReadAtaSector(smartHandle, 0xEC, 0, out _, out var identifyError,sat));
                        if(sat&&(identify is null||!AtaModelMatches(model,identify.Model)))
                        {diagnostics.Add("sat-identify:UnverifiedOrMismatchedModel");identify=null;}
                        if (identify is not null)
                        {
                            // Word 217 is the device's reported rotation rate,
                            // not a guess based on the model name or TRIM.
                            if (identify.MediaType is not null)
                            { media = identify.MediaType; mediaSource = "AtaIdentifyRotationRate"; }
                        }
                        else diagnostics.Add("ata-identify:" + identifyError);
                        string attributeError="UnverifiedIdentity";
                        var attributes = !sat||identify is not null?ReadAtaSector(smartHandle, 0xB0, 0xD0, out ataSource, out attributeError,sat):null;
                        byte[]? thresholds = null;
                        if (attributes is not null)
                        {
                            thresholds = ReadAtaSector(smartHandle, 0xB0, 0xD1, out _, out var thresholdError,sat);
                            if (thresholds is null) diagnostics.Add("ata-thresholds:" + thresholdError);
                        }
                        ata = DecodeAtaSmartPayload(attributes, thresholds);
                        if (ata is null) diagnostics.Add("ata-smart:" + (attributes is null ? attributeError : "InvalidResponse"));
                        else
                        {
                            var decoded = DecodeAtaSensors(identify?.Model ?? model, media, ata.Attributes);
                            ataProfile = decoded.Profile;
                            if((media is "Unknown" or "NonRotating")&&SelectSsdProfile(identify?.Model??model,media)is not null)
                            {media="SSD";mediaSource="AtaSmartModelProfile";}
                            if (decoded.TemperatureC.HasValue)
                            { temperatureC = decoded.TemperatureC; temperatureSource = ataSource + ":" + decoded.TemperatureAttribute; }
                            if (decoded.WearPercent.HasValue)
                            {
                                // For confirmed SC311/SC401, CrystalDiskInfo uses
                                // E9 raw[0] as life remaining. Windows may report
                                // normalized E9 as Wear, which is incompatible.
                                bool conflicts = WearEstimatesConflict(providerWear, decoded.WearPercent);
                                if (conflicts && !IsSc311Family(decoded.Profile))
                                {
                                    diagnostics.Add("ata-life:ConflictingSources");
                                    wear = null; remainingLife = null;
                                    wearSource = remainingSource = "None";
                                }
                                else
                                {
                                    // CrystalDiskInfo's SC311/SC401 profile reads the
                                    // low raw byte of E9 and ignores normalized E9.
                                    if (conflicts) diagnostics.Add("reliability:Sc311WindowsWearDisagreesWithE9");
                                    wear = decoded.WearPercent; wearSource = ataSource + ":" + decoded.Profile + ":E9";
                                    remainingLife = decoded.RemainingLifePercent; remainingSource = wearSource;
                                }
                            }
                            if (decoded.PowerOnHours.HasValue)
                            { hours = decoded.PowerOnHours; hoursSource = ataSource + ":" + decoded.Profile + ":09"; }
                            powerOnCount = decoded.PowerOnCount ?? powerOnCount;
                            hostReadsBytes = decoded.HostReadsBytes ?? hostReadsBytes;
                            hostWritesBytes = decoded.HostWritesBytes ?? hostWritesBytes;
                            if (decoded.Profile == "None") diagnostics.Add("ata-sensors:NoSupportedProfile");
                            if (decoded.LifeConflict || decoded.LifeInvalid)
                            {
                                diagnostics.Add(decoded.LifeInvalid ? "ata-life:InvalidAttribute" : "ata-life:ConflictingAttributes");
                                // Contradictory device attributes cannot be
                                // hidden by an optimistic provider fallback.
                                wear = null; remainingLife = null;
                                wearSource = remainingSource = "None";
                            }
                        }
                    }
                    else diagnostics.Add("ata-smart:" + Win32Code(Marshal.GetLastWin32Error()));
                }
            }
            else diagnostics.Add("physical-drive:" + Win32Code(Marshal.GetLastWin32Error()));
        }
        else diagnostics.Add("physical-drive:Unavailable");
        // The physical type may only become known after IDENTIFY. The provider
        // wear estimate can then be interpreted as SSD endurance, provided an
        // explicit invalid/conflicting device value did not invalidate it.
        if (media == "SSD" && wear.HasValue && !remainingLife.HasValue &&
            (wearSource is "WindowsStorageMethod" or "WindowsStorageAssociation") &&
            !diagnostics.Any(x => x.StartsWith("ata-life:", StringComparison.Ordinal)))
        {
            remainingLife = Math.Max(0, 100 - wear.Value);
            remainingSource = wearSource;
        }
        // No universal remaining-life percentage can be derived for an HDD.
        // A later ATA rotation report may contradict a provider's SSD type.
        if (media == "HDD")
        {
            if (wear.HasValue) diagnostics.Add("reliability:HddWearNotInterpreted");
            wear = null; wearSource = "None";
            remainingLife = null; remainingSource = "None";
        }
        bool? sectorWarning = media == "HDD" && ata is not null ? HddSectorWarning(ata.Attributes) : null;
        if (sectorWarning == true) diagnostics.Add("ata-smart:NonzeroHddSectorWarningAttribute");
        var selected = SelectHealth(health, operational, predicted, nvme?.CriticalWarning, wear, readErrors, writeErrors, nvme?.MediaErrors, ata?.PrefailureThresholdExceeded, wearSource, sectorWarning);
        if(ata?.ThresholdAtLimit==true&&(selected.Code is "Unknown" or "ProviderHealthy" or "NoFailurePredicted" or "EnduranceExceeded"))selected=("AtaThresholdAtLimit","AtaSmartThresholds");
        return new()
        {
            Index = index, DeviceId = path.Length > 0 ? path : "Storage:" + (physicalId ?? "Unknown"),
            Model = model, Manufacturer = manufacturer, MediaType = media, MediaTypeSource = mediaSource, BusType = bus,
            PartitionStyle = (Int(disk, "PartitionStyle") ?? Int(raw, "PartitionStyle")) switch { 1 => "MBR", 2 => "GPT", _ => null },
            TotalBytes = Positive(ULong(physical, "Size")) ?? Positive(ULong(disk, "Size")) ?? Positive(ULong(raw, "Size")),
            Volumes = volumes, HealthCode = selected.Code, HealthSource = selected.Source,
            ProviderHealthStatus = health, OperationalStatus = operational, FailurePredicted = predicted,
            ReliabilitySource = reliabilitySource, AtaPrefailureThresholdExceeded = ata?.PrefailureThresholdExceeded,
            AtaThresholdAtLimit=ata?.ThresholdAtLimit,
            AtaSectorWarning = sectorWarning,
            SmartAttributes = ata?.Attributes ?? [],
            TemperatureC = temperatureC, WearPercent = wear, PowerOnHours = hours,
            PowerOnCount = powerOnCount, HostReadsBytes = hostReadsBytes, HostWritesBytes = hostWritesBytes,
            TemperatureSource = temperatureSource, WearSource = wearSource, PowerOnHoursSource = hoursSource,
            RemainingLifePercent = remainingLife, RemainingLifeSource = remainingSource,
            AtaSmartSource = ataSource, AtaProfile = ataProfile,
            ReadErrorsUncorrected = readErrors, WriteErrorsUncorrected = writeErrors,
            NvmeCriticalWarning = nvme?.CriticalWarning, NvmeMediaErrors = nvme?.MediaErrors,
            NvmeAvailableSparePercent=nvme?.AvailableSparePercent,NvmeSpareThresholdPercent=nvme?.SpareThresholdPercent,
            NvmeSensorTemperaturesC=nvme?.SensorTemperaturesC??[],
            Diagnostics = diagnostics.Distinct().ToArray()
        };
    }


    // WMI storage provider: this source was independently confirmed accessible
    // on the user's SC311 even when IOCTL ATA pass-through returned ERROR 1117.
    // Device identities are never printed into notes or exported in the report.
    static void EnrichWithWmiSmart(List<StorageDisk> disks, List<DiskWmiRow> raw, DiskWmiSession wmi, List<string> notes)
    {
        var data = wmi.Query("SELECT InstanceName,Active,VendorSpecific FROM MSStorageDriver_FailurePredictData",
            "InstanceName", "Active", "VendorSpecific");
        var thresholds = wmi.Query("SELECT InstanceName,Active,VendorSpecific FROM MSStorageDriver_FailurePredictThresholds",
            "InstanceName", "Active", "VendorSpecific");
        var predictions = wmi.Query("SELECT InstanceName,Active,PredictFailure FROM MSStorageDriver_FailurePredictStatus",
            "InstanceName", "Active", "PredictFailure");
        notes.Add("wmi-smart:entries=" + data.Count.ToString(CultureInfo.InvariantCulture));
        int applied = 0;
        foreach (var entry in data)
        {
            if (entry.TryGetValue("Active", out object? active) && active is bool b && !b) continue;
            string? instance = Text(entry, "InstanceName");
            string? identity = NormalizeSmartPnp(instance, true);
            if (identity is null) { notes.Add("wmi-smart:UnboundInstance"); continue; }
            var matches = disks.Select((disk, index) => new { disk, index })
                .Where(x => x.disk.SmartAttributes.Count == 0 &&
                    (string.Equals(identity, NormalizeSmartPnp(x.disk.DeviceId, false), StringComparison.OrdinalIgnoreCase)
                    || raw.Any(r => x.disk.Index.HasValue && Int(r, "Index") == x.disk.Index &&
                        string.Equals(identity, NormalizeSmartPnp(Text(r, "PNPDeviceID"), false), StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            // A zero or multiple match has insufficient disk identity evidence.
            if (matches.Length != 1) { notes.Add(matches.Length == 0 ? "wmi-smart:UnmatchedIdentity" : "wmi-smart:AmbiguousIdentity"); continue; }
            var sector = SmartByteArray(entry);
            var sameThresholds = thresholds.Where(t =>
                string.Equals(instance, Text(t, "InstanceName"), StringComparison.OrdinalIgnoreCase) &&
                (!t.TryGetValue("Active", out var a) || a is not bool act || act)).ToArray();
            var thresholdSector = sameThresholds.Length == 1 ? SmartByteArray(sameThresholds[0]) : null;
            var smart = DecodeAtaSmartPayload(sector, thresholdSector);
            if (smart is null) { notes.Add("wmi-smart:InvalidSmartSector"); continue; }
            var original = matches[0].disk;
            var decoded = DecodeAtaSensors(original.Model, original.MediaType, smart.Attributes);
            // Preserve raw measurements from the already independently bound
            // storage provider; reject misleading Windows Wear on the SC311.
            bool sc311 = IsSc311Family(decoded.Profile);
            bool conflict = !sc311 && WearEstimatesConflict(original.WearPercent, decoded.WearPercent);
            bool invalidLife = decoded.LifeInvalid || decoded.LifeConflict || conflict;
            double? remaining = invalidLife ? null : decoded.RemainingLifePercent ?? original.RemainingLifePercent;
            int? wear = invalidLife ? null : decoded.WearPercent ?? original.WearPercent;
            var predictionMatches = predictions.Where(t =>
                string.Equals(instance, Text(t, "InstanceName"), StringComparison.OrdinalIgnoreCase)).ToArray();
            bool? predicts = predictionMatches.Length == 1 && predictionMatches[0].TryGetValue("PredictFailure", out var prediction)
                && prediction is bool predicted ? predicted : original.FailurePredicted;
            bool? sectorWarning = original.MediaType == "HDD" ? HddSectorWarning(smart.Attributes) : null;
            var selected = SelectHealth(original.ProviderHealthStatus, original.OperationalStatus.ToArray(), predicts,
                original.NvmeCriticalWarning, wear, original.ReadErrorsUncorrected, original.WriteErrorsUncorrected,
                original.NvmeMediaErrors, smart.PrefailureThresholdExceeded, "WindowsWmiFailurePredictData", sectorWarning);
            var diagnostics = original.Diagnostics.ToList();
            diagnostics.Add("wmi-smart:BoundByPnPInstance");
            if (invalidLife) diagnostics.Add("wmi-smart:ConflictingOrInvalidLife");
            if (sc311) diagnostics.Add("wmi-smart:Sc311RawE9Life");
            var enriched = original with
            {
                SmartAttributes = smart.Attributes, AtaSmartSource = "WindowsWmiFailurePredictData", AtaProfile = decoded.Profile,
                TemperatureC = original.TemperatureC ?? decoded.TemperatureC,
                TemperatureSource = original.TemperatureC.HasValue ? original.TemperatureSource :
                    decoded.TemperatureC.HasValue ? "WindowsWmiFailurePredictData:C2" : "None",
                RemainingLifePercent = remaining,
                RemainingLifeSource = invalidLife ? "None" : decoded.RemainingLifePercent.HasValue ? "WindowsWmiFailurePredictData:" + decoded.Profile + ":E9" : original.RemainingLifeSource,
                WearPercent = wear,
                WearSource = invalidLife ? "None" : decoded.WearPercent.HasValue ? "WindowsWmiFailurePredictData:" + decoded.Profile + ":E9" : original.WearSource,
                PowerOnHours = original.PowerOnHours ?? decoded.PowerOnHours,
                PowerOnHoursSource = original.PowerOnHours.HasValue ? original.PowerOnHoursSource :
                    decoded.PowerOnHours.HasValue ? "WindowsWmiFailurePredictData:09" : "None",
                PowerOnCount = original.PowerOnCount ?? decoded.PowerOnCount,
                HostReadsBytes = original.HostReadsBytes ?? decoded.HostReadsBytes,
                HostWritesBytes = original.HostWritesBytes ?? decoded.HostWritesBytes,
                FailurePredicted = predicts,
                AtaPrefailureThresholdExceeded = smart.PrefailureThresholdExceeded,
                AtaThresholdAtLimit = smart.ThresholdAtLimit,
                AtaSectorWarning = sectorWarning,
                HealthCode = selected.Code, HealthSource = selected.Source,
                Diagnostics = diagnostics.Distinct().ToArray()
            };
            disks[matches[0].index] = PromoteAtaThresholdHealth(enriched);
            applied++;
        }
        notes.Add("wmi-smart:bound=" + applied.ToString(CultureInfo.InvariantCulture));
    }

    static byte[]? SmartByteArray(DiskWmiRow row)
    {
        if (!row.TryGetValue("VendorSpecific", out var value)) return null;
        return value switch
        {
            byte[] b when b.Length == 512 => b,
            // A WMI provider can expose the 362-byte attribute area without the
            // checksum. Never manufacture a checksum or call it a valid sector.
            _ => null
        };
    }

    // WMI InstanceName: SCSI\Disk&Ven_SK&Prod_hynix\4&...&000100_0
    // Native device path: \\?\scsi#disk&ven_sk&prod_hynix#4&...&000100#{GUID}
    // WMI PNPDeviceID: SCSI\Disk&Ven_SK&Prod_hynix\4&...&000100
    // Match all segments exactly, ignoring only the WMI _LUN suffix.
    internal static string? NormalizeSmartPnp(string? value, bool wmiInstance)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim().ToUpperInvariant();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            string[] parts = text[4..].Split('#');
            if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
                return null;
            text = parts[0] + @"\" + parts[1] + @"\" + parts[2];
        }
        // Accept only the expected PnP multi-segment hardware instance syntax.
        string[] fields = text.Split('\\');
        if (fields.Length != 3 || fields.Any(string.IsNullOrWhiteSpace)) return null;
        if (wmiInstance)
        {
            int suffix = fields[2].LastIndexOf('_');
            if (suffix < 1 || !int.TryParse(fields[2][(suffix + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out int lun) || lun < 0 || lun > 255) return null;
            fields[2] = fields[2][..suffix];
        }
        return string.Join(@"\", fields);
    }

    static DiskWmiRow? AssociatedCounter(DiskWmiRow? source, string sourceClass, string sourceField, List<DiskWmiRow> links, List<DiskWmiRow> counters)
    {
        string? sourcePath = WmiReferenceKey(Text(source, "__RELPATH"), sourceClass);
        if (sourcePath is null) return null;
        var targets = links.Where(link => WmiReferenceKey(Text(link, sourceField), sourceClass) == sourcePath)
            .Select(link => WmiReferenceKey(Text(link, "StorageReliabilityCounter"), "MSFT_StorageReliabilityCounter"))
            .Where(path => path is not null).Distinct(StringComparer.Ordinal).ToArray();
        // Multiple target objects or duplicate counter instances are ambiguous.
        if (targets.Length != 1) return null;
        var matches = counters.Where(counter => WmiReferenceKey(Text(counter, "__RELPATH"), "MSFT_StorageReliabilityCounter") == targets[0]).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    static bool MethodCounterMatches(DiskWmiRow source, string sourceClass, DiskWmiRow result)
    {
        if (!string.Equals(Text(result, "__CLASS"), "MSFT_StorageReliabilityCounter", StringComparison.OrdinalIgnoreCase) ||
            sourceClass is not ("MSFT_PhysicalDisk" or "MSFT_Disk")) return false;
        string? expected = Text(source, sourceClass == "MSFT_PhysicalDisk" ? "DeviceId" : "Number");
        string? actual = Text(result, "DeviceId");
        // The embedded result belongs to the exact method input object. DeviceId
        // is only an additional contradiction check, never a cross-class join.
        return expected is null || actual is null || string.Equals(expected, actual, StringComparison.Ordinal);
    }

    static string? WmiReferenceKey(string? path, string className)
    {
        if (path is null) return null;
        int start = path.IndexOf(className + ".", StringComparison.OrdinalIgnoreCase);
        // __RELPATH and an association's absolute reference differ only in their
        // namespace prefix. Preserve every escaped character of the actual key.
        return start >= 0 ? className + path[(start + className.Length)..] : null;
    }

    static List<DiskWmiRow> ReadNativeDisks(List<string> notes)
    {
        var rows = new List<DiskWmiRow>();
        var guid = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) { notes.Add("native-disk-enumeration:" + Win32Code(Marshal.GetLastWin32Error())); return rows; }
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new DiskInterfaceData { Size = Marshal.SizeOf<DiskInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 259) notes.Add("native-disk-enumeration:" + Win32Code(error));
                    break;
                }
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed < 8 || needed > 1048576) { notes.Add("native-disk-enumeration:InvalidResponse"); continue; }
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                IntPtr deviceInfo = Marshal.AllocHGlobal(Marshal.SizeOf<DiskDeviceInfoData>());
                try
                {
                    Marshal.StructureToPtr(new DiskDeviceInfoData { Size = Marshal.SizeOf<DiskDeviceInfoData>() }, deviceInfo, false);
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, deviceInfo))
                    { notes.Add("native-disk-detail:" + Win32Code(Marshal.GetLastWin32Error())); continue; }
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var row = new DiskWmiRow
                    {
                        ["DeviceID"] = path,
                        ["Model"] = NativeDeviceProperty(set, deviceInfo, 12) ?? NativeDeviceProperty(set, deviceInfo, 0),
                        ["Manufacturer"] = Brand(NativeDeviceProperty(set, deviceInfo, 11))
                    };
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (!handle.IsInvalid)
                    {
                        var number = Query(handle, 0x002D1080, [], 12, out _); // STORAGE_GET_DEVICE_NUMBER
                        if (number is { Length: >= 12 } && UInt(number, 0) == 7 && UInt(number, 4) <= int.MaxValue)
                            row["Index"] = (int)UInt(number, 4);
                        var descriptor = Query(handle, IoctlStorageQueryProperty, Property(0), 4096, out _);
                        if (descriptor is { Length: >= 36 })
                        {
                            row["Manufacturer"] = Brand(AsciiAt(descriptor, UInt(descriptor, 12))) ?? row["Manufacturer"];
                            row["Model"] ??= AsciiAt(descriptor, UInt(descriptor, 16));
                            row["InterfaceType"] = Bus((int)UInt(descriptor, 28));
                        }
                        var geometry = Query(handle, 0x000700A0, [], 1024, out _); // DISK_GET_DRIVE_GEOMETRY_EX
                        if (geometry is { Length: >= 32 })
                        {
                            long size = BitConverter.ToInt64(geometry, 24);
                            if (size > 0) row["Size"] = (ulong)size;
                            if (geometry.Length >= 40 && UInt(geometry, 32) >= 8) row["PartitionStyle"] = UInt(geometry, 36) switch { 0 => 1, 1 => 2, _ => 0 };
                        }
                    }
                    else notes.Add("native-disk:" + Win32Code(Marshal.GetLastWin32Error()));
                    if (!rows.Any(existing => Int(row, "Index").HasValue && Int(existing, "Index") == Int(row, "Index"))) rows.Add(row);
                }
                finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(deviceInfo); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return rows;
    }

    static string? NativeDeviceProperty(IntPtr set, IntPtr deviceInfo, uint property)
    {
        var bytes = new byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(set, deviceInfo, property, out uint type, bytes, (uint)bytes.Length, out uint required) || type != 1 || required < 2 || required > bytes.Length) return null;
        string value = Encoding.Unicode.GetString(bytes, 0, (int)required).TrimEnd('\0').Trim();
        return value.Length > 0 && !value.Any(char.IsControl) ? value : null;
    }

    static void ReadNativeVolumes(List<DiskWmiRow> rows, Dictionary<string, HashSet<int>> owners, List<string> notes)
    {
        foreach (var volume in DriveInfo.GetDrives())
        {
            try
            {
                if (volume.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                string name = volume.Name.TrimEnd('\\');
                using var handle = CreateFile(@"\\.\" + name, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) { notes.Add("native-volume:" + Win32Code(Marshal.GetLastWin32Error())); continue; }
                var extents = Query(handle, 0x00560000, [], 65536, out var extentError);
                var numbers = DecodeVolumeExtents(extents);
                if (numbers is null) { notes.Add("volume-extents:" + (extents is null ? extentError : "InvalidResponse")); continue; }
                owners[name] = numbers;
                var row = rows.FirstOrDefault(x => string.Equals(Text(x, "DeviceID"), name, StringComparison.OrdinalIgnoreCase));
                if (row is null) { row = new() { ["DeviceID"] = name }; rows.Add(row); }
                if (!volume.IsReady) continue;
                row["FileSystem"] = volume.DriveFormat;
                if (volume.TotalSize > 0) row["Size"] = (ulong)volume.TotalSize;
                // TotalFreeSpace includes quota-reserved space and is the actual
                // filesystem free quantity; AvailableFreeSpace is user-specific.
                if (volume.TotalFreeSpace >= 0) row["FreeSpace"] = (ulong)volume.TotalFreeSpace;
            }
            catch (Exception ex) { notes.Add("native-volume:" + ErrorCode(ex)); }
        }
    }

    static HashSet<int>? DecodeVolumeExtents(byte[]? bytes)
    {
        // VOLUME_DISK_EXTENTS begins with DWORD plus alignment padding. Each
        // DISK_EXTENT contains DWORD + padding + two LARGE_INTEGERs (24 bytes).
        if (bytes is null || bytes.Length < 8) return null;
        uint count = UInt(bytes, 0);
        if (count == 0 || 8UL + count * 24UL > (ulong)bytes.Length) return null;
        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            uint number = UInt(bytes, 8 + i * 24);
            if (number > int.MaxValue) return null;
            result.Add((int)number);
        }
        return result;
    }

    static (string Code, string Source) SelectHealth(ushort? health, ushort[] operational, bool? predicted, byte? warning, int? wear, ulong? readErrors, ulong? writeErrors, ulong? mediaErrors, bool? ataThresholdExceeded = null, string? wearSource = null, bool? ataSectorWarning = null)
    {
        if (predicted == true) return ("FailurePredicted", "FailurePrediction");
        if (health == 2 || operational.Any(x => x is 6 or 7 or 0xD004 or 0xD007)) return ("Unhealthy", "WindowsStorage");
        if (warning>0) return ("Unhealthy", "NvmeHealthLog");
        if (ataThresholdExceeded == true) return ("AtaThresholdExceeded", "AtaSmartThresholds");
        if (health == 1 || operational.Any(x => x is 3 or 4 or 5 or 0xD005 or 0xD006 or 0xD008)) return ("Warning", "WindowsStorage");
        if (readErrors > 0 || writeErrors > 0) return ("Warning", "WindowsStorage");
        if (mediaErrors > 0) return ("Warning", "NvmeHealthLog");
        if (ataSectorWarning == true) return ("AtaSectorWarning", "AtaSmartSectors");
        // Endurance consumption is not a controller failure flag. Keep it
        // separate so a genuine zero remaining resource is displayed as such.
        if (wear >= 100) return ("EnduranceExceeded", wearSource ?? (warning.HasValue ? "NvmeHealthLog" : "WindowsStorage"));
        if (warning == 0) return ("NvmeNoCriticalWarning", "NvmeHealthLog");
        if (health == 0) return ("ProviderHealthy", "WindowsStorage");
        if (predicted == false) return ("NoFailurePredicted", "FailurePrediction");
        return ("Unknown", "None");
    }

    internal static StorageDisk PromoteAtaThresholdHealth(StorageDisk disk)
    {
        // Preserve stronger existing warnings from Windows, NVMe or libcdi.
        if (disk.AtaPrefailureThresholdExceeded == true &&
            disk.HealthCode is not ("FailurePredicted" or "Unhealthy" or "AtaThresholdExceeded"))
            return disk with { HealthCode = "AtaThresholdExceeded", HealthSource = "AtaSmartThresholds" };
        if (disk.AtaThresholdAtLimit == true &&
            disk.HealthCode is "Unknown" or "ProviderHealthy" or "NoFailurePredicted" or "NvmeNoCriticalWarning" or "EnduranceExceeded")
            return disk with { HealthCode = "AtaThresholdAtLimit", HealthSource = "AtaSmartThresholds" };
        return disk;
    }

    // Two nominal wear percentages that differ by more than ten points must
    // not be silently preferred over each other. Device wear is not a generic
    // SMART attribute: callers only pass explicitly supported SSD profiles.
    internal static bool IsSc311Family(string? profile) => profile == "SKhynixSC311SC401";

    internal static bool WearEstimatesConflict(int? providerWear, int? decodedWear) =>
        providerWear.HasValue && decodedWear.HasValue &&
        providerWear.Value is >= 0 and <= 255 && decodedWear.Value is >= 0 and <= 255 &&
        Math.Abs(providerWear.Value - decodedWear.Value) > 10;

    static bool? HddSectorWarning(IReadOnlyList<StorageSmartAttribute> attributes)
    {
        // These conventional rotating-ATA warning attributes are checked by
        // CrystalDiskInfo independently of the normalized failure threshold.
        // Do not apply this rule to SSDs, whose attribute meanings can differ,
        // and do not convert the raw values into a resource percentage/count.
        var values = attributes.Where(x => x.Id is 0x05 or 0xC5 or 0xC6)
            .Where(x => x.Raw < (1UL << 48) && (uint)x.Raw != uint.MaxValue).ToArray();
        if(values.Length==0)return null;
        if(values.Any(x=>(ushort)x.Raw>0))return true;
        return values.Any(x=>x.Raw!=0)?null:false;
    }

    static StorageVolume MakeVolume(DiskWmiRow row, Dictionary<string, HashSet<int>> owners)
    {
        string name = Text(row, "DeviceID") ?? "";
        ulong? total = Positive(ULong(row, "Size")), free = ULong(row, "FreeSpace");
        if (free > total) free = null;
        return new(name, Text(row, "FileSystem"), total, free, owners.TryGetValue(name, out var indices) && indices.Count > 1);
    }

    static Dictionary<string, HashSet<int>> MapVolumeOwners(List<DiskWmiRow> partitions, List<DiskWmiRow> links)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            string? partitionId = ReferenceDeviceId(Text(link, "Antecedent"));
            string? volumeId = ReferenceDeviceId(Text(link, "Dependent"));
            int? index = Int(partitions.FirstOrDefault(x => string.Equals(Text(x, "DeviceID"), partitionId, StringComparison.OrdinalIgnoreCase)), "DiskIndex");
            if (volumeId is null || !index.HasValue) continue;
            if (!result.TryGetValue(volumeId, out var owners)) result[volumeId] = owners = [];
            owners.Add(index.Value);
        }
        return result;
    }

    static string? ReferenceDeviceId(string? value)
    {
        if (value is null) return null;
        const string marker = "DeviceID=\"";
        int start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var builder = new StringBuilder();
        for (int i = start; i < value.Length; i++)
        {
            if (value[i] == '"') return builder.ToString();
            if (value[i] == '\\' && i + 1 < value.Length) builder.Append(value[++i]);
            else builder.Append(value[i]);
        }
        return null;
    }

    static DiskWmiRow? MatchPhysical(DiskWmiRow raw, DiskWmiRow? disk, DiskWmiRow[] physical)
    {
        string? unique = Identity(Text(disk, "UniqueId"));
        string? serial = Identity(Text(disk, "SerialNumber") ?? Text(raw, "SerialNumber"));
        var matches = physical.Where(x => unique is not null && Identity(Text(x, "UniqueId")) == unique).ToArray();
        if (matches.Length == 1) return matches[0];
        matches = physical.Where(x => serial is not null && Identity(Text(x, "SerialNumber")) == serial).ToArray();
        if (matches.Length == 1) return matches[0];
        // DeviceId is provider-defined. A numerical match needs corroborating
        // model and size; otherwise this provider's health must remain unmapped.
        int? index = Int(raw, "Index");
        string? model = Identity(Text(raw, "Model"));
        ulong? size = ULong(raw, "Size");
        matches = physical.Where(x => index.HasValue && Int(x, "DeviceId") == index && size.HasValue &&
            SizeClose(size.Value, ULong(x, "Size")) && model is not null &&
            (Identity(Text(x, "Model")) == model || Identity(Text(x, "FriendlyName")) == model)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    static bool SizeClose(ulong size, ulong? other) => other.HasValue && (size > other ? size - other.Value : other.Value - size) < 1048576;
    static string? Identity(string? value) => value is null ? null : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()) is { Length: > 0 } text ? text : null;
    static string? Brand(string? value) => value is null || value.Contains("standard", StringComparison.OrdinalIgnoreCase) || value == "(Standard disk drives)" ? null : value;
    static ulong? Positive(ulong? value) => value > 0 ? value : null;
    static string? Bus(int? value) => value switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Virtual", 16 => "Storage Spaces", 17 => "NVMe", 19 => "UFS", _ => null
    };
    static string? Text(DiskWmiRow? row, string field)
    {
        string? value = row?.GetValueOrDefault(field)?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : value;
    }
    static int? Int(DiskWmiRow? row, string field) => int.TryParse(Text(row, field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    static ulong? ULong(DiskWmiRow? row, string field) => ulong.TryParse(Text(row, field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    static ushort? UShort(DiskWmiRow? row, string field) => ushort.TryParse(Text(row, field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    static ushort[] UShortArray(DiskWmiRow? row, string field)
    {
        if (row?.GetValueOrDefault(field) is not Array values) return [];
        var result = new List<ushort>();
        foreach (var value in values) if (ushort.TryParse(value?.ToString(), out var n)) result.Add(n);
        return result.ToArray();
    }

    static bool? DecodeSeekPenalty(byte[]? bytes)
    {
        // Version is sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR), including padding.
        if (bytes is null || bytes.Length < 12) return null;
        uint version = UInt(bytes, 0), size = UInt(bytes, 4);
        if (version < 12 || size < version || size > bytes.Length || bytes[8] > 1) return null;
        return bytes[8] == 1;
    }

    static double? DecodeStorageTemperature(byte[]? bytes)
    {
        // ntddstor.h: fixed prefix 24 bytes; each STORAGE_TEMPERATURE_INFO is
        // 16 bytes. Index 0 is the device/composite sensor, not the adapter.
        if (bytes is null || bytes.Length < 40 || UInt(bytes, 0) < 40 ||
            UInt(bytes, 4) > bytes.Length || UInt(bytes, 4) < UInt(bytes, 0)) return null;
        ushort count = BitConverter.ToUInt16(bytes, 12);
        if (count == 0 || 24UL + count * 16UL > UInt(bytes, 4)) return null;
        var indices = new HashSet<ushort>();
        double? composite = null;
        for (int p = 24; p < 24 + count * 16; p += 16)
        {
            ushort index = BitConverter.ToUInt16(bytes, p);
            if (!indices.Add(index)) return null;
            short celsius = BitConverter.ToInt16(bytes, p + 2);
            // 0x8000 means not reported; zero Celsius is valid here.
            if (index == 0 && celsius is >= -40 and <= 150) composite = celsius;
        }
        return composite;
    }

    sealed record AtaIdentify(string Model, string? MediaType);
    static AtaIdentify? DecodeAtaIdentify(byte[]? data)
    {
        if (data is null || data.Length != 512) return null;
        ushort configuration = BitConverter.ToUInt16(data, 0);
        if (configuration is 0 or ushort.MaxValue || (configuration & 0x8004) != 0) return null;
        // The optional ATA integrity word is checked only when its signature
        // declares a checksum. Old compliant devices may omit that signature.
        if (data[510] == 0xA5 && (data.Sum(x => (int)x) & 255) != 0) return null;
        var modelBytes = data[54..94];
        for (int i = 0; i < modelBytes.Length; i += 2) (modelBytes[i], modelBytes[i + 1]) = (modelBytes[i + 1], modelBytes[i]);
        string model = Encoding.ASCII.GetString(modelBytes).Trim(' ', '\0');
        if (model.Length == 0 || model.Any(ch => ch < 32 || ch > 126)) return null;
        ushort rotation = BitConverter.ToUInt16(data, 434); // ATA word 217
        string? media = rotation == 1 ? "SSD" : rotation is >= 0x401 and < 0xFFFF ? "HDD" : null;
        return new(model, media);
    }

    sealed record AtaSensors(string Profile, double? TemperatureC, string TemperatureAttribute,
        int? WearPercent, int? RemainingLifePercent, ulong? PowerOnHours, bool LifeConflict = false, bool LifeInvalid = false,
        ulong? PowerOnCount = null, ulong? HostReadsBytes = null, ulong? HostWritesBytes = null);

    static AtaSensors DecodeAtaSensors(string model, string media, IReadOnlyList<StorageSmartAttribute> attributes) => DecodeCompatibleAtaSensors(model,media,attributes);

    // Compatibility mapping above is informed by the following work. This notice
    // travels with the source and the portable executable's embedded attribution.
    internal const string CrystalDiskInfoNotice = """
        CrystalDiskInfo - SMART transport and device interpretation compatibility rules
        https://github.com/hiyohiyo/CrystalDiskInfo/blob/master/AtaSmart.cpp
        Source reviewed 2026-10-20/21: CheckDiskStatus, IsSsdSKhynix, IsSsdMicronMU03, FillSmartData, SAT transport.
        MIT License
        Copyright (c) 2008-2023 hiyohiyo
        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:
        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.
        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    sealed record AtaSmart(IReadOnlyList<StorageSmartAttribute> Attributes, bool? PrefailureThresholdExceeded,bool? ThresholdAtLimit);
    static byte[]? ReadAtaSector(SafeFileHandle handle, byte command, byte feature, out string source, out string error,bool sat=false)
    {
        // Only PIO data-in IDENTIFY and the two SMART read pages are allowed.
        // This function cannot enable SMART, issue a self-test or write sectors.
        if (!((command == 0xEC && feature == 0) || (command == 0xB0 && feature is 0xD0 or 0xD1)))
            throw new ArgumentOutOfRangeException(nameof(command));
        if(sat)return ReadSatSector(handle,command,feature,out source,out error);
        var pass = AtaPassThroughQuery(command, feature, IntPtr.Size);
        var response = Query(handle, IoctlAtaPassThrough, pass, pass.Length, out var passError);
        var payload = DecodeAtaPassThrough(response, IntPtr.Size);
        if (payload is not null && (command == 0xEC ? DecodeAtaIdentify(payload) is not null : ValidAtaSmartSector(payload)))
        { source = "AtaPassThrough"; error = "None"; return payload; }
        var legacy = Query(handle, IoctlSmartReceiveDriveData, command == 0xEC ? AtaIdentifyQuery() : AtaSmartQuery(feature), 528, out var legacyError);
        payload = LegacyAtaSector(legacy);
        if (payload is not null && (command == 0xEC ? DecodeAtaIdentify(payload) is not null : ValidAtaSmartSector(payload)))
        { source = "AtaSmartReceiveDriveData"; error = "None"; return payload; }
        source = "None";
        error = (response is null ? passError : "InvalidPassThroughResponse") + "/" +
            (legacy is null ? legacyError : "InvalidLegacyResponse");
        return null;
    }

    static byte[] AtaPassThroughQuery(byte command, byte feature, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (!((command == 0xEC && feature == 0) || (command == 0xB0 && feature is 0xD0 or 0xD1)))
            throw new ArgumentOutOfRangeException(nameof(command));
        // ATA_PASS_THROUGH_EX has default native packing. ULONG_PTR inserts
        // padding at byte20 on x64: offset field24, previous regs32, current40.
        int header = pointerSize == 8 ? 48 : 40, dataOffsetField = pointerSize == 8 ? 24 : 20;
        int taskFile = pointerSize == 8 ? 40 : 32;
        var bytes = new byte[header + 512];
        BitConverter.GetBytes((ushort)header).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)3).CopyTo(bytes, 2); // DRDY_REQUIRED | DATA_IN
        Put(bytes, 8, 512); Put(bytes, 12, 5); // maximum command timeout seconds
        if (pointerSize == 8) BitConverter.GetBytes((ulong)header).CopyTo(bytes, dataOffsetField);
        else Put(bytes, dataOffsetField, (uint)header);
        bytes[taskFile] = feature; bytes[taskFile + 1] = 1; bytes[taskFile + 2] = 1;
        if (command == 0xB0) { bytes[taskFile + 3] = 0x4F; bytes[taskFile + 4] = 0xC2; }
        bytes[taskFile + 5] = 0xA0; bytes[taskFile + 6] = command;
        return bytes;
    }

    static byte[]? DecodeAtaPassThrough(byte[]? response, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) return null;
        int header = pointerSize == 8 ? 48 : 40, offsetField = pointerSize == 8 ? 24 : 20;
        int taskFile = pointerSize == 8 ? 40 : 32;
        if (response is null || response.Length < header || BitConverter.ToUInt16(response, 0) != header || UInt(response, 8) != 512) return null;
        ulong offset = pointerSize == 8 ? BitConverter.ToUInt64(response, offsetField) : UInt(response, offsetField);
        // A driver returning success can still report an ATA error, busy/DRQ,
        // short transfer, or corrupt offset. Never decode that buffer as data.
        byte status = response[taskFile + 6];
        if ((status & 0xA9) != 0 || (status & 0x40) == 0 || offset < (ulong)header ||
            offset > (ulong)response.Length || 512UL > (ulong)response.Length - offset) return null;
        return response[(int)offset..((int)offset + 512)];
    }

    static byte[] AtaIdentifyQuery()
    {
        var bytes = new byte[32]; Put(bytes, 0, 512);
        bytes[5] = 1; bytes[6] = 1; bytes[9] = 0xA0; bytes[10] = 0xEC;
        return bytes;
    }
    static byte[] AtaSmartQuery(byte feature)
    {
        // SENDCMDINPARAMS without the trailing variable buffer. Explicitly
        // whitelist the two SMART read subcommands: no arbitrary ATA command.
        if (feature is not (0xD0 or 0xD1)) throw new ArgumentOutOfRangeException(nameof(feature));
        var bytes = new byte[32];
        Put(bytes, 0, 512);
        bytes[4] = feature; // READ_ATTRIBUTES / READ_THRESHOLDS
        bytes[5] = 1;      // sector count
        bytes[6] = 1;      // sector number
        bytes[7] = 0x4F; bytes[8] = 0xC2; // SMART signature
        bytes[9] = 0xA0;   // device register; the open handle selects the device
        bytes[10] = 0xB0;  // SMART_CMD
        return bytes;
    }

    static AtaSmart? DecodeAtaSmart(byte[]? attributes, byte[]? thresholds)
    {
        var data = AtaPayload(attributes);
        var limitData = AtaPayload(thresholds);
        return DecodeAtaSmartPayload(data, limitData);
    }

    static AtaSmart? DecodeAtaSmartPayload(byte[]? data, byte[]? limitData)
    {
        if (!ValidAtaSmartSector(data)) return null;
        if (!ValidAtaSmartSector(limitData)) limitData = null;
        var limits = new Dictionary<byte, byte>();
        if (limitData is not null)
        {
            for (int p = 2; p < 362; p += 12)
            {
                byte id = limitData[p], value = limitData[p + 1];
                if (id == 0) continue;
                if (!limits.TryAdd(id, value)) { limitData = null; limits.Clear(); break; }
            }
        }
        var items = new List<StorageSmartAttribute>();
        var ids = new HashSet<byte>();
        for (int p = 2; p < 362; p += 12)
        {
            byte id = data![p];
            if (id == 0) continue;
            if (!ids.Add(id)) return null;
            ulong raw = 0;
            for (int j = 0; j < 6; j++) raw |= (ulong)data[p + 5 + j] << (j * 8);
            byte? threshold = limits.TryGetValue(id, out byte value) ? value : null;
            bool prefailure = (BitConverter.ToUInt16(data, p + 1) & 1) != 0;
            items.Add(new(id, data[p + 3], data[p + 4], threshold, raw, prefailure));
        }
        if (items.Count == 0) return null;
        // Shared with DiskInfoToolkit: for Pre-fail, reaching the device's
        // normalized threshold (equality included) is a failure condition.
        // A non-Pre-fail supported health attribute at equality is a caution.
        var thresholdState = ClassifyAtaThresholds(items, limitData is not null);
        return new(items, thresholdState.Exceeded, thresholdState.AtLimit);
    }
    internal static (bool? Exceeded, bool? AtLimit) ClassifyAtaThresholds(IReadOnlyList<StorageSmartAttribute> attributes, bool trustworthyThresholdPage)
    {
        if (!trustworthyThresholdPage) return (null, null);
        var rated = attributes.Where(a => a.Id != 0xC2 &&
            (a.Prefailure || NormalizedHealthAttribute(a.Id)) &&
            a.Threshold is > 0 and < 254 && a.Current < 254).ToArray();
        if (rated.Length == 0) return (null, null);
        bool exceeded = rated.Any(a => a.Current < a.Threshold ||
            (a.Prefailure && a.Current == a.Threshold));
        bool atLimit = rated.Any(a => !a.Prefailure && a.Current == a.Threshold);
        return (exceeded, atLimit);
    }
    static bool NormalizedHealthAttribute(byte id)=>id is >=0x01 and <=0x0D or 0x16 or 0xB1 or >=0xBB and <=0xBD or >=0xBF and <=0xC1 or >=0xC3 and <=0xD1 or >=0xD3 and <=0xD4 or >=0xDC and <=0xE4 or >=0xE6 and <=0xE7 or 0xE9 or 0xF0 or 0xFA or 0xFE;

    static byte[]? AtaPayload(byte[]? response)
    {
        var data = LegacyAtaSector(response);
        return ValidAtaSmartSector(data) ? data : null;
    }

    static byte[]? LegacyAtaSector(byte[]? response)
    {
        // SENDCMDOUTPARAMS: ULONG length + DRIVERSTATUS (12 bytes) + sector.
        if (response is null || response.Length < 528 || UInt(response, 0) != 512 || response[4] != 0 || response[5] != 0) return null;
        return response[16..528];
    }

    static bool ValidAtaSmartSector(byte[]? data)
    {
        if (data is null || data.Length != 512) return false;
        ushort revision = BitConverter.ToUInt16(data, 0);
        return revision is not (0 or ushort.MaxValue) && (data.Sum(x => (int)x) & 255) == 0;
    }

    sealed record NvmeLog(byte CriticalWarning, double? TemperatureC, int WearPercent, ulong? PowerOnHours, ulong? MediaErrors,
        byte? AvailableSparePercent,byte? SpareThresholdPercent,IReadOnlyList<double?> SensorTemperaturesC,
        ulong? PowerOnCount, ulong? HostReadsBytes, ulong? HostWritesBytes);
    static NvmeLog? DecodeNvme(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 48 || UInt(bytes, 0) != 48 || UInt(bytes, 4) != 48 || UInt(bytes, 8) != 3 || UInt(bytes, 12) != 2) return null;
        uint offset = UInt(bytes, 24), length = UInt(bytes, 28);
        if (offset < 40 || length < 512 || 8UL + offset + length > (ulong)bytes.Length) return null;
        int start = checked((int)(8 + offset));
        if (!bytes.AsSpan(start, 512).ContainsAnyExcept((byte)0)) return null;
        if(bytes[start+3]>100||bytes[start+4]>100)return null;
        double? temp=DecodeKelvin(BitConverter.ToUInt16(bytes,start+1));
        var sensors=Enumerable.Range(0,8).Select(i=>DecodeKelvin(BitConverter.ToUInt16(bytes,start+200+i*2))).ToArray();
        byte? spare=bytes[start+3]<=100?bytes[start+3]:null;
        byte? threshold=bytes[start+4] is >0 and <=100?bytes[start+4]:null;
        return new(bytes[start], temp, bytes[start + 5], Read128(bytes, start + 128), Read128(bytes, start + 160),spare,threshold,sensors,
            Read128(bytes,start+112), NvmeDataBytes(bytes,start+32), NvmeDataBytes(bytes,start+48));
    }
    // NVMe data units are thousands of 512-byte units (Microsoft NVME_HEALTH_INFO_LOG).
    static ulong? NvmeDataBytes(byte[] bytes,int start)
    {
        var units=Read128(bytes,start);
        return units.HasValue&&units.Value<=ulong.MaxValue/512000?units.Value*512000:null;
    }
    static double? DecodeKelvin(ushort kelvin)=>kelvin is >0 and <65535&&kelvin-273.15 is >=-40 and <=150?kelvin-273.15:null;
    static ulong? Read128(byte[] bytes, int start)
    {
        for (int i = start + 8; i < start + 16; i++) if (bytes[i] != 0) return null;
        return BitConverter.ToUInt64(bytes, start);
    }
    static byte[] Property(uint id) { var b = new byte[12]; Put(b, 0, id); return b; }
    static byte[] NvmeHealthQuery()
    {
        var bytes = new byte[560];
        Put(bytes, 0, 50); // StorageDeviceProtocolSpecificProperty
        Put(bytes, 8, 3); // ProtocolTypeNvme
        Put(bytes, 12, 2); // NVMeDataTypeLogPage
        Put(bytes, 16, 2); // NVME_LOG_PAGE_HEALTH_INFO
        Put(bytes, 24, 40); // offset from STORAGE_PROTOCOL_SPECIFIC_DATA
        Put(bytes, 28, 512);
        return bytes;
    }
    static byte[]? Query(SafeFileHandle handle, uint code, byte[] input, int maximum, out string error)
    {
        var output = new byte[maximum];
        if (!DeviceIoControl(handle, code, input, input.Length, output, output.Length, out int count, IntPtr.Zero))
        { error = Win32Code(Marshal.GetLastWin32Error()); return null; }
        if (count < 0 || count > output.Length) { error = "InvalidResponse"; return null; }
        error = "InvalidResponse";
        return output[..count];
    }
    static uint UInt(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
    static void Put(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    static string? AsciiAt(byte[] bytes, uint offset)
    {
        if (offset < 36 || offset >= bytes.Length) return null;
        int end = Array.IndexOf(bytes, (byte)0, (int)offset);
        if (end < 0) return null;
        string value = Encoding.ASCII.GetString(bytes, (int)offset, end - (int)offset).Trim();
        return value.Length > 0 && !value.Any(char.IsControl) ? value : null;
    }
    static string Win32Code(int code) => code switch { 5 => "AccessDenied", 1 or 50 or 87 => "Unsupported", 2 or 3 or 21 or 1167 => "Unavailable", 121 or 1460 => "Timeout", _ => "Error:" + code.ToString(CultureInfo.InvariantCulture) };
    static string ErrorCode(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x80070005) or unchecked((int)0x80041003) => "AccessDenied",
        unchecked((int)0x8004100E) or unchecked((int)0x80041010) or unchecked((int)0x8004100C) => "Unsupported",
        _ => "Error:" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture)
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)] struct DiskInterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct DiskDeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref DiskInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DiskInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, IntPtr device, uint property, out uint type, byte[] buffer, uint size, out uint required);

    internal static void SelfTest()
    {
        SmartCompatibilitySelfTest();
        static void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Storage self-test: " + name); }
        Require(SelectHealth(null, [], null, null, null, null, null, null).Code == "Unknown", "missing health remains unknown");
        Require(SelectHealth(null, [], false, null, null, null, null, null).Code == "NoFailurePredicted", "SMART negative is not verified healthy");
        Require(SelectHealth(0, [], true, 0, 0, 0, 0, 0).Code == "FailurePredicted", "prediction overrides nominal provider health");
        Require(SelectHealth(0, [], false, 4, 0, 0, 0, 0).Code == "Unhealthy", "NVMe degradation overrides provider healthy");
        Require(SelectHealth(0, [], false, 0, 101, 0, 0, 0).Code == "EnduranceExceeded", "consumed NVMe endurance remains separate from failure flags");
        Require(SelectHealth(0, [], false, 0, 100, 0, 0, 1).Code == "Warning", "media errors override exhausted endurance");
        Require(SelectHealth(0, [0xD007], false, 0, 0, 0, 0, 0).Code == "Unhealthy", "operational IO error overrides nominal health");
        Require(SelectHealth(0, [], false, null, 0, 0, 0, null, true).Code == "AtaThresholdExceeded", "drive-provided ATA failure threshold overrides provider healthy");
        Require(SelectHealth(null, [], false, null, null, null, null, null, false).Code == "NoFailurePredicted", "ATA attributes within thresholds do not prove health");
        Require(SelectHealth(0, [], false, null, null, 0, 0, null, false, null, true).Code == "AtaSectorWarning", "HDD sector warning overrides healthy provider without requiring normalized failure");
        Require(SelectHealth(0, [], true, null, null, 0, 0, null, false, null, true).Code == "FailurePredicted", "controller failure prediction has priority over HDD sector caution");
        Require(HddSectorWarning([new(0xC5, 100, 100, 0, 1, false)]) == true, "pending sector warning at normalized value 100");
        Require(HddSectorWarning([new(0x05, 100, 100, 36, 0, true), new(0xC6, 100, 100, 0, 0, false)]) == false, "valid zero HDD sector attributes");
        Require(HddSectorWarning([new(0xC5, 100, 100, 0, 0xFFFFFFFF, false)]) is null, "all-FF HDD sector sentinel remains unavailable");
        Require(HddSectorWarning([new(0xC2, 100, 100, 0, 33, false)]) is null, "temperature attribute cannot become a sector warning");
        Require(HddSectorWarning([new(0xC5,100,100,0,0x10000,false)]) is null,"vendor high bytes are not a universal pending-sector count");
        var v = new StorageVolume("C:", "NTFS", 100, 40, false);
        Require(v.UsedBytes == 60 && new StorageVolume("X:", null, 100, 110, false).UsedBytes is null, "volume bounds");
        const string exampleInstance = @"SCSI\Disk&Ven_SK&Prod_hynix\4&1723f0da&0&000100_0";
        const string examplePnP = @"SCSI\Disk&Ven_SK&Prod_hynix\4&1723f0da&0&000100";
        const string exampleNative = @"\\?\scsi#disk&ven_sk&prod_hynix#4&1723f0da&0&000100#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}";
        Require(NormalizeSmartPnp(exampleInstance, true) == NormalizeSmartPnp(exampleNative, false), "WMI SMART binds exact native PnP ID");
        Require(NormalizeSmartPnp(exampleInstance, true) == NormalizeSmartPnp(examplePnP, false), "WMI SMART binds Win32 PNPDeviceID");
        Require(NormalizeSmartPnp(exampleInstance, true) != NormalizeSmartPnp(examplePnP + "Z", false), "WMI SMART rejects wrong instance");
        Require(NormalizeSmartPnp("SCSI\\Disk\\Name", true) is null, "WMI SMART requires LUN suffix");
        var vendorSector = new byte[512];
        vendorSector[0] = 1;
        vendorSector[2] = 0xE9; vendorSector[5] = 2; vendorSector[6] = 1; vendorSector[7] = 99;
        vendorSector[511] = (byte)((256 - vendorSector.Take(511).Sum(b => (int)b) % 256) % 256);
        var sectorRow = new DiskWmiRow { ["VendorSpecific"] = vendorSector };
        Require(DecodeAtaSmartPayload(SmartByteArray(sectorRow), null)?.Attributes.Single().Raw == 99,
            "WMI SMART data with valid checksum decoded");
        vendorSector[40] ^= 1;
        Require(DecodeAtaSmartPayload(SmartByteArray(sectorRow), null) is null,
            "WMI SMART invalid checksum rejected");
        Require(SmartByteArray(new DiskWmiRow { ["VendorSpecific"] = new byte[362] }) is null,
            "partial WMI SMART page rejected");
        Require(ReferenceDeviceId("Win32_DiskPartition.DeviceID=\"Disk #0, Partition #1\"") == "Disk #0, Partition #1", "WMI association reference");
        var b = NvmeHealthQuery(); Put(b, 0, 48); Put(b, 4, 48); b[49] = 0x2C; b[50] = 1; b[53] = 7; BitConverter.GetBytes(1234UL).CopyTo(b, 176);
        var log = DecodeNvme(b);
        Require(log is not null && Math.Abs(log.TemperatureC!.Value - 26.85) < .001 && log.WearPercent == 7 && log.PowerOnHours == 1234, "NVMe descriptor layout and Kelvin decoding");
        BitConverter.GetBytes(42UL).CopyTo(b,48+112);
        BitConverter.GetBytes(3UL).CopyTo(b,48+32);
        BitConverter.GetBytes(4UL).CopyTo(b,48+48);
        Require(DecodeNvme(b) is {PowerOnCount:42,HostReadsBytes:1536000,HostWritesBytes:2048000},"NVMe power cycles and data units decoded");
        b[48+112+8]=1;Require(DecodeNvme(b)?.PowerOnCount is null,"128-bit cycle overflow stays unavailable");b[48+112+8]=0;
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(b,48+32);
        Require(DecodeNvme(b)?.HostReadsBytes is null,"NVMe byte conversion overflow stays unavailable");
        b[51]=9;b[52]=10;BitConverter.GetBytes((ushort)310).CopyTo(b,248);
        var detailedLog=DecodeNvme(b);
        Require(detailedLog?.AvailableSparePercent==9&&detailedLog.SpareThresholdPercent==10&&Math.Abs(detailedLog.SensorTemperaturesC[0]!.Value-36.85)<.001&&detailedLog.SensorTemperaturesC[1] is null,"NVMe spare/threshold/eight temperature sensors decoded");
        b[51]=255;b[52]=255;Require(DecodeNvme(b) is null,"invalid mandatory NVMe spare fields invalidate the payload");
        b[51]=9;b[52]=10;
        Put(b, 4, uint.MaxValue); Require(DecodeNvme(b) is null, "reject malformed NVMe descriptor Size"); Put(b, 4, 48);
        Put(b, 24, uint.MaxValue); Require(DecodeNvme(b) is null, "reject invalid protocol offset");
        Require(DecodeNvme(new byte[560]) is null, "reject empty NVMe response");
        var zeroLog = NvmeHealthQuery(); Put(zeroLog, 0, 48); Put(zeroLog, 4, 48);
        Require(DecodeNvme(zeroLog) is null, "valid descriptor with all-zero NVMe health payload cannot imply 100 percent life");
        var seek = new byte[12]; Put(seek, 0, 12); Put(seek, 4, 12);
        Require(DecodeSeekPenalty(seek) == false, "valid nonrotating descriptor");
        seek[8] = 1; Require(DecodeSeekPenalty(seek) == true, "valid rotational descriptor");
        seek[8] = 2; Require(DecodeSeekPenalty(seek) is null, "invalid seek boolean rejected");
        seek[8] = 0; Put(seek, 4, 13); Require(DecodeSeekPenalty(seek) is null, "truncated seek descriptor rejected");
        Require(DecodeSeekPenalty(new byte[12]) is null, "zero seek descriptor cannot imply SSD");
        static byte[] SmartResponse(byte normalized, byte id, bool prefailure, bool thresholdPage)
        {
            var response = new byte[528]; Put(response, 0, 512); response[16] = 1; response[18] = id;
            if (thresholdPage) response[19] = normalized;
            else { response[19] = prefailure ? (byte)1 : (byte)0; response[21] = normalized; response[22] = normalized; response[23] = 99; }
            int sum = response.Skip(16).Take(511).Sum(x => (int)x);
            response[527] = (byte)((256 - (sum & 255)) & 255);
            return response;
        }
        var attrs = SmartResponse(9, 5, true, false);
        var limits = SmartResponse(10, 5, true, true);
        var smart = DecodeAtaSmart(attrs, limits);
        Require(smart?.PrefailureThresholdExceeded == true && smart.Attributes[0].Raw == 99 && smart.Attributes[0].Threshold == 10, "ATA drive-specific normalized threshold and raw decoding");
        Require(DecodeAtaSmart(SmartResponse(11, 5, true, false), limits)?.PrefailureThresholdExceeded == false, "ATA normalized value above own threshold");
        Require(DecodeAtaSmart(SmartResponse(10,5,true,false),limits) is {PrefailureThresholdExceeded:true,ThresholdAtLimit:false},"Pre-fail equality is critical, not caution");
        Require(DecodeAtaSmart(SmartResponse(10,5,false,false),limits) is {PrefailureThresholdExceeded:false,ThresholdAtLimit:true},"non-Pre-fail equality is only a caution");
        Require(DecodeAtaSmart(SmartResponse(0,5,true,false),limits)?.PrefailureThresholdExceeded==true,"zero normalized value cannot be hidden as missing");
        Require(DecodeAtaSmart(SmartResponse(9,0xB1,false,false),SmartResponse(10,0xB1,false,true))?.PrefailureThresholdExceeded==true,"SSD life threshold applies even without prefailure flag");
        Require(DecodeAtaSmart(SmartResponse(1, 194, false, false), SmartResponse(10, 194, false, true))?.PrefailureThresholdExceeded is null, "non-prefailure vendor raw attribute cannot infer health");
        Require(DecodeAtaSmart(attrs, null)?.PrefailureThresholdExceeded is null, "missing vendor threshold cannot infer health");
        attrs[23] ^= 1; Require(DecodeAtaSmart(attrs, limits) is null, "invalid SMART checksum rejected");
        attrs = SmartResponse(10, 5, true, false); attrs[4] = 1;
        Require(DecodeAtaSmart(attrs, limits) is null, "SMART driver error rejected");
        var ataQuery = AtaSmartQuery(0xD0);
        Require(ataQuery.Length == 32 && ataQuery[4] == 0xD0 && ataQuery[7] == 0x4F && ataQuery[8] == 0xC2 && ataQuery[10] == 0xB0, "read-only ATA input layout");
        static byte[] Identify(string model, ushort rotation)
        {
            var page = new byte[512]; page[0] = 0x40;
            byte[] text = Encoding.ASCII.GetBytes(model.PadRight(40)[..40]);
            for (int i = 0; i < text.Length; i += 2) { page[54 + i] = text[i + 1]; page[55 + i] = text[i]; }
            BitConverter.GetBytes(rotation).CopyTo(page, 434);
            return page;
        }
        var identifySc311 = Identify("SK hynix SC311 SATA 256GB", 1);
        Require(DecodeAtaIdentify(identifySc311) is { Model: "SK hynix SC311 SATA 256GB", MediaType: "SSD" }, "ATA word-swapped model and nonrotating word217");
        Require(DecodeAtaIdentify(Identify("Conventional rotating ATA drive", 5400))?.MediaType == "HDD", "ATA rotation rate proves HDD");
        Require(DecodeAtaIdentify(Identify("TRIM-marketed name", 0))?.MediaType is null, "missing rotation word cannot imply SSD");
        Require(DecodeAtaIdentify(Identify("Unknown rotation", 0xFFFF))?.MediaType is null, "reserved rotation word remains unknown");
        identifySc311[510] = 0xA5;
        Require(DecodeAtaIdentify(identifySc311) is null, "declared IDENTIFY checksum must validate");
        identifySc311[511] = (byte)((256 - (identifySc311.Take(511).Sum(x => (int)x) & 255)) & 255);
        Require(DecodeAtaIdentify(identifySc311)?.MediaType == "SSD", "optional IDENTIFY checksum valid");
        foreach (int width in new[] { 4, 8 })
        {
            var packet = AtaPassThroughQuery(0xB0, 0xD0, width);
            int header = width == 8 ? 48 : 40, registers = width == 8 ? 40 : 32;
            Require(packet.Length == header + 512 && packet[registers] == 0xD0 && packet[registers + 3] == 0x4F && packet[registers + 4] == 0xC2, "ATA_PASS_THROUGH layout x" + width * 8);
            packet[registers + 6] = 0x50; identifySc311.CopyTo(packet, header);
            Require(DecodeAtaPassThrough(packet, width)?.Length == 512, "ATA_PASS_THROUGH successful sector x" + width * 8);
            packet[registers + 6] = 0x51;
            Require(DecodeAtaPassThrough(packet, width) is null, "ATA status ERR invalidates ioctl success x" + width * 8);
            packet[registers + 6] = 0x58;
            Require(DecodeAtaPassThrough(packet, width) is null, "ATA incomplete DRQ transfer rejected x" + width * 8);
            packet[registers + 6] = 0x50; Put(packet, 8, 511);
            Require(DecodeAtaPassThrough(packet, width) is null, "ATA short transfer rejected x" + width * 8);
            Put(packet, 8, 512); Put(packet, width == 8 ? 24 : 20, uint.MaxValue);
            Require(DecodeAtaPassThrough(packet, width) is null, "ATA invalid payload offset rejected x" + width * 8);
        }
        var scAttributes = new StorageSmartAttribute[]
        {
            new(194, 68, 64, 0, 32UL | (45UL << 16), false),
            new(233, 100, 100, 0, 87, false), new(9, 100, 100, 0, 12345, false)
        };
        var sc = DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD", scAttributes);
        Require(sc.TemperatureC == 32 && sc.RemainingLifePercent == 87 && sc.WearPercent == 13 && sc.PowerOnHours == 12345,
            "SC311 temperature/raw remaining life/hours mapping");
        // Confirmed SC311, E9 raw=99: CrystalDiskInfo calculates 99% remaining
        // despite the incompatible Windows Wear=98 (normalized E9=2).
        // F1/F2 use 512-byte sectors and the display rounds down to GiB.
        var actualSc311 = DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD",
            [new(0xE9, 2, 1, null, 99, false), new(0xB1, 99, 1, null, 20, false),
             new(0xC2, 26, 14, null, 26, false), new(9, 92, 92, null, 7667, false),
             new(0x0C, 100, 100, null, 1938, false),
             new(0xF1, 100, 100, null, 0x03E396A4E5UL, false),
             new(0xF2, 100, 100, null, 0x05E6C1F204UL, false)]);
        Require(actualSc311.TemperatureC == 26 && actualSc311.PowerOnHours == 7667 &&
            actualSc311.RemainingLifePercent == 99 && actualSc311.WearPercent == 1 &&
            actualSc311.PowerOnCount == 1938 &&
            Math.Floor(actualSc311.HostWritesBytes!.Value/1073741824d)==7964 &&
            Math.Floor(actualSc311.HostReadsBytes!.Value/1073741824d)==12086 &&
            WearEstimatesConflict(98, actualSc311.WearPercent),
            "SC311 original SMART sample matches CrystalDiskInfo 99%, 26C, 7667h, 1938, 7964/12086 GiB");
        Require(!WearEstimatesConflict(1, actualSc311.WearPercent) && !WearEstimatesConflict(null, 1),
            "matching or absent wear sources do not trigger a false conflict");
        Require(DecodeAtaSensors("SK hynix SC401 SATA 512GB", "SSD", scAttributes).RemainingLifePercent == 87, "SC401 shares verified raw life profile");
        Require(DecodeAtaSensors("Unknown SSD SC311", "SSD", scAttributes).RemainingLifePercent is null, "model family without vendor identity is insufficient");
        Require(DecodeAtaSensors("SK hynix unprofiled family", "SSD", scAttributes).TemperatureC is null, "no generic SSD raw temperature guess");
        Require(DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD", []).RemainingLifePercent is null, "missing lifetime cannot become 100 percent");
        scAttributes[1] = scAttributes[1] with { Raw = 0 };
        Require(DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD", scAttributes) is { RemainingLifePercent: null, LifeInvalid: true }, "raw E9 zero is unavailable per CDI SC311 profile");
        scAttributes[1] = scAttributes[1] with { Raw = 255 };
        scAttributes[0] = scAttributes[0] with { Raw = 255 };
        Require(DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD", scAttributes) is { TemperatureC: null, RemainingLifePercent: null }, "invalid SC311 sensor bytes remain unavailable");
        Require(DecodeAtaSensors("SK hynix SC311 SATA 256GB", "SSD", [new(0xE7,99,99,null,95,false)]).RemainingLifePercent is null,
            "SC311 cannot mistake unrelated E7 for validated E9");
        var conflictingLife = new StorageSmartAttribute[] { new(231, 100, 100, 0, 80, false), new(233, 100, 100, 0, 60, false) };
        Require(DecodeAtaSensors("HFS256TND000", "SSD", conflictingLife) is { RemainingLifePercent: null, LifeConflict: true }, "conflicting lifetime attributes cannot choose healthier value");
        conflictingLife[1] = conflictingLife[1] with { Raw = 255 };
        Require(DecodeAtaSensors("HFS256TND000", "SSD", conflictingLife) is { RemainingLifePercent: null, LifeInvalid: true }, "invalid second lifetime attribute cannot leave optimistic first value");
        var hddSensors = DecodeAtaSensors("Rotating ATA disk", "HDD", [new(194, 100, 100, null, 34, false)]);
        Require(hddSensors.TemperatureC == 34 && hddSensors.RemainingLifePercent is null, "HDD conventional temperature has no invented life score");
        Require(DecodeAtaSensors("SAMSUNG SV legacy", "HDD", [new(194, 100, 100, null, 350, false)]).TemperatureC is null, "known legacy temperature encoding excluded from generic HDD decoder");
        var descriptorTemperature = new byte[40]; Put(descriptorTemperature, 0, 40); Put(descriptorTemperature, 4, 40); descriptorTemperature[12] = 1;
        BitConverter.GetBytes((short)35).CopyTo(descriptorTemperature, 26);
        Require(DecodeStorageTemperature(descriptorTemperature) == 35, "documented device temperature descriptor");
        BitConverter.GetBytes(short.MinValue).CopyTo(descriptorTemperature, 26);
        Require(DecodeStorageTemperature(descriptorTemperature) is null, "device temperature not-reported sentinel");
        BitConverter.GetBytes((short)0).CopyTo(descriptorTemperature, 26);
        Require(DecodeStorageTemperature(descriptorTemperature) == 0, "documented Celsius zero is a valid temperature");
        descriptorTemperature[12] = 2;
        Require(DecodeStorageTemperature(descriptorTemperature) is null, "truncated device sensor array rejected");
        var partitions = new List<DiskWmiRow> { new() { ["DeviceID"] = "P0", ["DiskIndex"] = 0 }, new() { ["DeviceID"] = "P1", ["DiskIndex"] = 1 } };
        var links = new List<DiskWmiRow> { new() { ["Antecedent"] = "Win32_DiskPartition.DeviceID=\"P0\"", ["Dependent"] = "Win32_LogicalDisk.DeviceID=\"C:\"" }, new() { ["Antecedent"] = "Win32_DiskPartition.DeviceID=\"P1\"", ["Dependent"] = "Win32_LogicalDisk.DeviceID=\"C:\"" } };
        Require(MapVolumeOwners(partitions, links)["C:"].Count == 2, "spanned volume retains multiple owners");
        var extents = new byte[56]; Put(extents, 0, 2); Put(extents, 8, 1); Put(extents, 32, 3);
        Require(DecodeVolumeExtents(extents)!.SetEquals([1, 3]), "native volume extent alignment");
        Put(extents, 0, 3); Require(DecodeVolumeExtents(extents) is null, "reject truncated volume extent array");
        var raw = new DiskWmiRow { ["Index"] = 0, ["Model"] = "Same Model", ["Size"] = 1000000000UL };
        var wrongPhysical = new DiskWmiRow { ["DeviceId"] = "0", ["Model"] = "Different Model", ["Size"] = 1000000000UL };
        Require(MatchPhysical(raw, null, [wrongPhysical]) is null, "numeric device ID alone must not link provider health");
        var systemDisk = new DiskWmiRow { ["UniqueId"] = "exact-storage-identity" };
        var physicalDisk = new DiskWmiRow { ["DeviceId"] = "99", ["UniqueId"] = "exact-storage-identity" };
        Require(ReferenceEquals(MatchPhysical(raw, systemDisk, [physicalDisk]), physicalDisk), "unique storage identity links independently of device number");
        var physicalWithPath = new DiskWmiRow { ["__RELPATH"] = "MSFT_PhysicalDisk.ObjectId=\"physical-key\"", ["DeviceId"] = "0", ["HealthStatus"] = 0 };
        var methodCounter = new DiskWmiRow { ["__CLASS"] = "MSFT_StorageReliabilityCounter", ["DeviceId"] = "0" };
        Require(MethodCounterMatches(physicalWithPath, "MSFT_PhysicalDisk", methodCounter), "method embedded output validates exact supplied physical device");
        methodCounter["DeviceId"] = "1";
        Require(!MethodCounterMatches(physicalWithPath, "MSFT_PhysicalDisk", methodCounter), "method counter with contradictory device ID rejected");
        methodCounter["DeviceId"] = "0"; methodCounter["__CLASS"] = "UnrelatedClass";
        Require(!MethodCounterMatches(physicalWithPath, "MSFT_PhysicalDisk", methodCounter), "unexpected embedded method output class rejected");
        var counterA = new DiskWmiRow { ["__RELPATH"] = "MSFT_StorageReliabilityCounter.ObjectId=\"counter-a\"", ["DeviceId"] = "0", ["Wear"] = 101 };
        var counterB = new DiskWmiRow { ["__RELPATH"] = "MSFT_StorageReliabilityCounter.ObjectId=\"counter-b\"", ["DeviceId"] = "0", ["Wear"] = 0 };
        var exactLink = new DiskWmiRow { ["PhysicalDisk"] = "\\\\LOCALHOST\\ROOT\\Microsoft\\Windows\\Storage:MSFT_PhysicalDisk.ObjectId=\"physical-key\"", ["StorageReliabilityCounter"] = "MSFT_StorageReliabilityCounter.ObjectId=\"counter-a\"" };
        Require(ReferenceEquals(AssociatedCounter(physicalWithPath, "MSFT_PhysicalDisk", "PhysicalDisk", [exactLink], [counterB, counterA]), counterA), "association identity resolves colliding DeviceId counters");
        Require(AssociatedCounter(physicalWithPath, "MSFT_PhysicalDisk", "PhysicalDisk", [], [counterA]) is null, "same DeviceId without association is insufficient");
        Require(AssociatedCounter(physicalWithPath, "MSFT_PhysicalDisk", "PhysicalDisk", [exactLink], [counterA, counterA]) is null, "duplicate associated counter instances are rejected");
        var worn = BuildDisk(null, null, physicalWithPath, [counterB, counterA], [exactLink], [], []);
        Require(worn.WearPercent == 101 && worn.HealthCode == "EnduranceExceeded", "WMI wear above endurance limit is separate from provider faults");
        var confirmedHdd = new DiskWmiRow { ["__RELPATH"] = physicalWithPath["__RELPATH"], ["DeviceId"] = "0", ["HealthStatus"] = 0, ["MediaType"] = 3 };
        var hddCounter = new DiskWmiRow { ["__RELPATH"] = counterA["__RELPATH"], ["DeviceId"] = "0", ["Wear"] = 100 };
        var hddWithProviderWear = BuildDisk(null, null, confirmedHdd, [hddCounter], [exactLink], [], []);
        Require(hddWithProviderWear.MediaType == "HDD" && hddWithProviderWear.WearPercent is null &&
            hddWithProviderWear.RemainingLifePercent is null && hddWithProviderWear.WearSource == "None" &&
            hddWithProviderWear.HealthCode == "ProviderHealthy", "final HDD type clears provider endurance before health selection");
    }

    sealed class DiskWmiRow : Dictionary<string, object?> { internal DiskWmiRow() : base(StringComparer.OrdinalIgnoreCase) { } }

    // Native WMI COM interfaces avoid adding System.Management or starting
    // PowerShell. Semisynchronous enumerator Next has a real provider timeout.
    sealed class DiskWmiSession : IDisposable
    {
        IWbemDiskServices? services;
        readonly string scope;
        readonly List<string> notes;
        internal DiskWmiSession(string scope, List<string> notes)
        {
            this.scope = scope; this.notes = notes;
            object? locatorObject = null;
            try
            {
                locatorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4590F811-1D3A-11D0-891F-00AA004B2E24"), true)!);
                var locator = (IWbemDiskLocator)locatorObject!;
                int hr = locator.ConnectServer(scope, null, null, null, 0x80, null, IntPtr.Zero, out services);
                Marshal.ThrowExceptionForHR(hr);
                hr = CoSetProxyBlanket(services, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0);
                Marshal.ThrowExceptionForHR(hr);
            }
            catch (Exception ex) { notes.Add(scope + ":" + ErrorCode(ex)); Release(services); services = null; }
            finally { Release(locatorObject); }
        }
        internal List<DiskWmiRow> Query(string query, params string[] fields)
        {
            var rows = new List<DiskWmiRow>();
            if (services is null) return rows;
            IWbemDiskEnumerator? enumerator = null;
            string source = query.Contains(" FROM ", StringComparison.Ordinal) ? query.Split(" FROM ")[1].Split(' ')[0] : scope;
            try
            {
                Marshal.ThrowExceptionForHR(services.ExecQuery("WQL", query, 0x30, IntPtr.Zero, out enumerator));
                if (enumerator is null) return rows;
                // ExecQuery returns a distinct COM proxy. The services proxy
                // blanket does not propagate to IEnumWbemClassObject; without
                // it, Next() may return WBEM_E_ACCESS_DENIED even for admins.
                Marshal.ThrowExceptionForHR(CoSetProxyBlanket(enumerator, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0));
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 1800)
                {
                    IWbemDiskObject? item = null;
                    try
                    {
                        int hr = enumerator.Next(600, 1, out item, out uint count);
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        if (count == 0)
                        {
                            if (hr == 0x40004) notes.Add(source + ":Timeout");
                            break;
                        }
                        var row = new DiskWmiRow();
                        foreach (string field in fields)
                        {
                            if (item is not null && item.Get(field, 0, out var value, IntPtr.Zero, IntPtr.Zero) >= 0)
                                row[field] = value is DBNull ? null : value;
                        }
                        rows.Add(row);
                    }
                    finally { Release(item); }
                }
                if (watch.ElapsedMilliseconds >= 1800) notes.Add(source + ":Timeout");
            }
            catch (Exception ex) { notes.Add(source + ":" + ErrorCode(ex)); }
            finally { Release(enumerator); }
            return rows;
        }

        internal DiskWmiRow? ReadReliability(DiskWmiRow? source, string sourceClass, string parameter, List<string> diagnostics)
        {
            if (services is null || source is null) return null;
            string? path = Text(source, "__RELPATH");
            if (path is null || !path.StartsWith(sourceClass + ".", StringComparison.OrdinalIgnoreCase))
            { diagnostics.Add("reliability-method:MissingInstancePath"); return null; }
            IWbemDiskObject? commandClass = null, sourceObject = null, inputSignature = null, outputSignature = null;
            IWbemDiskObject? input = null, output = null;
            IWbemDiskCallResult? call = null;
            object? embedded = null;
            string methodSource = "reliability-method-" + sourceClass;
            try
            {
                commandClass = GetObjectBounded("PS_StorageCmdlets", methodSource, diagnostics);
                sourceObject = GetObjectBounded(path, methodSource, diagnostics);
                if (commandClass is null || sourceObject is null) return null;
                Marshal.ThrowExceptionForHR(commandClass.GetMethod("GetStorageReliabilityCounter", 0, out inputSignature, out outputSignature));
                if (inputSignature is null) { diagnostics.Add(methodSource + ":InvalidMethodSchema"); return null; }
                Marshal.ThrowExceptionForHR(inputSignature.SpawnInstance(0, out input));
                if (input is null) return null;
                // IWbemClassObject::Put requires an embedded object as a
                // VT_UNKNOWN variant, never an automation dispatch wrapper.
                object diskValue = new UnknownWrapper(sourceObject);
                Marshal.ThrowExceptionForHR(input.Put(parameter, 0, ref diskValue, 0));
                Marshal.ThrowExceptionForHR(services.ExecMethod("PS_StorageCmdlets", "GetStorageReliabilityCounter", 0x10, IntPtr.Zero, input, out output, out call));
                if (output is null && call is not null)
                {
                    int hr = call.GetResultObject(1500, out output);
                    if (hr == 0x40004) { diagnostics.Add(methodSource + ":Timeout"); return null; }
                    Marshal.ThrowExceptionForHR(hr);
                }
                if (output is null) { diagnostics.Add(methodSource + ":NoOutput"); return null; }
                var method = Row(output, "ReturnValue");
                int? status = Int(method, "ReturnValue");
                if (status != 0)
                {
                    diagnostics.Add(methodSource + ":" + (status switch { 1 => "Unsupported", 3 => "Timeout", 40001 => "AccessDenied", _ => "MethodError:" + (status?.ToString(CultureInfo.InvariantCulture) ?? "Unknown") }));
                    return null;
                }
                if (output.Get("StorageReliabilityCounter", 0, out embedded, IntPtr.Zero, IntPtr.Zero) < 0 || embedded is not IWbemDiskObject result)
                { diagnostics.Add(methodSource + ":NoCounter"); return null; }
                var row = Row(result, "__CLASS", "__RELPATH", "DeviceId", "Temperature", "Wear", "PowerOnHours", "ReadErrorsUncorrected", "WriteErrorsUncorrected");
                if (!MethodCounterMatches(source, sourceClass, row))
                { diagnostics.Add(methodSource + ":IdentityMismatch"); return null; }
                return row;
            }
            catch (Exception ex) { diagnostics.Add(methodSource + ":" + ErrorCode(ex)); return null; }
            finally
            {
                Release(embedded); Release(call); Release(output); Release(input);
                Release(outputSignature); Release(inputSignature); Release(sourceObject); Release(commandClass);
            }
        }

        IWbemDiskObject? GetObjectBounded(string path, string source, List<string> diagnostics)
        {
            if (services is null) return null;
            IWbemDiskObject? result = null;
            IWbemDiskCallResult? call = null;
            try
            {
                Marshal.ThrowExceptionForHR(services.GetObject(path, 0x10, IntPtr.Zero, out result, out call));
                if (result is null && call is not null)
                {
                    int hr = call.GetResultObject(1500, out result);
                    if (hr == 0x40004) { diagnostics.Add(source + ":Timeout"); return null; }
                    Marshal.ThrowExceptionForHR(hr);
                }
                var value = result;
                result = null;
                return value;
            }
            finally { Release(call); Release(result); }
        }

        static DiskWmiRow Row(IWbemDiskObject item, params string[] fields)
        {
            var row = new DiskWmiRow();
            foreach (string field in fields)
                if (item.Get(field, 0, out var value, IntPtr.Zero, IntPtr.Zero) >= 0)
                    row[field] = value is DBNull ? null : value;
            return row;
        }
        public void Dispose() { Release(services); services = null; }
    }
    static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")]
    static extern int CoSetProxyBlanket([MarshalAs(UnmanagedType.IUnknown)] object proxy, uint authentication, uint authorization, IntPtr principal, uint authenticationLevel, uint impersonation, IntPtr identity, uint capabilities);

    [ComImport, Guid("DC12A687-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWbemDiskLocator
    {
        [PreserveSig] int ConnectServer([MarshalAs(UnmanagedType.BStr)] string resource, [MarshalAs(UnmanagedType.BStr)] string? user, [MarshalAs(UnmanagedType.BStr)] string? password, [MarshalAs(UnmanagedType.BStr)] string? locale, int flags, [MarshalAs(UnmanagedType.BStr)] string? authority, IntPtr context, out IWbemDiskServices services);
    }
    [ComImport, Guid("9556DC99-828C-11CF-A37E-00AA003240C7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWbemDiskServices
    {
        [PreserveSig] int OpenNamespace(); [PreserveSig] int CancelAsyncCall(); [PreserveSig] int QueryObjectSink();
        [PreserveSig] int GetObject([MarshalAs(UnmanagedType.BStr)] string path, int flags, IntPtr context, out IWbemDiskObject? item, out IWbemDiskCallResult? call);
        [PreserveSig] int GetObjectAsync(); [PreserveSig] int PutClass();
        [PreserveSig] int PutClassAsync(); [PreserveSig] int DeleteClass(); [PreserveSig] int DeleteClassAsync();
        [PreserveSig] int CreateClassEnum(); [PreserveSig] int CreateClassEnumAsync(); [PreserveSig] int PutInstance();
        [PreserveSig] int PutInstanceAsync(); [PreserveSig] int DeleteInstance(); [PreserveSig] int DeleteInstanceAsync();
        [PreserveSig] int CreateInstanceEnum(); [PreserveSig] int CreateInstanceEnumAsync();
        [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query, int flags, IntPtr context, out IWbemDiskEnumerator enumerator);
        [PreserveSig] int ExecQueryAsync(); [PreserveSig] int ExecNotificationQuery(); [PreserveSig] int ExecNotificationQueryAsync();
        [PreserveSig] int ExecMethod([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string method, int flags, IntPtr context, IWbemDiskObject input, out IWbemDiskObject? output, out IWbemDiskCallResult? call);
    }
    [ComImport, Guid("44ACA675-E8FC-11D0-A07C-00C04FB68820"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWbemDiskCallResult
    {
        [PreserveSig] int GetResultObject(int timeout, out IWbemDiskObject? item);
    }
    [ComImport, Guid("027947E1-D731-11CE-A357-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWbemDiskEnumerator
    {
        [PreserveSig] int Reset();
        [PreserveSig] int Next(int timeout, uint count, [MarshalAs(UnmanagedType.Interface)] out IWbemDiskObject? item, out uint returned);
    }
    [ComImport, Guid("DC12A681-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWbemDiskObject
    {
        [PreserveSig] int GetQualifierSet();
        [PreserveSig] int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr type, IntPtr flavor);
        [PreserveSig] int Put([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, [MarshalAs(UnmanagedType.Struct)] ref object value, int type);
        [PreserveSig] int Delete(); [PreserveSig] int GetNames(); [PreserveSig] int BeginEnumeration(); [PreserveSig] int Next();
        [PreserveSig] int EndEnumeration(); [PreserveSig] int GetPropertyQualifierSet(); [PreserveSig] int Clone();
        [PreserveSig] int GetObjectText(); [PreserveSig] int SpawnDerivedClass();
        [PreserveSig] int SpawnInstance(int flags, out IWbemDiskObject? item);
        [PreserveSig] int CompareTo(); [PreserveSig] int GetPropertyOrigin(); [PreserveSig] int InheritsFrom();
        [PreserveSig] int GetMethod([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, out IWbemDiskObject? input, out IWbemDiskObject? output);
    }
}
