using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DiskInfoToolkit;
using DiskInfoToolkit.Devices;
using DiskInfoToolkit.Smart;

namespace BatteryDiagnostic;

internal static class StorageRegressionTests
{
    internal static void Run()
    {
        static void Check(bool ok,string reason){if(!ok)throw new Exception("Storage regression: "+reason);}
        var csmi=typeof(Storage).Assembly.GetType("DiskInfoToolkit.Probes.CsmiProbe")!;
        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var read=csmi.GetMethod("TryReadDiagnosticPages",flags)!;
        var gate=csmi.GetMethod("IsReadOnlyAtaCommand",flags)!;
        var identity=csmi.GetMethod("IdentifyMatches",flags)!;
        Check(read!=null&&gate!=null&&identity!=null,"CSMI read-only reader and identity guard exist");
        byte[] page=new byte[512];page[0]=1;page[2]=0xE9;page[5]=99;page[6]=99;page[7]=99;
        page[511]=(byte)((256-page.Take(511).Sum(b=>(int)b)%256)%256);
        var commands=new List<byte>();
        Func<byte,byte[]> reader=feature=>{commands.Add(feature);return feature==0xD0?page:null!;};
        object?[] args=[reader,null];
        Check((bool)read!.Invoke(null,args)!,"valid SMART data survives an unsupported threshold read");
        var attrs=(List<SmartAttributeEntry>)args[1]!;
        Check(attrs.Count==1&&attrs[0].ID==0xE9&&attrs[0].RawValue==99&&attrs[0].ThresholdValue==0,"real lifetime retained, missing threshold stays absent");
        Check(commands.SequenceEqual(new byte[]{0xD0,0xD1}),"reader sends only SMART READ DATA and READ THRESHOLDS");
        var decoded=PerformanceStorage.LibCdiDecodedSensors("SK hynix SC311 SATA 256GB","SSD",[new(0xE9,99,99,null,99,false)]);
        Check(decoded.RemainingLifePercent==99,"SC311 lifetime comes from the validated E9 raw value");
        commands.Clear();page[10]^=1;args=[reader,null];
        Check(!(bool)read.Invoke(null,args)!&&commands.SequenceEqual(new byte[]{0xD0}),"invalid SMART data never becomes health or triggers threshold/enable commands");
        bool Allowed(byte cmd,byte feature,int count)=>(bool)gate!.Invoke(null,[cmd,feature,count])!;
        Check(Allowed(0xEC,0,512)&&Allowed(0xB0,0xD0,512)&&Allowed(0xB0,0xD1,512),"identify and SMART reads allowed");
        Check(!Allowed(0xB0,0xD8,0)&&!Allowed(0xB0,0xD4,512)&&!Allowed(0xEF,0,512)&&!Allowed(0xB0,0xD0,0),"SMART enable, self-test, feature writes and malformed reads blocked");
        byte[] identify=new byte[512];
        void WriteWords(int word,int length,string text)
        {
            var bytes=Encoding.ASCII.GetBytes(text.PadRight(length*2));
            for(int i=0;i<length*2;i+=2){identify[word*2+i]=bytes[i+1];identify[word*2+i+1]=bytes[i];}
        }
        WriteWords(27,20,"SK hynix SC311 SATA 256GB");WriteWords(10,10,"FIXTURE-001");
        var device=new StorageDevice {ProductName="SK hynix SC311 SATA 256GB",SerialNumber="FIXTURE-001"};
        Check((bool)identity!.Invoke(null,[device,identify])!,"matching model and serial accepted");
        device.SerialNumber="FIXTURE-002";Check(!(bool)identity.Invoke(null,[device,identify])!,"same-model neighbouring member rejected by serial");
        device.SerialNumber="";device.ProductName="Other SSD";Check(!(bool)identity.Invoke(null,[device,identify])!,"wrong CSMI member cannot replace the physical disk identity");
        Check(PerformanceStorage.WmiAuthenticationLevel==6,"WMI services and enumerators request packet privacy");
        foreach(int hr in new[]{unchecked((int)0x80070005),unchecked((int)0x80041003)})
            Check(PerformanceStorage.WmiFailure("EnumeratorNext",new COMException("fixture",hr)).Contains("hr=0x"+hr.ToString("X8")),"WMI preserves the exact failure code and stage");
        var notes=new List<string>();LibCdiStorage.Enrich([],notes);
        Check(notes.SequenceEqual(new[]{"libcdi:ReadOnlyPolicyDisabled"}),"unverified native SMART-enable initializer is never run");
    }
}
