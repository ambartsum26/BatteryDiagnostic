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

        // Existing pure aggregation entry point can be tested on the baseline.
        // Enumeration-failure and engine-subtotal tests need the small WMI helper
        // extraction described in the audit; no fake live COM provider is invoked.
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
        if(failures.Count>0)throw new Exception("Audit sensor regressions: "+string.Join("; ",failures));
    }
}
