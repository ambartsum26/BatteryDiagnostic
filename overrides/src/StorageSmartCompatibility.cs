using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BatteryDiagnostic;

internal static partial class PerformanceStorage
{
    // Read-only SAT-12/SAT-16 transport. No bridge-specific vendor commands,
    // SMART enable, mode change, self-test or arbitrary command execution.
    const uint IoctlScsiPassThrough=0x0004D004;
    static bool AtaModelMatches(string reported,string identified)
    {
        string? a=Identity(reported),b=Identity(identified);
        return a is {Length:>=8}&&b is {Length:>=8}&&(a==b||a==b+"USBDEVICE"||a==b+"SCSIDISKDEVICE");
    }
    static byte[] SatQuery(byte command,byte feature,int cdbLength,int pointerSize)
    {
        if(pointerSize is not (4 or 8)||cdbLength is not (12 or 16))throw new ArgumentOutOfRangeException();
        if(!((command==0xEC&&feature==0)||(command==0xB0&&feature is 0xD0 or 0xD1)))throw new ArgumentOutOfRangeException(nameof(command));
        int header=pointerSize==8?56:44,offsetField=pointerSize==8?24:20,senseField=pointerSize==8?32:24,cdb=pointerSize==8?36:28;
        int data=header+32;
        var bytes=new byte[data+512];
        BitConverter.GetBytes((ushort)header).CopyTo(bytes,0);
        bytes[6]=(byte)cdbLength;bytes[7]=32;bytes[8]=1; // sense length, SCSI_IOCTL_DATA_IN
        Put(bytes,12,512);Put(bytes,16,3);
        if(pointerSize==8)BitConverter.GetBytes((ulong)data).CopyTo(bytes,offsetField);else Put(bytes,offsetField,(uint)data);
        Put(bytes,senseField,(uint)header);
        bytes[cdb]=(byte)(cdbLength==12?0xA1:0x85);bytes[cdb+1]=8;bytes[cdb+2]=0x0E; // PIO data-in, 512-byte blocks
        if(cdbLength==12)
        {
            bytes[cdb+3]=feature;bytes[cdb+4]=1;bytes[cdb+5]=1;
            if(command==0xB0){bytes[cdb+6]=0x4F;bytes[cdb+7]=0xC2;}
            bytes[cdb+8]=0xA0;bytes[cdb+9]=command;
        }
        else
        {
            bytes[cdb+4]=feature;bytes[cdb+6]=1;bytes[cdb+8]=1;
            if(command==0xB0){bytes[cdb+10]=0x4F;bytes[cdb+12]=0xC2;}
            bytes[cdb+13]=0xA0;bytes[cdb+14]=command;
        }
        return bytes;
    }
    static byte[]? DecodeSat(byte[]? bytes,int pointerSize)
    {
        if(pointerSize is not (4 or 8))return null;
        int header=pointerSize==8?56:44,offsetField=pointerSize==8?24:20;
        if(bytes is null||bytes.Length<header||BitConverter.ToUInt16(bytes,0)!=header||bytes[2]!=0||UInt(bytes,12)!=512)return null;
        ulong offset=pointerSize==8?BitConverter.ToUInt64(bytes,offsetField):UInt(bytes,offsetField);
        if(offset<(ulong)header+32||offset>(ulong)bytes.Length||512UL>(ulong)bytes.Length-offset)return null;
        return bytes[(int)offset..((int)offset+512)];
    }
    static byte[]? ReadSatSector(SafeFileHandle handle,byte command,byte feature,out string source,out string error)
    {
        var errors=new List<string>();
        foreach(int length in new[]{12,16})
        {
            var query=SatQuery(command,feature,length,IntPtr.Size);
            var response=Query(handle,IoctlScsiPassThrough,query,query.Length,out var failure);
            var sector=DecodeSat(response,IntPtr.Size);
            if(sector is not null&&(command==0xEC?DecodeAtaIdentify(sector)is not null:ValidAtaSmartSector(sector)))
            {source="Sat"+length;error="None";return sector;}
            errors.Add("SAT"+length+":"+(response is null?failure:"InvalidResponse"));
        }
        source="None";error=string.Join("/",errors);return null;
    }

