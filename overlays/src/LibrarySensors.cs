using LibreHardwareMonitor.Hardware;
using System.Runtime.Intrinsics.X86;

namespace BatteryDiagnostic;

// One independent reader. Storage and SMBus groups remain disabled: their writes,
// device enumeration and slow calls must not occur on the GPU/CPU refresh path.
internal static class LibrarySensors
{
    static readonly object Gate=new();
    static Computer? computer;
    static bool cpuOpened;
    static int running;
    static long nextPoll, sampled;
    static GpuLive[] cached=[];
    static double? cpu;
    static string diagnostic="LibreHardwareMonitor:NotSampled";
    internal static string Diagnostic {get {lock(Gate)return diagnostic;}}
    internal static bool IsSupportedAmd
    {
        get
        {
            if(!X86Base.IsSupported)return false;
            var vendor=X86Base.CpuId(0,0);
            if(vendor.Ebx!=0x68747541||vendor.Edx!=0x69746e65||vendor.Ecx!=0x444d4163)return false;
            var id=X86Base.CpuId(1,0);int family=(id.Eax>>8)&15;
            if(family==15)family+=(id.Eax>>20)&255;
            return family is 0x17 or 0x19 or 0x1A;
        }
    }
    internal static (double? Cpu,GpuLive[] Gpus) ReadCached(GpuInventory[] adapters)
    {
        long now=Environment.TickCount64;
        lock(Gate)
        {
            if(now>=nextPoll&&Interlocked.CompareExchange(ref running,1,0)==0)
            {
                nextPoll=now+3000;
                _=Task.Run(()=>Poll(adapters));
            }
            return now-sampled is >=0 and <=10000?(cpu,cached):(null,[]);
        }
    }
    static void Poll(GpuInventory[] adapters)
    {
        try
        {
            // GPU APIs use the installed display driver. Kernel CPU access is
            // only attempted after explicit activation and our signed-driver preflight.
            bool allowCpu=PerformanceCpuTemperature.Enabled&&PerformanceCpuTemperature.IsAdministrator&&
                PerformanceCpuTemperature.CheckInstalledDriver().State==CpuTemperatureState.Ready;
            if(computer is null)
            {
                computer=new Computer {IsGpuEnabled=true,IsCpuEnabled=allowCpu,
                    IsMemoryEnabled=false,IsMotherboardEnabled=false,IsStorageEnabled=false,
                    IsControllerEnabled=false,IsNetworkEnabled=false,IsPsuEnabled=false,IsBatteryEnabled=false};
                computer.Open();cpuOpened=allowCpu;
            }
            else if(allowCpu!=cpuOpened){computer.IsCpuEnabled=allowCpu;cpuOpened=allowCpu;}
            var hardware=computer.Hardware;
            foreach(var h in hardware)h.Update();
            var cards=hardware.Where(h=>h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel).ToArray();
            var values=new List<GpuLive>();
            for(int i=0;i<adapters.Length;i++)
            {
                int match=PerformanceTemperatures.UniqueModelMatch(adapters,i,cards.Select(c=>c.Name).ToArray());
                if(match<0)continue;
                var sensors=cards[match].Sensors;
                double? Value(SensorType type,string name)=>sensors.FirstOrDefault(s=>s.SensorType==type&&s.Name==name)?.Value;
                values.Add(new(adapters[i].Id,Percentage(Value(SensorType.Load,"GPU Core")),null,null,
                    Frequency(Value(SensorType.Clock,"GPU Core")),Temperature(Value(SensorType.Temperature,"GPU Core"))));
            }
            var processors=hardware.Where(h=>h.HardwareType==HardwareType.Cpu).ToArray();
            // One package only; do not silently show one CPU's temperature as a multi-socket total.
            double? temperature=processors.Length==1?SelectCpuTemperature(processors[0].Sensors.Select(s=>(s.SensorType,s.Name,(double?)s.Value))):null;
            lock(Gate){cpu=allowCpu?temperature:null;cached=values.ToArray();sampled=Environment.TickCount64;
                diagnostic="LibreHardwareMonitor 0.9.6: GPU="+cards.Length+"; CPU="+(allowCpu?"activated":"disabled")+"; storage/SPD/control groups disabled";}
        }
        catch(Exception ex){lock(Gate){cpu=null;cached=[];diagnostic="LibreHardwareMonitor:"+ex.GetType().Name;}}
        finally{Volatile.Write(ref running,0);}
    }
    internal static double? SelectCpuTemperature(IEnumerable<(SensorType Type,string Name,double? Value)> sensors)
    {
        var data=sensors.Where(s=>s.Type==SensorType.Temperature).ToArray();
        foreach(string name in new[]{"CPU Package","Core (Tdie)","Core (Tctl/Tdie)"})
        {
            var match=data.Where(s=>s.Name==name).ToArray();
            if(match.Length==1&&Temperature(match[0].Value) is {} value)return value;
        }
        var cores=data.Where(s=>s.Name.StartsWith("CPU Core #",StringComparison.Ordinal)&&!s.Name.Contains("Distance",StringComparison.Ordinal)).ToArray();
        return cores.Length>0&&cores.All(s=>Temperature(s.Value).HasValue)?cores.Max(s=>Temperature(s.Value)):null;
    }
    static double? Temperature(double? value)=>value.HasValue&&double.IsFinite(value.Value)&&value is >=-40 and <=150?value:null;
    static double? Percentage(double? value)=>value.HasValue&&double.IsFinite(value.Value)&&value is >=0 and <=100?value:null;
    static double? Frequency(double? value)=>value.HasValue&&double.IsFinite(value.Value)&&value is >0 and <=100000?value:null;
    internal static void SelfTest()
    {
        void Check(bool pass,string reason){if(!pass)throw new Exception("Library sensors: "+reason);}
        Check(SelectCpuTemperature([(SensorType.Temperature,"Core (Tctl)",85)]) is null,"Tctl alone not substituted for die temperature");
        Check(SelectCpuTemperature([(SensorType.Temperature,"Core (Tdie)",65)])==65,"AMD die value");
        Check(SelectCpuTemperature([(SensorType.Temperature,"CPU Package",55),(SensorType.Temperature,"CPU Core #1",60)])==55,"package preferred");
        Check(SelectCpuTemperature([(SensorType.Temperature,"CPU Core #1",52),(SensorType.Temperature,"CPU Core #2",61)])==61,"hottest actual core");
        Check(SelectCpuTemperature([(SensorType.Temperature,"CPU Package",double.NaN)]) is null,"NaN rejected");
        Check(Temperature(0)==0&&Percentage(0)==0&&Frequency(0) is null,"zero validity depends on metric");
    }
}
