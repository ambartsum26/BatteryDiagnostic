namespace BatteryDiagnostic;

internal enum DiskResourceGrade { Unknown, Normal, Low, Exhausted }
internal sealed record DiskAssessment(double? Remaining,DiskResourceGrade Resource,string StateKey,Color Color);
internal static class DiskAssessmentEngine
{
    internal static DiskResourceGrade Classify(double? remaining)=>remaining switch
    {
        null=>DiskResourceGrade.Unknown,
        {} n when !double.IsFinite(n)||n<0||n>100=>DiskResourceGrade.Unknown,
        0=>DiskResourceGrade.Exhausted,
        <=10=>DiskResourceGrade.Low,
        _=>DiskResourceGrade.Normal
    };
    internal static DiskAssessment Evaluate(StorageDisk disk)
    {
        bool invalidData=disk.Diagnostics.Any(d=>d.StartsWith("ata-life:",StringComparison.Ordinal)||d=="nvme-health:InvalidResponse");
        var grade=Classify(invalidData?null:disk.RemainingLifePercent);
        double? remaining=grade==DiskResourceGrade.Unknown?null:disk.RemainingLifePercent;
        bool nvme=disk.NvmeCriticalWarning.HasValue||disk.NvmeAvailableSparePercent.HasValue||disk.HealthSource=="NvmeHealthLog"||disk.RemainingLifeSource.StartsWith("NvmeHealthLog",StringComparison.Ordinal);
        if(disk.NvmeCriticalWarning>0)return new(remaining,grade,"nvmeCritical",Theme.Danger);
        if(disk.NvmeAvailableSparePercent is {} spare&&disk.NvmeSpareThresholdPercent is >0 and <=100)
        {
            if(spare<disk.NvmeSpareThresholdPercent)return new(remaining,grade,"spareLow",Theme.Danger);
        }
        // A controller resource estimate and actual warning flags are separate inputs.
        // A large remaining percentage cannot hide a reported hardware failure.
        string? fault=disk.HealthCode switch
        {
            "Unhealthy"=>"unhealthy","FailurePredicted"=>"predicted","AtaThresholdExceeded"=>"smartLimit","AtaThresholdAtLimit"=>"smartAtLimit","Warning"=>"warning","AtaSectorWarning"=>"sectorWarning",_=>null
        };
        if(fault!=null&&fault is not ("warning" or "sectorWarning" or "smartAtLimit"))return new(remaining,grade,fault,Theme.Danger);
        // A newly populated fallback SMART page may not have updated the old
        // Windows health code yet. Do not let a Pre-fail threshold be downgraded.
        if(disk.AtaPrefailureThresholdExceeded==true)return new(remaining,grade,"smartLimit",Theme.Danger);
        if(disk.NvmeMediaErrors>0)return new(remaining,grade,"mediaErrors",Theme.Warning);
        if(invalidData)return new(null,DiskResourceGrade.Unknown,"dataInvalid",Theme.Muted);
        if(grade==DiskResourceGrade.Exhausted&&!nvme&&disk.AtaProfile!="None")return new(remaining,grade,"smartLimit",Theme.Danger);
        if(fault!=null)return new(remaining,grade,fault,Theme.Warning);
        if(disk.AtaThresholdAtLimit==true)return new(remaining,grade,"smartAtLimit",Theme.Warning);
        if(disk.NvmeAvailableSparePercent==disk.NvmeSpareThresholdPercent&&disk.NvmeSpareThresholdPercent is >0 and <100)return new(remaining,grade,"spareAtLimit",Theme.Warning);
        if(grade is DiskResourceGrade.Low or DiskResourceGrade.Exhausted)return new(remaining,grade,"resourceCaution",Theme.Warning);
        string state=disk.HealthCode switch {"ProviderHealthy"=>"healthy","NoFailurePredicted"=>"insufficient","NvmeNoCriticalWarning"=>"nvmeClear","EnduranceExceeded"=>"warning",_=>remaining.HasValue?"insufficient":"unknown"};
        if((state is "insufficient" or "unknown")&&disk.SmartAttributes.Count>0&&disk.AtaPrefailureThresholdExceeded==false&&disk.AtaThresholdAtLimit!=true)state="ataClear";
        return new(remaining,grade,state,state is "ataClear" or "nvmeClear" or "healthy"?Theme.Accent:state=="warning"?Theme.Warning:Theme.Muted);
    }
    internal static void SelfTest()
    {
        static void Check(bool v,string what){if(!v)throw new Exception("Disk resource: "+what);}
        Check(Classify(null)==DiskResourceGrade.Unknown&&Classify(double.NaN)==DiskResourceGrade.Unknown&&Classify(101)==DiskResourceGrade.Unknown,"invalid data stays ungraded");
        Check(Classify(100)==DiskResourceGrade.Normal&&Classify(11)==DiskResourceGrade.Normal,"normal remaining endurance");
        Check(Classify(10)==DiskResourceGrade.Low&&Classify(1)==DiskResourceGrade.Low,"ten percent caution");
        Check(Classify(0)==DiskResourceGrade.Exhausted&&Classify(-1)==DiskResourceGrade.Unknown,"zero versus invalid");
        var failed=Evaluate(new StorageDisk {RemainingLifePercent=90,HealthCode="FailurePredicted"});
        Check(failed.StateKey=="predicted"&&failed.Remaining==90&&failed.Color==Theme.Danger,"failure overrides apparently large resource");
        Check(Evaluate(new StorageDisk {HealthCode="NoFailurePredicted"}).StateKey=="insufficient","negative SMART prediction never invents a percentage");
        Check(Evaluate(new StorageDisk {HealthCode="ProviderHealthy",AtaPrefailureThresholdExceeded=true}).StateKey=="smartLimit","an existing provider healthy status cannot hide a critical Pre-fail threshold");
        Check(Evaluate(new StorageDisk {HealthCode="ProviderHealthy",AtaThresholdAtLimit=true}).StateKey=="smartAtLimit","supported non-Pre-fail equality is a caution when no stronger failure exists");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=0,HealthCode="EnduranceExceeded"}).StateKey=="resourceCaution","resource exhaustion does not prove a physical failure");
        Check(Evaluate(new StorageDisk {HealthCode="AtaSectorWarning"}).StateKey=="sectorWarning","HDD sectors warn without invented percentage");
        Check(Evaluate(new StorageDisk {HealthCode="NvmeNoCriticalWarning",NvmeCriticalWarning=0,NvmeMediaErrors=2}).StateKey=="mediaErrors","media errors cannot be hidden by clear critical flags");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=95,NvmeCriticalWarning=1}).StateKey=="nvmeCritical","NVMe spare warning overrides high life");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=95,NvmeAvailableSparePercent=9,NvmeSpareThresholdPercent=10}).StateKey=="spareLow","reserve below controller threshold");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=95,NvmeAvailableSparePercent=10,NvmeSpareThresholdPercent=10}).StateKey=="spareAtLimit","reserve at threshold cautions");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=0,NvmeCriticalWarning=0}).StateKey=="resourceCaution","NVMe zero life without flags is caution");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=0,AtaProfile="SamsungSataConsumerB1"}).StateKey=="smartLimit","SATA life zero is bad per known profile");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=0,AtaProfile="SamsungSataConsumerB1",HealthCode="Warning"}).Color==Theme.Danger,"generic caution cannot hide exhausted known SATA life");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=100,HealthCode="ProviderHealthy",Diagnostics=["ata-life:ConflictingAttributes"]}) is {Remaining:null,StateKey:"dataInvalid"},"contradictory controller data cannot become green through provider fallback");
        Check(Evaluate(new StorageDisk {RemainingLifePercent=99,HealthCode="ProviderHealthy",Diagnostics=["ata-life:ConflictingSources"]}) is {Remaining:null,StateKey:"dataInvalid"},"SC311 Windows/ATA contradictory estimates cannot become 99 percent healthy");
    }
}