    sealed record SsdProfile(string Name,byte[] LifeIds,bool Raw,bool Used);
    static SsdProfile? SelectSsdProfile(string model,string media)
    {
        if(media=="HDD")return null;
        string name=model.Trim().ToUpperInvariant();
        bool sk=name.StartsWith("SK HYNIX ")||name.StartsWith("HFS")||name.StartsWith("SHG");
        if(sk&&Regex.IsMatch(name,@"(?:^|[ _])SC(?:311|401)(?:$|[ _])"))return new("SKhynixSC311SC401",[0xE9],true,false);
        if(name.StartsWith("HFS")&&(name.Contains("TND")||name.Contains("MND")))return new("SKhynixSL300SC210",[0xE7,0xE9],true,true);
        if(name.StartsWith("HFS")&&name.Contains("TNF"))return new("SKhynixHfsTnf",[0xE7,0xE9],true,false);
        if(Regex.IsMatch(name,@"^SAMSUNG SSD (?:470|750|830|840|850|860|870)(?: |$)"))return new("SamsungSataConsumerB1",[0xB1],false,false);
        if(Regex.IsMatch(name,@"^(?:CRUCIAL[ _])?CT[0-9]+(?:MX(?:100|200|300|500)|BX(?:100|200|300|500))SSD[0-9](?: |$)")||Regex.IsMatch(name,@"^MICRON[ _](?:1100|1300)[ _]"))
            return new("MicronCrucialSataCA",[0xCA],false,false);
        if(Regex.IsMatch(name,@"^INTEL SSD(?:SC2(?:BW|BP|CT|CW)|SA2CW)"))return new("IntelSataE9",[0xE9],false,false);
        return null;
    }
    static AtaSensors DecodeCompatibleAtaSensors(string model,string media,IReadOnlyList<StorageSmartAttribute> attributes)
    {
        var profile=SelectSsdProfile(model,media);
        bool hdd=media=="HDD"&&!model.StartsWith("SAMSUNG SV",StringComparison.OrdinalIgnoreCase);
        if(profile is null&&!hdd)return new("None",null,"None",null,null,null);
        double? temperature=null;string temperatureId="None";
        foreach(byte id in new byte[]{0xC2,0xBE})
        {
            var attribute=attributes.FirstOrDefault(a=>a.Id==id);
            if(attribute is null||attribute.Raw>=(1UL<<48)||(uint)attribute.Raw==uint.MaxValue)continue;
            int raw=(byte)attribute.Raw;
            if(raw is >0 and <100){temperature=raw;temperatureId=id.ToString("X2");break;}
        }
        // 09 raw-hour mapping is only enabled for confirmed SC311/SC401 here.
        // Other controllers can encode minutes or half-minutes under the same ID.
        var hoursAttribute=profile?.Name=="SKhynixSC311SC401"?attributes.FirstOrDefault(a=>a.Id==9):null;
        ulong? hours=hoursAttribute is not null&&(uint)hoursAttribute.Raw!=uint.MaxValue?(uint)hoursAttribute.Raw:null;
        if(profile is null)return new("RotatingAtaConventionalTemperature",temperature,temperatureId,null,null,null);
        var life=attributes.Where(a=>profile.LifeIds.Contains(a.Id)).ToArray();
        // CrystalDiskInfo treats raw E9=0 as unavailable for SK hynix SC311,
        // not as evidence of a failed drive. Only E9 raw 1..100 is accepted.
        bool sc311=IsSc311Family(profile.Name);
        bool invalid=life.Any(a=>a.Raw>=(1UL<<48)||(profile.Raw?(byte)a.Raw:a.Current)>100||
            (sc311&&(byte)a.Raw==0));
        var values=life.Select(a=>profile.Raw?(int)(byte)a.Raw:a.Current).Select(v=>profile.Used?100-v:v).Distinct().ToArray();
        bool conflict=values.Length>1;
        int? remaining=!invalid&&!conflict&&values.Length==1?values[0]:null;
        // These SC311-specific mappings must not leak to unknown SSD families.
        var cycles=sc311?attributes.FirstOrDefault(a=>a.Id==0x0C):null;
        ulong? powerCycles=cycles is not null&&cycles.Raw<=uint.MaxValue?cycles.Raw:null;
        static ulong? SectorsToBytes(StorageSmartAttribute? a) =>
            a is not null && a.Raw < (1UL<<48) ? a.Raw*512UL : null;
        ulong? writes=sc311?SectorsToBytes(attributes.FirstOrDefault(a=>a.Id==0xF1)):null;
        ulong? reads=sc311?SectorsToBytes(attributes.FirstOrDefault(a=>a.Id==0xF2)):null;
        return new(profile.Name,temperature,temperatureId,remaining.HasValue?100-remaining:null,remaining,hours,conflict,invalid,
            powerCycles,reads,writes);
    }

