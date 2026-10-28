using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BatteryDiagnostic;

internal sealed record ExternalCpuReading(double? TemperatureC, string Source, string Diagnostic, DateTimeOffset? Timestamp = null);

/// <summary>
/// Optional readers for sensor values already published by running AIDA64/HWiNFO.
/// No vendor DLL or private API is used. No programs, drivers, or settings are changed.
/// Both readers are off the UI thread. Data freshness and identity are checked.
/// </summary>
internal static class ExternalCpuTemperature
{
    const string AidaMapping = "AIDA64_SensorValues";
    const string HwiMapping = @"Global\HWiNFO_SENS_SM2";
    const string HwiMutex = @"Global\HWiNFO_SM2_MUTEX";
    const uint HwiSignature = 0x53695748;  // ASCII "HWiS" in little-endian
    const int MaxSharedBytes = 1024 * 1024;
    static readonly object Gate = new();
    static int running;
    static long nextPoll, lastSample;
    static ExternalCpuReading cached = Missing("Not sampled");
    internal const string SourceDescription = "Optional CPU temperature from AIDA64 shared memory and HWiNFO SM2 shared memory when the corresponding application is running and publishing data. No temperature is inferred from ACPI/board/socket data. Readings older than 10 seconds in this app, or stale HWiNFO publisher data, are rejected.";

