using System.Reflection;
using System.Runtime.CompilerServices;
using DiskInfoToolkit;

namespace BatteryDiagnostic;

internal static class IntegrationTests
{
    internal static void Run()
    {
        LibCdiStorage.SelfTest();
        void Check(bool value,string reason){if(!value)throw new Exception("Integration: "+reason);}
        var disk=new StorageDisk {Index=0,DeviceId=@"\\.\PhysicalDrive0",Model="SK hynix SC311 SATA 256GB"};
        Check(PerformanceStorage.ToolkitIdentity(disk,@"\\.\PhysicalDrive0",0,disk.Model),"same physical disk");
        Check(!PerformanceStorage.ToolkitIdentity(disk,@"\\.\PhysicalDrive1",1,disk.Model),"identical models on different drives");
        Check(!PerformanceStorage.ToolkitIdentity(disk,@"\\.\PhysicalDrive0",0,"Other SSD"),"conflicting model");
        Check(!PerformanceStorage.ToolkitIdentity(disk,"unknown",null,disk.Model),"model alone cannot map a disk");
        var failAt = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(5,10,10,10,0,true)],true);
        Check(failAt is (true, false),"Pre-fail at threshold is a failure");
        var failBelow = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(5,9,9,10,0,true)],true);
        Check(failBelow.Exceeded == true,"Pre-fail below threshold is a failure");
        var passAbove = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(5,11,11,10,0,true)],true);
        Check(passAbove is (false, false),"Pre-fail above threshold is okay");
        var warnAt = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(0xB1,10,10,10,0,false)],true);
        Check(warnAt is (false, true),"supported non-Pre-fail at threshold warns");
        var noThreshold = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(5,10,10,null,0,true)],true);
        Check(noThreshold is (null, null),"no drive threshold means no guessed failure");
        var noPage = PerformanceStorage.ClassifyAtaThresholds([new StorageSmartAttribute(5,10,10,10,0,true)],false);
        Check(noPage.Exceeded is null,"unverified SMART threshold page cannot flag failure");
        var prioritized = PerformanceStorage.ClassifyAtaThresholds([
            new StorageSmartAttribute(0xB1,10,10,10,0,false),new StorageSmartAttribute(5,10,10,10,0,true)],true);
        Check(prioritized is (true, true),"critical threshold wins even alongside a caution");
        var promoted = PerformanceStorage.PromoteAtaThresholdHealth(disk with {HealthCode="ProviderHealthy",AtaPrefailureThresholdExceeded=true});
        Check(promoted.HealthCode=="AtaThresholdExceeded" && DiskAssessmentEngine.Evaluate(promoted).StateKey=="smartLimit","toolkit Pre-fail threshold promotes healthy provider to critical");
        var caution = PerformanceStorage.PromoteAtaThresholdHealth(disk with {HealthCode="ProviderHealthy",AtaThresholdAtLimit=true});
        Check(caution.HealthCode=="AtaThresholdAtLimit" && DiskAssessmentEngine.Evaluate(caution).StateKey=="smartAtLimit","non-Pre-fail equality stays caution");
        var priorFailure = PerformanceStorage.PromoteAtaThresholdHealth(disk with {HealthCode="FailurePredicted",AtaThresholdAtLimit=true});
        Check(priorFailure.HealthCode=="FailurePredicted","existing failure cannot be replaced by threshold warning");
        // Verify every removed SMART-enable entry point without any hardware handle.
        var assembly=typeof(Storage).Assembly;
        var methods=assembly.GetTypes().SelectMany(t=>t.GetMethods(BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)
            .Where(m=>m.Name.StartsWith("TryEnableSmart",StringComparison.Ordinal)).Select(m=>(Type:t,Method:m))).ToArray();
        Check(methods.Length==7,"all seven upstream SMART-enable paths accounted for");
        foreach(var entry in methods)
        {
            var args=entry.Method.GetParameters().Select(p=>p.ParameterType.IsValueType?Activator.CreateInstance(p.ParameterType):null).ToArray();
            object? instance=entry.Method.IsStatic?null:RuntimeHelpers.GetUninitializedObject(entry.Type);
            Check(Equals(entry.Method.Invoke(instance,args),false),"SMART enable disabled: "+entry.Method.Name);
        }
        var parser=assembly.GetType("DiskInfoToolkit.Probes.SmartProbe")!.GetMethod("ParseSmartPages",BindingFlags.Public|BindingFlags.Static)!;
        int Count(byte[] page)=>((System.Collections.ICollection)parser.Invoke(null,[page,null])!).Count;
        byte[] sector=new byte[512];sector[0]=1;sector[2]=0xE7;sector[5]=100;sector[6]=100;sector[7]=95;
        sector[511]=(byte)((256-sector.Take(511).Sum(b=>(int)b)%256)%256);
        Check(Count(sector)==1,"valid ATA sector decoded by toolkit");
        sector[10]^=1;Check(Count(sector)==0,"bad checksum rejected by toolkit");
        Check(Count(new byte[362])==0&&Count(new byte[512])==0,"short and empty SMART pages rejected");
    }
}