    internal static (string Profile, double? RemainingLifePercent, bool LifeInvalid, bool LifeConflict) LibCdiDecodedSensors(string model, string media, IReadOnlyList<StorageSmartAttribute> attributes)
    {
        var decoded = DecodeCompatibleAtaSensors(model, media, attributes);
        return (decoded.Profile, decoded.RemainingLifePercent, decoded.LifeInvalid, decoded.LifeConflict);
    }

    static void SmartCompatibilitySelfTest()
    {
        static void Check(bool v,string s){if(!v)throw new Exception("SMART compatibility: "+s);}
        foreach(int pointer in new[]{4,8})foreach(int length in new[]{12,16})
        {
            var query=SatQuery(0xB0,0xD0,length,pointer);int header=pointer==8?56:44,cdb=pointer==8?36:28;
            Check(query[cdb]==(length==12?0xA1:0x85)&&query[cdb+2]==0x0E&&UInt(query,12)==512,"SAT CDB/data-in layout");
            Check(query[cdb+(length==12?3:4)]==0xD0&&query[cdb+(length==12?9:14)]==0xB0,"SAT read whitelist");
            query[header+32]=0x42;Check(DecodeSat(query,pointer)?[0]==0x42,"SAT payload offset");
            query[2]=2;Check(DecodeSat(query,pointer)is null,"SCSI check-condition is not valid data");query[2]=0;
            Put(query,12,511);Check(DecodeSat(query,pointer)is null,"SAT short transfer rejected");
            bool rejected=false;try{SatQuery(0xB0,0xD8,length,pointer);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"SMART enable cannot be sent");
        }
        Check(AtaModelMatches("Samsung SSD 870 EVO 1TB USB Device","Samsung SSD 870 EVO 1TB")&&!AtaModelMatches("Other disk","Samsung SSD 870 EVO 1TB"),"USB identity binding");
        var samsung=DecodeCompatibleAtaSensors("Samsung SSD 870 EVO 1TB","SSD",[new(0xB1,72,72,0,1234,false),new(0xC2,100,100,0,35,false)]);
        Check(samsung.RemainingLifePercent==72&&samsung.TemperatureC==35,"Samsung normalized B1, not raw erase count");
        Check(DecodeCompatibleAtaSensors("CT500MX500SSD1","SSD",[new(0xCA,86,86,0,14,false)]).RemainingLifePercent==86,"Crucial CA normalized remaining life");
        Check(DecodeCompatibleAtaSensors("Crucial_CT512MX100SSD1","SSD",[new(0xCA,86,86,0,14,false)]).RemainingLifePercent==86,"Crucial IDENTIFY vendor prefix");
        Check(DecodeCompatibleAtaSensors("CT500MX999SSD1","SSD",[new(0xCA,100,100,0,0,false)]).RemainingLifePercent is null,"unreviewed Crucial families stay unsupported");
        Check(DecodeCompatibleAtaSensors("INTEL SSDSC2BW240A4","SSD",[new(0xE9,93,93,0,123,false)]).RemainingLifePercent==93,"Intel E9 normalized life");
        Check(DecodeCompatibleAtaSensors("Unknown SSD","SSD",[new(0xE9,100,100,0,100,false)]).RemainingLifePercent is null,"unknown family never inherits E9 meaning");
        Check(DecodeCompatibleAtaSensors("Samsung SSD 870 EVO 1TB","SSD",[new(0xB1,255,255,0,1,false)]).LifeInvalid,"invalid normalized life rejected");
        Check(DecodeCompatibleAtaSensors("CT500MX500SSD1","HDD",[new(0xCA,86,86,0,14,false)]).RemainingLifePercent is null,"contradictory HDD media forbids SSD profile");
        Check(DecodeCompatibleAtaSensors("SK hynix SC311 SATA 256GB","SSD",[new(0xE9,2,1,null,99,false),new(0xB1,99,1,null,20,false)]) is { RemainingLifePercent: 99, LifeConflict: false },"SC311 E9 raw beats misleading normalized E9 and unrelated B1");
        Check(DecodeCompatibleAtaSensors("SK hynix SC311 SATA 256GB","SSD",[new(0xE7,100,100,null,99,false)]).RemainingLifePercent is null,"SC311 requires E9, never assumes E7");
    }
}

// Safe adapter exposed to the optional libcdi backend for SC311 cross-check.
