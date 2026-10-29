using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatteryDiagnostic;
internal sealed record SmartctlAttribute(byte Id,byte Current,byte Worst,byte? Threshold,ulong Raw,bool Prefailure);
internal sealed record SmartctlSample
{
    public int DriveIndex {get;init;}
    public string Model {get;init;}="";
    public string Protocol {get;init;}="";
    public string MediaType {get;init;}="Unknown";
    public bool? Passed {get;init;}
    public bool HealthWarning {get;init;}
    public double? TemperatureC {get;init;}
    public double? RemainingLifePercent {get;init;}
    public int? WearPercent {get;init;}
    public ulong? PowerOnHours {get;init;}
    public ulong? PowerOnCount {get;init;}
    public ulong? HostReadsBytes {get;init;}
    public ulong? HostWritesBytes {get;init;}
    public byte? CriticalWarning {get;init;}
    public byte? AvailableSpare {get;init;}
    public byte? SpareThreshold {get;init;}
    public ulong? MediaErrors {get;init;}
    public string? ModelFamily {get;init;}
    public IReadOnlyList<SmartctlAttribute> Attributes {get;init;}=[];
    public IReadOnlyList<string> Diagnostics {get;init;}=[];
}
internal static class SmartctlParser
{
    internal static SmartctlSample? Parse(string json,int requestedDrive,int exitCode)
    {
        if(requestedDrive is <0 or >4095||exitCode is <0 or >255||(exitCode&3)!=0||
            string.IsNullOrWhiteSpace(json)||json.Length>2097152)return null;
        try
        {
            RejectDuplicateProperties(json);
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});
            var root=document.RootElement;
            var format=Get(root,"json_format_version");
            if(format.ValueKind!=JsonValueKind.Array||format.GetArrayLength()!=2||UInt(format[0])!=1||
                UInt(Get(Get(root,"smartctl"),"exit_status"))!=(ulong)exitCode)return null;
            string model=Text(Get(root,"model_name"))??"";
            var device=Get(root,"device");string name=Text(Get(device,"name"))??"";
            if(!DeviceMatches(name,requestedDrive)||model.Length is 0 or >256||model.Any(char.IsControl))return null;
            string protocol=Text(Get(device,"protocol"))??"";
            if(protocol is not ("ATA" or "NVMe" or "SCSI"))return null;
            bool nvme=protocol=="NVMe";
            ulong? rotation=UInt(Get(root,"rotation_rate"));
            string media=nvme||rotation==0?"SSD":rotation>0?"HDD":"Unknown";
            var notes=new List<string>();
            var log=Get(root,"nvme_smart_health_information_log");
            // ATA endurance_used may be a guess from an attribute name and can
            // clamp an invalid normalized 255 to zero used. A database match
            // does not validate it. Only native ACS device statistics qualify.
            ulong? used=protocol=="ATA"?AtaEndurance(root):UInt(Get(Get(root,"endurance_used"),"current_percent"));
            ulong? nvmeUsed=UInt(Get(log,"percentage_used"));
            bool invalidLife=used>255||nvmeUsed>255;
            bool conflict=nvme&&used.HasValue&&nvmeUsed.HasValue&&used!=nvmeUsed;
            if(invalidLife)notes.Add("ata-life:InvalidSmartctlEndurance");
            if(conflict)notes.Add("ata-life:ConflictingSmartctlValues");
            used=nvme?nvmeUsed??used:used;
            bool decoded=nvme||protocol=="SCSI"||used.HasValue;
            int? wear=!invalidLife&&!conflict&&media=="SSD"&&decoded&&used.HasValue?(int)used.Value:null;
            var attributes=new List<SmartctlAttribute>();var ids=new HashSet<byte>();
            var table=Get(Get(root,"ata_smart_attributes"),"table");
            if(table.ValueKind==JsonValueKind.Array)
            {
                if(table.GetArrayLength()>64)return null;
                foreach(var row in table.EnumerateArray())
                {
                    byte? id=Byte(Get(row,"id"));
                    if(id is null or 0 or 255)continue;
                    if(!ids.Add(id.Value))return null;
                    byte? current=Byte(Get(row,"value")),worst=Byte(Get(row,"worst"));
                    ulong? raw=UInt(Get(Get(row,"raw"),"value"));
                    if(current is null||worst is null||raw is null||raw>=(1UL<<48))continue;
                    attributes.Add(new(id.Value,current.Value,worst.Value,Byte(Get(row,"thresh")),raw.Value,
                        Bool(Get(Get(row,"flags"),"prefailure"))==true));
                }
            }
            bool? passed=Bool(Get(Get(root,"smart_status"),"passed"));
            if((exitCode&8)!=0)passed=false;
            else if((exitCode&4)!=0&&passed==true)passed=null;
            if((exitCode&4)!=0)notes.Add("smartctl:PartialCommandFailure");
            double? temperature=Number(Get(Get(root,"temperature"),"current"))??Number(Get(log,"temperature"));
            if(temperature is <-40 or >150)temperature=null;
            return new()
            {
                DriveIndex=requestedDrive,Model=model,Protocol=protocol,MediaType=media,Passed=passed,
                HealthWarning=(exitCode&0xf0)!=0,TemperatureC=temperature,WearPercent=wear,
                RemainingLifePercent=wear.HasValue?Math.Max(0,100-wear.Value):null,
                PowerOnHours=UInt(Get(Get(root,"power_on_time"),"hours"))??UInt(Get(log,"power_on_hours")),
                PowerOnCount=UInt(Get(root,"power_cycle_count"))??UInt(Get(log,"power_cycles")),
                HostReadsBytes=nvme?Bytes(UInt(Get(log,"data_units_read"))):null,
                HostWritesBytes=nvme?Bytes(UInt(Get(log,"data_units_written"))):null,
                CriticalWarning=nvme?Byte(Get(log,"critical_warning")):null,
                AvailableSpare=nvme?Percent(Get(log,"available_spare")):null,
                SpareThreshold=nvme?Percent(Get(log,"available_spare_threshold")):null,
                MediaErrors=nvme?UInt(Get(log,"media_errors")):null,
                ModelFamily=decoded?Text(Get(root,"model_family")):null,Attributes=attributes,Diagnostics=notes
            };
        }
        catch(Exception e) when(e is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {return null;}
    }
    static JsonElement Get(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var value)?value:default;
    static string? Text(JsonElement e)=>e.ValueKind==JsonValueKind.String?e.GetString():null;
    static bool? Bool(JsonElement e)=>e.ValueKind==JsonValueKind.True?true:e.ValueKind==JsonValueKind.False?false:null;
    static ulong? UInt(JsonElement e)=>e.ValueKind==JsonValueKind.Number&&e.TryGetUInt64(out var n)?n:null;
    static byte? Byte(JsonElement e)=>UInt(e) is {} n&&n<=255?(byte)n:null;
    static byte? Percent(JsonElement e)=>UInt(e) is {} n&&n<=100?(byte)n:null;
    static double? Number(JsonElement e)=>e.ValueKind==JsonValueKind.Number&&e.TryGetDouble(out var n)&&double.IsFinite(n)?n:null;
    static ulong? Bytes(ulong? units)=>units.HasValue&&units<=ulong.MaxValue/512000UL?units.Value*512000UL:null;
    static ulong? AtaEndurance(JsonElement root)
    {
        var pages=Get(Get(root,"ata_device_statistics"),"pages");
        if(pages.ValueKind!=JsonValueKind.Array||pages.GetArrayLength()>256)return null;
        var matches=new List<ulong?>();
        foreach(var page in pages.EnumerateArray())
        {
            if(UInt(Get(page,"number"))!=7)continue;
            var table=Get(page,"table");
            if(table.ValueKind!=JsonValueKind.Array||table.GetArrayLength()>64)continue;
            foreach(var item in table.EnumerateArray())
                if(UInt(Get(item,"offset"))==8&&Bool(Get(Get(item,"flags"),"valid"))==true)
                    matches.Add(UInt(Get(item,"value")));
        }
        return matches.Count==1?matches[0]:null;
    }
    static bool DeviceMatches(string name,int index)=>name.Equals("/dev/pd"+index.ToString(CultureInfo.InvariantCulture),StringComparison.OrdinalIgnoreCase)||
        name.Equals(@"\\.\PhysicalDrive"+index.ToString(CultureInfo.InvariantCulture),StringComparison.OrdinalIgnoreCase);
    internal static bool IdentityMatches(SmartctlSample sample,int index,string model)
    {
        static string Normalize(string text)=>Regex.Replace(text.Trim().ToUpperInvariant(),"[^A-Z0-9]","");
        string a=Normalize(model),b=Normalize(sample.Model);
        return sample.DriveIndex==index&&a.Length>=4&&b.Length>=4&&(a==b||a==b+"USBDEVICE"||a==b+"SCSIDISKDEVICE");
    }
    static void RejectDuplicateProperties(string json)
    {
        var reader=new Utf8JsonReader(Encoding.UTF8.GetBytes(json),new JsonReaderOptions{MaxDepth=32});
        var stack=new Stack<HashSet<string>>();
        while(reader.Read())
        {
            if(reader.TokenType==JsonTokenType.StartObject)stack.Push(new(StringComparer.Ordinal));
            else if(reader.TokenType==JsonTokenType.EndObject)stack.Pop();
            else if(reader.TokenType==JsonTokenType.PropertyName&&!stack.Peek().Add(reader.GetString()!))
                throw new JsonException("Duplicate property");
        }
    }
}