    static ExternalCpuReading Missing(string why) => new(null,"None",why);
    internal static ExternalCpuReading ReadCached()
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (now >= nextPoll && Interlocked.CompareExchange(ref running, 1, 0) == 0)
            {
                nextPoll = now + 3000;
                _ = Task.Run(() =>
                {
                    ExternalCpuReading result;
                    try { result = ReadOnce(); }
                    catch (Exception e) { result = Missing("External reader: " + e.GetType().Name); }
                    lock (Gate) { cached = result; lastSample = Environment.TickCount64; }
                    Volatile.Write(ref running, 0);
                });
            }
            return now - lastSample is >= 0 and <= 10000 ? cached : Missing("External CPU temperature sample stale or starting");
        }
    }

    static ExternalCpuReading ReadOnce()
    {
        // AIDA64 is preferred when both external sources are publishing data.
        var aida = ReadAidaShared();
        if (aida.TemperatureC.HasValue) return aida;
        var hwinfo = ReadHwiShared();
        if (hwinfo.TemperatureC.HasValue) return hwinfo;
        return Missing("AIDA64: " + aida.Diagnostic + "; HWiNFO: " + hwinfo.Diagnostic);
    }

    static ExternalCpuReading ReadAidaShared()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(AidaMapping, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            int size = (int)Math.Min(view.Capacity, 65536);
            if (size < 16) return Missing("AIDA64 buffer too small");
            byte[] bytes = new byte[size];
            view.ReadArray(0, bytes, 0, size);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end <= 0) return Missing("AIDA64 shared memory empty");
            return ParseAida(Encoding.UTF8.GetString(bytes,0,end));
        }
        catch (FileNotFoundException) { return Missing("Shared memory not enabled or AIDA64 not running"); }
        catch (UnauthorizedAccessException) { return Missing("AIDA64 shared memory access denied"); }
        catch (Exception e) { return Missing("AIDA64 " + e.GetType().Name); }
    }

    // AIDA64 publishes an XML fragment: <temp><id>TCPUPKG</id><label>CPU Package</label><value>...</value></temp>.
    internal static ExternalCpuReading ParseAida(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 65535) return Missing("AIDA64 XML missing or too large");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 };
            using var sr = new StringReader("<root>" + xml + "</root>");
            using var reader = XmlReader.Create(sr,settings);
            var root = XDocument.Load(reader).Root;
            if(root is null) return Missing("AIDA64 XML empty");
            var samples = root.Elements().Where(e => e.Name.LocalName.Equals("temp",StringComparison.OrdinalIgnoreCase))
                .Select(e => (Id: (string?)e.Element("id") ?? "", Label: (string?)e.Element("label") ?? "", Value: ParseTemperature((string?)e.Element("value"))))
                .Where(x => x.Value.HasValue).ToArray();
            // Reject explicit evidence of multiple packages before selecting CPU #1.
            // Keep all valid package/core rows in this check, including unselected IDs.
            var packageIds = samples.Where(x => x.Id.StartsWith("TCPU", StringComparison.OrdinalIgnoreCase)
                && x.Id.EndsWith("PKG", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var corePackages = samples.Where(x => x.Id.StartsWith("TCC-", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id.Split('-')).Where(x => x.Length >= 3)
                .Select(x => x[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (packageIds.Length > 1 || corePackages.Length > 1 ||
                packageIds.Any(x => !x.Equals("TCPUPKG", StringComparison.OrdinalIgnoreCase)
                    && !x.Equals("TCPU1PKG", StringComparison.OrdinalIgnoreCase)) ||
                corePackages.Any(x => x != "1"))
                return Missing("AIDA64 ambiguous CPU packages");
            // Only explicit CPU sensors: never board, CPU socket, VRM, GPU, or ACPI zones.
            foreach (string id in new [] {"TCPUPKG", "TCPU1PKG", "TCPUIAC"})
            {
                var match = samples.Where(s=>s.Id.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
                if (match.Length == 1) return new(match[0].Value,"AIDA64 CPU Package", "AIDA64 "+id,DateTimeOffset.Now);
                if (match.Length > 1) return Missing("AIDA64 duplicate package sensor " + id);
            }
            // If Package is absent, the explicitly CPU-labelled AIDA64 sensor is a lower-priority fallback.
            foreach (string id in new [] {"TCPU", "TCPUDIO"})
            {
                var match = samples.Where(s=>s.Id.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
                if (match.Length == 1) return new(match[0].Value,"AIDA64 CPU", "AIDA64 "+id+" (not package)",DateTimeOffset.Now);
                if (match.Length > 1) return Missing("AIDA64 duplicate CPU sensor " + id);
            }
            var cores = samples.Where(s=>s.Id.StartsWith("TCC-1-",StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cores.Length > 0) return new(cores.Max(c=>c.Value!.Value),"AIDA64 Core Max","Hottest available CPU #1 core; package sensor missing",DateTimeOffset.Now);
            return Missing("AIDA64 CPU temperature sensor not published");
        }
        catch (XmlException) { return Missing("AIDA64 XML malformed"); }
        catch (Exception e) { return Missing("AIDA64 parser: " + e.GetType().Name); }
    }

    static ExternalCpuReading ReadHwiShared()
    {
        bool taken = false;
        Mutex? mutex = null;
        try
        {
            mutex = Mutex.OpenExisting(HwiMutex);
            try { taken = mutex.WaitOne(60); }
            catch (AbandonedMutexException) { taken = true; }
            if (!taken) return Missing("HWiNFO shared memory busy");
            using var mapping = MemoryMappedFile.OpenExisting(HwiMapping, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            int len = (int)Math.Min(view.Capacity, MaxSharedBytes);
            if (len < 48) return Missing("HWiNFO buffer too small");
            byte[] data = new byte[len];
            view.ReadArray(0,data,0,len);
            return ParseHwi(data,DateTimeOffset.UtcNow);
        }
        catch (WaitHandleCannotBeOpenedException) { return Missing("HWiNFO shared-memory mutex missing"); }
        catch (FileNotFoundException) { return Missing("Shared memory not enabled or HWiNFO not running"); }
        catch (UnauthorizedAccessException) { return Missing("HWiNFO shared-memory access denied"); }
        catch (Exception e) { return Missing("HWiNFO "+e.GetType().Name); }
        finally
        {
            if(taken)try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    static uint U32(ReadOnlySpan<byte> data,int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset,4));
    static long I64(ReadOnlySpan<byte> data,int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset,8));
    static double F64(ReadOnlySpan<byte> data,int offset) => BitConverter.Int64BitsToDouble(I64(data,offset));
    static string Ascii(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        if(end<0)end=data.Length;
        return Encoding.Latin1.GetString(data.Slice(0,end)).Trim();
    }
    static bool InBounds(int len,uint offset,uint size,uint count)
        => size > 0 && offset <= len && (ulong)size*count <= (ulong)len-offset;

    internal static ExternalCpuReading ParseHwi(byte[] data,DateTimeOffset now)
    {
        try
        {
            ReadOnlySpan<byte> s=data;
            if(s.Length < 48 || U32(s,0)!=HwiSignature) return Missing("HWiNFO inactive or signature mismatch");
            if(U32(s,4)!=1) return Missing("Unsupported HWiNFO header version");
            long tick=I64(s,12), age=now.ToUnixTimeSeconds()-tick;
            if(age is < -5 or > 30) return Missing("HWiNFO producer sample stale");
            uint sensorOffset=U32(s,20), sensorSize=U32(s,24), sensorCount=U32(s,28);
            uint readingOffset=U32(s,32), readingSize=U32(s,36), readingCount=U32(s,40);
            if(sensorCount is 0 or > 4096 || readingCount is 0 or > 16384 || sensorSize < 264 || readingSize < 316 ||
                !InBounds(s.Length,sensorOffset,sensorSize,sensorCount) || !InBounds(s.Length,readingOffset,readingSize,readingCount))
                return Missing("HWiNFO section bounds/layout invalid");
            var sensors=new string[(int)sensorCount];
            for(int i=0;i<sensors.Length;i++)
            {
                int o=checked((int)(sensorOffset+(ulong)i*sensorSize));
                sensors[i]=Ascii(s.Slice(o+8,128));
            }
            var candidates=new List<(int Rank,double Temp,string Sensor,string Label)>();
            for(int i=0;i<readingCount;i++)
            {
                int o=checked((int)(readingOffset+(ulong)i*readingSize));
                if(U32(s,o)!=1)continue; // temperature only
                uint sensorIndex=U32(s,o+4);
                if(sensorIndex>=sensors.Length)continue;
                string sensor=sensors[(int)sensorIndex];
                if(!IsCpuSensor(sensor))continue;
                string name=Ascii(s.Slice(o+12,128)); // original label, not renamed/user label
                string unit=Ascii(s.Slice(o+268,16));
                if(!unit.Equals("°C",StringComparison.OrdinalIgnoreCase) && !unit.Equals("C",StringComparison.OrdinalIgnoreCase))continue;
                double temp=F64(s,o+284);
                if(!ValidTemperature(temp))continue;
                int rank=SensorRank(name);
                if(rank<=0)continue;
                candidates.Add((rank,temp,sensor,name));
            }
            if(candidates.Count==0)return Missing("HWiNFO matching CPU temperature not published");
            foreach(int rank in new[]{3,2,1})
            {
                var matches=candidates.Where(c=>c.Rank==rank).ToArray();
                if(matches.Length==0)continue;
                if(rank==1)
                    return new(matches.Max(c=>c.Temp),"HWiNFO Core Max","HWiNFO hottest published core (not Package)",now);
                // Never pick arbitrarily between multiple CPU packages or sensors.
                if(matches.Length!=1)return Missing("HWiNFO ambiguous CPU package readings");
                return new(matches[0].Temp,rank==3?"HWiNFO CPU Package":"HWiNFO CPU Die","HWiNFO "+matches[0].Label,now);
            }
            return Missing("HWiNFO no suitable CPU sensor");
        }
        catch (Exception e) { return Missing("HWiNFO parser: " + e.GetType().Name); }
    }
    static bool IsCpuSensor(string name)
    {
        string n=name.ToLowerInvariant();
        return (n.Contains("cpu") || n.Contains("intel core") || n.Contains("amd ryzen") || n.Contains("amd athlon"))
            && !n.Contains("gpu") && !n.Contains("graphics") && !n.Contains("motherboard") && !n.Contains("mainboard") && !n.Contains("super i/o");
    }
    static int SensorRank(string label)
    {
        string n=label.ToLowerInvariant().Trim();
        if(n is "cpu package" or "package temperature") return 3;
        if(n is "cpu (tctl/tdie)" or "cpu (tdie)" or "cpu die" or "cpu die (average)")return 2;
        if(n.StartsWith("core ",StringComparison.Ordinal) && n.Length<35 && !n.Contains("distance") ||
           n.StartsWith("cpu core #",StringComparison.Ordinal) && !n.Contains("distance"))return 1;
        return 0;
    }
    static bool ValidTemperature(double? x) => x.HasValue&&double.IsFinite(x.Value)&&x is >=-40 and <=150;
    static double? ParseTemperature(string? text)
        => double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&ValidTemperature(number)?number:null;

    internal static void SelfTest()
    {
        static void Check(bool pass,string why){if(!pass)throw new Exception("External CPU temperature: "+why);}
        string Sample(string id,string value) => "<temp><id>"+id+"</id><label>CPU</label><value>"+value+"</value></temp>";
        Check(ParseAida(Sample("TCPUPKG","53.25")).TemperatureC==53.25,"AIDA64 package reading");
        Check(ParseAida(Sample("TMOBO","78")).TemperatureC==null,"motherboard must not substitute CPU");
        Check(ParseAida(Sample("TCPUPKG","NaN")).TemperatureC==null,"AIDA64 NaN rejected");
        Check(ParseAida(Sample("TCPUPKG","165")).TemperatureC==null,"AIDA64 implausible reading rejected");
        Check(ParseAida(Sample("TCPUPKG","51")+Sample("TCPU","42")).TemperatureC==51,"AIDA64 package outranks generic CPU");
        Check(ParseAida(Sample("TCPUPKG","53")+Sample("TCPUPKG","54")).TemperatureC==null,"AIDA64 duplicate packages rejected");
        Check(ParseAida("<!DOCTYPE foo [<!ENTITY x SYSTEM 'file:///C:/a'>]><temp><id>TCPU</id><value>&x;</value></temp>").TemperatureC==null,"AIDA64 DTD prohibited");
        Check(ParseAida(Sample("TCC-1-1","46")+Sample("TCC-1-2","56")).TemperatureC==56,"AIDA64 max core fallback");
        var now=DateTimeOffset.UtcNow;
        byte[] buffer=FakeHwi(now,"CPU [#0]: Intel Core i3-8100","CPU Package",49);
        Check(ParseHwi(buffer,now).TemperatureC==49,"HWiNFO CPU package");
        Check(ParseHwi(FakeHwi(now,"GPU [#0]: NVIDIA","CPU Package",49),now).TemperatureC==null,"GPU must not substitute CPU");
        Check(ParseHwi(FakeHwi(now,"CPU [#0]: Intel Core","CPU Socket",55),now).TemperatureC==null,"socket not package");
        Check(ParseHwi(buffer,now.AddMinutes(2)).TemperatureC==null,"stale HWiNFO producer rejected");
        buffer[0]=0;
        Check(ParseHwi(buffer,now).TemperatureC==null,"bad signature rejected");
    }
    static byte[] FakeHwi(DateTimeOffset now,string name,string label,double temperature)
    {
        byte[] data=new byte[48+264+316];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0,4),HwiSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4,4),1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(12,8),now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20,4),48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24,4),264);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28,4),1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32,4),312);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36,4),316);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40,4),1);
        Encoding.ASCII.GetBytes(name).CopyTo(data,48+8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(312,4),1);
        Encoding.ASCII.GetBytes(label).CopyTo(data,312+12);
        Encoding.UTF8.GetBytes("C").CopyTo(data,312+268);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(312+284,8),BitConverter.DoubleToInt64Bits(temperature));
        return data;
    }
}
