namespace BatteryDiagnostic;

internal sealed record StorageSnapshot(DateTimeOffset Timestamp, IReadOnlyList<StorageDisk> Disks, IReadOnlyList<string> Diagnostics);

internal sealed record StorageVolume(string Name, string? FileSystem, ulong? TotalBytes, ulong? FreeBytes, bool SharedAcrossDisks)
{
    public ulong? UsedBytes => TotalBytes.HasValue && FreeBytes.HasValue && FreeBytes <= TotalBytes ? TotalBytes - FreeBytes : null;
}

// ATA SMART raw values are vendor-specific. Keep the attribute identifier and
// the disk's own normalized threshold; never rename a raw value as a universal
// sector count, temperature, lifetime or remaining-health percentage.
internal sealed record StorageSmartAttribute(byte Id, byte Current, byte Worst, byte? Threshold, ulong Raw, bool Prefailure);

internal sealed record StorageDisk
{
    public int? Index { get; init; }
    public string DeviceId { get; init; } = "";
    public string Model { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string MediaType { get; init; } = "Unknown";
    public string MediaTypeSource { get; init; } = "None";
    public string? BusType { get; init; }
    public string? PartitionStyle { get; init; }
    public ulong? TotalBytes { get; init; }
    public IReadOnlyList<StorageVolume> Volumes { get; init; } = [];
    public string HealthCode { get; init; } = "Unknown";
    public string HealthSource { get; init; } = "None";
    public ushort? ProviderHealthStatus { get; init; }
    public IReadOnlyList<ushort> OperationalStatus { get; init; } = [];
    public bool? FailurePredicted { get; init; }
    public string ReliabilitySource { get; init; } = "None";
    public bool? AtaPrefailureThresholdExceeded { get; init; }
    public bool? AtaThresholdAtLimit { get; init; }
    public bool? AtaSectorWarning { get; init; }
    public IReadOnlyList<StorageSmartAttribute> SmartAttributes { get; init; } = [];
    public double? TemperatureC { get; init; }
    public string TemperatureSource { get; init; } = "None";
    public int? WearPercent { get; init; }
    public string WearSource { get; init; } = "None";
    public double? RemainingLifePercent { get; init; }
    public string RemainingLifeSource { get; init; } = "None";
    public ulong? PowerOnHours { get; init; }
    public string PowerOnHoursSource { get; init; } = "None";
    public ulong? PowerOnCount { get; init; }
    public ulong? HostReadsBytes { get; init; }
    public ulong? HostWritesBytes { get; init; }
    public string AtaSmartSource { get; init; } = "None";
    public string AtaProfile { get; init; } = "None";
    public ulong? ReadErrorsUncorrected { get; init; }
    public ulong? WriteErrorsUncorrected { get; init; }
    public ulong? NvmeMediaErrors { get; init; }
    public byte? NvmeCriticalWarning { get; init; }
    public byte? NvmeAvailableSparePercent { get; init; }
    public byte? NvmeSpareThresholdPercent { get; init; }
    public IReadOnlyList<double?> NvmeSensorTemperaturesC { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

