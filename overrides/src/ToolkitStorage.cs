using DiskInfoToolkit;
using DiskInfoToolkit.Devices;

namespace BatteryDiagnostic;

internal static partial class PerformanceStorage
{
    // Runs sequentially inside the one bounded storage worker, never on the UI.
    // This is our source-built read-only variant, not the unmodified NuGet binary.
    static void EnrichWithToolkit(List<StorageDisk> disks,List<string> notes)
    {
        try
        {
            var devices=Storage.GetDisks();
            notes.Add("DiskInfoToolkit 2.2.1 read-only: devices="+devices.Count);
            var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in devices)
            {
                if(string.IsNullOrWhiteSpace(item.DevicePath)||!used.Add(item.DevicePath))continue;
                int? number=item.StorageDeviceNumber is <=int.MaxValue?(int)item.StorageDeviceNumber.Value:null;
                var matches=disks.Select((disk,index)=>(disk,index)).Where(x=>ToolkitIdentity(x.disk,item.DevicePath,number,item.ProductName)).ToArray();
                if(matches.Length>1){notes.Add("toolkit:AmbiguousIdentity");continue;}
                int index=matches.Length==1?matches[0].index:-1;
                if(index<0&&disks.Any(d=>number.HasValue&&d.Index==number))
                {notes.Add("toolkit:ConflictingIdentity");continue;}
                var original=index>=0?disks[index]:new StorageDisk {DeviceId=item.DevicePath,Index=number,Model=item.ProductName};
                var updated=MergeToolkit(original,item);
                if(index>=0)disks[index]=updated;else disks.Add(updated);
            }
        }
        catch(Exception ex){notes.Add("DiskInfoToolkit:"+ex.GetType().Name);}
    }
    internal static bool ToolkitIdentity(StorageDisk disk,string path,int? number,string model)
    {
        if(disk.Index.HasValue&&number.HasValue&&disk.Index!=number)return false;
        bool physical=string.Equals(disk.DeviceId,path,StringComparison.OrdinalIgnoreCase)||
            (disk.Index.HasValue&&number.HasValue&&disk.Index==number);
        if(!physical)return false;
        string Normalize(string value)=>System.Text.RegularExpressions.Regex.Replace(value.Trim(),@"\s+"," ").ToUpperInvariant();
        return string.IsNullOrWhiteSpace(disk.Model)||string.IsNullOrWhiteSpace(model)||Normalize(disk.Model)==Normalize(model);
    }
    static StorageDisk MergeToolkit(StorageDisk disk,StorageDevice item)
    {
        string source="DiskInfoToolkit 2.2.1 read-only";
        var diagnostics=disk.Diagnostics.ToList();
        // Trace messages may contain device serials/paths. Keep operation identifiers only.
        diagnostics.Add("toolkit:strategy="+item.ProbeStrategy+"; transport="+item.TransportKind);
        diagnostics.Add("toolkit:controller="+item.Controller.Name+"; service="+item.Controller.Service+"; family="+item.Controller.Family);
        diagnostics.Add("toolkit:attributes="+item.SmartAttributes.Count+"; nvmeBytes="+item.Nvme.SmartLogData.Length);
        var attrs=item.SmartAttributes.Select(a=>new StorageSmartAttribute(a.ID,a.CurrentValue,a.WorstValue,
            a.ThresholdValue>0?a.ThresholdValue:null,a.RawValue,(a.StatusFlags&1)!=0)).ToArray();
        bool valid=attrs.Length>0&&attrs.Select(a=>a.Id).Distinct().Count()==attrs.Length&&attrs.All(a=>a.Id>0&&a.Raw<(1UL<<48));
        if(valid&&disk.SmartAttributes.Count==0)
        {
            var limits=ClassifyAtaThresholds(attrs,trustworthyThresholdPage:true);
            var decoded=DecodeAtaSensors(disk.Model,disk.MediaType,attrs);
            // Toolkit probing is a fallback. Missing measurements cannot erase
            // the already validated Windows storage counter readings.
            bool sc311=IsSc311Family(decoded.Profile);
            bool vendorProviderMismatch=WearEstimatesConflict(disk.WearPercent,decoded.WearPercent);
            bool conflictingSources=vendorProviderMismatch&&!sc311;
            bool conflictingAttributes=decoded.LifeConflict||decoded.LifeInvalid;
            bool invalidLife=conflictingSources||conflictingAttributes;
            bool withholdProviderLife=sc311&&!decoded.RemainingLifePercent.HasValue;
            disk=disk with {SmartAttributes=attrs,AtaSmartSource=source,AtaProfile=decoded.Profile,
                AtaPrefailureThresholdExceeded=limits.Exceeded,
                AtaThresholdAtLimit=limits.AtLimit,
                AtaSectorWarning=disk.MediaType=="HDD"?HddSectorWarning(attrs):null,
                TemperatureC=disk.TemperatureC??decoded.TemperatureC,
                TemperatureSource=disk.TemperatureC.HasValue?disk.TemperatureSource:decoded.TemperatureC.HasValue?source:"None",
                RemainingLifePercent=(invalidLife||withholdProviderLife)?null:(decoded.RemainingLifePercent??disk.RemainingLifePercent),
                RemainingLifeSource=(invalidLife||withholdProviderLife)?"None":decoded.RemainingLifePercent.HasValue?source+":"+decoded.Profile+":E9":disk.RemainingLifeSource,
                WearPercent=(invalidLife||withholdProviderLife)?null:(decoded.WearPercent??disk.WearPercent),
                WearSource=(invalidLife||withholdProviderLife)?"None":decoded.WearPercent.HasValue?source+":"+decoded.Profile+":E9":disk.WearSource,
                PowerOnHours=disk.PowerOnHours??decoded.PowerOnHours,PowerOnHoursSource=disk.PowerOnHours.HasValue?disk.PowerOnHoursSource:decoded.PowerOnHours.HasValue?source:"None",
                PowerOnCount=disk.PowerOnCount??decoded.PowerOnCount,
                HostReadsBytes=disk.HostReadsBytes??decoded.HostReadsBytes,
                HostWritesBytes=disk.HostWritesBytes??decoded.HostWritesBytes};
            if(conflictingAttributes)diagnostics.Add(decoded.LifeInvalid?"ata-life:InvalidAttribute":"ata-life:ConflictingAttributes");
            if(conflictingSources)diagnostics.Add("ata-life:ConflictingSources");
            if(sc311&&vendorProviderMismatch)diagnostics.Add("reliability:Sc311WindowsWearDisagreesWithE9");
        }
        // Decode the raw NVMe page with the same checks used by our native source.
        var raw=item.Nvme.SmartLogData;
        if(raw.Length==512&&disk.NvmeCriticalWarning is null)
        {
            byte[] descriptor=new byte[560];Put(descriptor,0,48);Put(descriptor,4,48);Put(descriptor,8,3);Put(descriptor,12,2);Put(descriptor,24,40);Put(descriptor,28,512);raw.CopyTo(descriptor,48);
            var log=DecodeNvme(descriptor);
            if(log is not null)disk=disk with {NvmeCriticalWarning=log.CriticalWarning,NvmeMediaErrors=log.MediaErrors,
                NvmeAvailableSparePercent=log.AvailableSparePercent,NvmeSpareThresholdPercent=log.SpareThresholdPercent,NvmeSensorTemperaturesC=log.SensorTemperaturesC,
                TemperatureC=log.TemperatureC,TemperatureSource=source,WearPercent=log.WearPercent,WearSource=source,
                RemainingLifePercent=Math.Max(0,100-log.WearPercent),RemainingLifeSource=source,PowerOnHours=log.PowerOnHours,PowerOnHoursSource=source};
            else diagnostics.Add("nvme-health:InvalidResponse");
        }
        // The newly read ATA thresholds must be reflected in the condition,
        // not merely copied into diagnostics for a future refresh.
        disk=PromoteAtaThresholdHealth(disk);
        // A false prediction flag alone is never mapped to a healthy disk.
        return disk with {Index=disk.Index??(item.StorageDeviceNumber is <=int.MaxValue?(int)item.StorageDeviceNumber.Value:null),TotalBytes=disk.TotalBytes??(item.DiskSizeBytes>0?item.DiskSizeBytes:null),
            FailurePredicted=disk.FailurePredicted??item.PredictsFailure,Diagnostics=diagnostics.ToArray()};
    }
}
