using System.Reflection;

namespace BatteryDiagnostic;

// Deterministic fixtures only: no live WMI, kernel handles, installers or sensors.
internal static class AuditSensorTests
{
    internal static void Run()
    {
        var failures=new List<string>();
        void Test(string name,Action test)
        {
            try { test(); }
            catch(Exception ex) { failures.Add(name+": "+(ex is TargetInvocationException t? t.InnerException?.Message:ex.Message)); }
        }
        void Check(bool pass,string reason) { if(!pass)throw new Exception(reason); }
        string Temp(string id,int value)=>$"<temp><id>{id}</id><label>CPU</label><value>{value}</value></temp>";
        Test("AIDA distinct packages",()=>Check(ExternalCpuTemperature.ParseAida(Temp("TCPU1PKG",50)+Temp("TCPU2PKG",80)).TemperatureC==null,
            "multiple CPU packages must not silently report CPU #1"));
        Test("AIDA distinct core packages",()=>Check(ExternalCpuTemperature.ParseAida(Temp("TCC-1-1",50)+Temp("TCC-2-1",80)).TemperatureC==null,
            "multiple CPU core groups must not silently report CPU #1"));
        Test("AIDA one package",()=>Check(ExternalCpuTemperature.ParseAida(Temp("TCPU1PKG",50)).TemperatureC==50,"single package remains supported"));
        Test("AIDA one core group",()=>Check(ExternalCpuTemperature.ParseAida(Temp("TCC-1-1",50)+Temp("TCC-1-2",65)).TemperatureC==65,"single package hottest core"));

        Test("WMI invalid memory subtotal",()=>
        {
            var method=typeof(PerformanceSensors).GetMethod("SumCounters",BindingFlags.Static|BindingFlags.NonPublic)!;
            Dictionary<string,object?>[] rows=[new() {["DedicatedUsage"]=10UL},new() {["DedicatedUsage"]=null}];
            Check(method.Invoke(null,[rows,"DedicatedUsage"])==null,"missing memory row invalidates subtotal");
        });
        Test("GPU invalid engine subtotal",()=>Check(PerformanceGpuCounters.BusiestEngine([
            new("pid_1_luid_0x0_0xa_phys_0_eng_0",30,null),
            new("pid_2_luid_0x0_0xa_phys_0_eng_0",null,null)],"luid_0x00000000_0x0000000a")==null,
            "missing matching process invalidates adapter utilization"));
        const string id="luid_0x00000000_0x0000000a";
        Dictionary<string,object?> Row(int pid,object? value)=>new() {
            ["Name"]=$"pid_{pid}_luid_0x0_0xa_phys_0_eng_0",["UtilizationPercentage"]=value};
        GpuInventory[] adapters=[new(id,"Fixture",GpuKind.Dedicated,1024,1024,null)];
        Test("WMI invalid engine subtotal",()=>Check(PerformanceSensors.AggregateWmiGpu(adapters,[],[Row(1,30),Row(2,null)])[0].UsagePercent==null,
            "partial matching process readings are unavailable"));
        Test("WMI complete engine sum",()=>Check(PerformanceSensors.AggregateWmiGpu(adapters,[],[Row(1,30),Row(2,25)])[0].UsagePercent==55,
            "complete process utilization sums correctly"));
        IEnumerable<Dictionary<string,object?>> Interrupted()
        { yield return Row(1,30);throw new InvalidOperationException("fixture provider failure"); }
        Test("WMI interrupted enumeration",()=>Check(PerformanceSensors.CollectWmiRows(Interrupted(),100,()=>false).Count==0,
            "a provider failure discards already collected rows"));
        Test("WMI expired enumeration",()=>Check(PerformanceSensors.CollectWmiRows([Row(1,30)],100,()=>true).Count==0,
            "expired partial enumeration is discarded"));
        Test("WMI complete enumeration",()=>Check(PerformanceSensors.CollectWmiRows([Row(1,30),Row(2,25)],100,()=>false).Count==2,
            "complete enumeration retains all rows"));
        Test("WMI row budget",()=>Check(PerformanceSensors.CollectWmiRows([Row(1,30),Row(2,25)],2,()=>false).Count==0,
            "row budget cannot certify complete enumeration"));
        if(failures.Count>0)throw new Exception("Audit sensor regressions: "+string.Join("; ",failures));
    }
}
