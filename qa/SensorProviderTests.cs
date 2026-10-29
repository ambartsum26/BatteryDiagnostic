using LibreHardwareMonitor.Hardware;
namespace BatteryDiagnostic;
internal static class SensorProviderTests
{
    internal static void Run()
    {
        void Check(bool value,string name){if(!value)throw new Exception("Provider integration: "+name);}
        var args=SmartctlStorage.Arguments(Path.Combine(AppContext.BaseDirectory,"smartmontools","drivedb.h"),7);
        Check(args.SequenceEqual(new[]{"--json=c","--info","--health","--attributes","--quietmode=noserial",
            "--drivedb="+Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"smartmontools","drivedb.h")),"--","/dev/pd7"}),"fixed read-only argument list");
        bool invalid=false;try{SmartctlStorage.Arguments("x",-1);}catch(ArgumentOutOfRangeException){invalid=true;}
        Check(invalid,"unbounded or arbitrary device path forbidden");
        Check(LibrarySensors.SelectCpuTemperature([(SensorType.Temperature,"Core #1",51),(SensorType.Temperature,"Core #2",62)])==62,"AMD K8 core naming");
        Check(LibrarySensors.SelectCpuTemperature([(SensorType.Temperature,"CPU (Tdie)",67)])==67,"AMD die naming");
        Check(LibrarySensors.SelectCpuTemperature([(SensorType.Temperature,"Core (Tctl)",95)]) is null,"no uncorrected control-temperature substitution");
        var sample=new SmartctlSample{DriveIndex=0,Model="SSD Fixture",MediaType="SSD",RemainingLifePercent=80,WearPercent=20};
        var disk=new StorageDisk{Index=0,Model=sample.Model,MediaType="SSD",RemainingLifePercent=99,RemainingLifeSource="AtaProfile"};
        var merged=SmartctlMerge.Apply(disk,sample);
        Check(DiskAssessmentEngine.HealthPercentage(merged) is null,"conflicting smartctl value cannot enter UI percentage");
        merged=SmartctlMerge.Apply(disk,sample with{Passed=false});
        Check(DiskAssessmentEngine.Evaluate(merged).StateKey=="predicted","failure retains priority over conflicting endurance");
        Console.WriteLine("PASS provider integration: bounded device selection, read-only arguments, AMD labels and disk warning precedence.");
    }
}
