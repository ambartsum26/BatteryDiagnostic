namespace BatteryDiagnostic;
internal static class SmartctlMerge
{
    internal static StorageDisk Apply(StorageDisk disk,SmartctlSample sample)
    {
        if(disk.Index is not {} index||!SmartctlParser.IdentityMatches(sample,index,disk.Model))return disk;
        var notes=disk.Diagnostics.Concat(sample.Diagnostics).Append("smartctl:MatchedPhysicalDriveAndModel").ToList();
        string media=disk.MediaType is "Unknown" or "Unspecified"?sample.MediaType:disk.MediaType;
        bool mediaConflict=disk.MediaType is "SSD" or "HDD"&&sample.MediaType is "SSD" or "HDD"&&disk.MediaType!=sample.MediaType;
        if(mediaConflict)notes.Add("smartctl:MediaTypeConflict");
        bool lifeConflict=disk.RemainingLifePercent.HasValue&&sample.RemainingLifePercent.HasValue&&
            Math.Abs(disk.RemainingLifePercent.Value-sample.RemainingLifePercent.Value)>1;
        if(lifeConflict)notes.Add("ata-life:ConflictingSmartctlSource");
        bool invalid=lifeConflict||mediaConflict||notes.Any(n=>n.StartsWith("ata-life:",StringComparison.Ordinal));
        double? life=invalid||media=="HDD"?null:disk.RemainingLifePercent??sample.RemainingLifePercent;
        string lifeSource=life.HasValue?(disk.RemainingLifePercent.HasValue?disk.RemainingLifeSource:"Smartctl"):"None";
        var attributes=sample.Attributes.Select(a=>new StorageSmartAttribute(a.Id,a.Current,a.Worst,a.Threshold,a.Raw,a.Prefailure)).ToArray();
        bool threshold=attributes.Any(a=>a.Prefailure&&a.Threshold>0&&a.Current<=a.Threshold);
        bool atLimit=attributes.Any(a=>a.Threshold>0&&a.Current==a.Threshold);
        bool sectors=media=="HDD"&&attributes.Any(a=>a.Id is 5 or 197 or 198&&a.Raw>0);
        bool oldFailure=disk.FailurePredicted==true||disk.HealthCode is "Unhealthy" or "FailurePredicted" or "AtaThresholdExceeded";
        string code=sample.Passed==false?"Unhealthy":oldFailure?disk.HealthCode:threshold?"AtaThresholdExceeded":
            sample.HealthWarning?"Warning":sectors?"AtaSectorWarning":disk.HealthCode=="Unknown"&&sample.Passed==true?"NoFailurePredicted":disk.HealthCode;
        byte? critical=disk.NvmeCriticalWarning.HasValue&&sample.CriticalWarning.HasValue?
            (byte)(disk.NvmeCriticalWarning.Value|sample.CriticalWarning.Value):disk.NvmeCriticalWarning??sample.CriticalWarning;
        ulong? mediaErrors=disk.NvmeMediaErrors.HasValue&&sample.MediaErrors.HasValue?
            Math.Max(disk.NvmeMediaErrors.Value,sample.MediaErrors.Value):disk.NvmeMediaErrors??sample.MediaErrors;
        return disk with
        {
            MediaType=media,MediaTypeSource=disk.MediaType==media?disk.MediaTypeSource:"Smartctl",
            TemperatureC=disk.TemperatureC??sample.TemperatureC,
            TemperatureSource=disk.TemperatureC.HasValue?disk.TemperatureSource:sample.TemperatureC.HasValue?"Smartctl":"None",
            RemainingLifePercent=life,RemainingLifeSource=lifeSource,
            WearPercent=invalid||media=="HDD"?null:disk.WearPercent??sample.WearPercent,
            WearSource=invalid||media=="HDD"?"None":disk.WearPercent.HasValue?disk.WearSource:sample.WearPercent.HasValue?"Smartctl":"None",
            PowerOnHours=disk.PowerOnHours??sample.PowerOnHours,
            PowerOnHoursSource=disk.PowerOnHours.HasValue?disk.PowerOnHoursSource:sample.PowerOnHours.HasValue?"Smartctl":"None",
            PowerOnCount=disk.PowerOnCount??sample.PowerOnCount,
            HostReadsBytes=disk.HostReadsBytes??sample.HostReadsBytes,HostWritesBytes=disk.HostWritesBytes??sample.HostWritesBytes,
            SmartAttributes=disk.SmartAttributes.Count==0?attributes:disk.SmartAttributes,
            AtaSmartSource=disk.SmartAttributes.Count==0&&attributes.Length>0?"Smartctl":disk.AtaSmartSource,
            AtaProfile=disk.AtaProfile=="None"&&sample.Protocol=="ATA"?sample.ModelFamily??"Smartctl drivedb":disk.AtaProfile,
            AtaPrefailureThresholdExceeded=disk.AtaPrefailureThresholdExceeded==true||threshold?true:disk.AtaPrefailureThresholdExceeded,
            AtaThresholdAtLimit=disk.AtaThresholdAtLimit==true||atLimit?true:disk.AtaThresholdAtLimit,
            AtaSectorWarning=disk.AtaSectorWarning==true||sectors?true:disk.AtaSectorWarning,
            FailurePredicted=sample.Passed==false?true:disk.FailurePredicted,
            HealthCode=code,HealthSource=code==disk.HealthCode?disk.HealthSource:"Smartctl",
            NvmeCriticalWarning=critical,NvmeMediaErrors=mediaErrors,
            NvmeAvailableSparePercent=disk.NvmeAvailableSparePercent??sample.AvailableSpare,
            NvmeSpareThresholdPercent=disk.NvmeSpareThresholdPercent??sample.SpareThreshold,
            Diagnostics=notes.Distinct().ToArray()
        };
    }
}
