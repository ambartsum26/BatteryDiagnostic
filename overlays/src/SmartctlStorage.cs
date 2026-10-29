using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BatteryDiagnostic;

// Independent read-only smartmontools process. Never installs smartd, starts
// tests, enables SMART, changes power/firmware settings, or runs a command shell.
internal static class SmartctlStorage
{
    internal const string BinarySha256="B5DB94E5082C042BE44994B7A4FA8F7B5C8E713B2AB1C9A560D8F7A7995EA27D";
    internal const string DatabaseSha256="428F9D56357CD35056719E1EF3FE2EFF87B5AE7499E7797E0811C38B2DBB7079";
    static readonly Dictionary<string,long> RetryAfter=new(StringComparer.Ordinal);
    internal static string[] Arguments(string database,int index)
    {
        if(index is <0 or >4095)throw new ArgumentOutOfRangeException(nameof(index));
        return ["--json=c","--info","--health","--attributes","--quietmode=noserial",
            "--drivedb="+Path.GetFullPath(database),"--","/dev/pd"+index.ToString(CultureInfo.InvariantCulture)];
    }
    static bool Verify(FileStream file,string expected)=>Convert.ToHexString(SHA256.HashData(file))==expected;
    internal static void Enrich(List<StorageDisk> disks,List<string> notes)
    {
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess)return;
        string directory=Path.Combine(AppContext.BaseDirectory,"smartmontools");
        string executable=Path.Combine(directory,"smartctl.exe"),database=Path.Combine(directory,"drivedb.h");
        if(!File.Exists(executable)||!File.Exists(database)){notes.Add("smartctl:NotPackaged");return;}
        if(!PerformanceCpuTemperature.IsAdministrator){notes.Add("smartctl:AdministratorRequired");return;}
        try
        {
            // Keep both verified files locked against replacement until probes finish.
            using var binary=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var data=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(!Verify(binary,BinarySha256)||!Verify(data,DatabaseSha256)){notes.Add("smartctl:HashMismatch");return;}
            notes.Add("smartctl:Version=7.5;drivedb=764fcfb2472920fcaeb62950e60cb8bf3f983065");
            var watch=Stopwatch.StartNew();int matched=0;
            for(int i=0;i<disks.Count;i++)
            {
                var disk=disks[i];
                if(disk.Index is not int index||index is <0 or >4095||string.IsNullOrWhiteSpace(disk.Model))continue;
                if(disk.TemperatureC.HasValue&&disk.PowerOnHours.HasValue&&disk.PowerOnCount.HasValue&&
                    (disk.MediaType=="HDD"||disk.RemainingLifePercent.HasValue)&&
                    (disk.SmartAttributes.Count>0||disk.NvmeCriticalWarning.HasValue))continue;
                string key=index+":"+disk.Model;
                if(RetryAfter.TryGetValue(key,out long retry)&&Environment.TickCount64<retry)continue;
                if(watch.ElapsedMilliseconds>=5500){notes.Add("smartctl:CycleBudgetReached");break;}
                var sample=Probe(executable,database,index,out string diagnostic);
                if(sample!=null&&SmartctlParser.IdentityMatches(sample,index,disk.Model))
                {disks[i]=SmartctlMerge.Apply(disk,sample);matched++;RetryAfter.Remove(key);}
                else
                {
                    RetryAfter[key]=Environment.TickCount64+60000;
                    disks[i]=disk with{Diagnostics=disk.Diagnostics.Append("smartctl:"+diagnostic).ToArray()};
                }
            }
            notes.Add("smartctl:Matched="+matched);
        }
        catch(Exception e) when(e is not OutOfMemoryException){notes.Add("smartctl:"+e.GetType().Name);}
    }
    static SmartctlSample? Probe(string executable,string database,int index,out string diagnostic)
    {
        diagnostic="NoMatchingData";
        using var process=new Process{StartInfo=new(executable){UseShellExecute=false,CreateNoWindow=true,
            WorkingDirectory=Path.GetDirectoryName(executable)!,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8}};
        foreach(string arg in Arguments(database,index))process.StartInfo.ArgumentList.Add(arg);
        using var deadline=new CancellationTokenSource(2500);
        try
        {
            if(!process.Start()){diagnostic="StartFailed";return null;}
            var output=ReadBounded(process.StandardOutput,2097152,deadline.Token);
            var error=ReadBounded(process.StandardError,16384,deadline.Token);
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            string json=output.GetAwaiter().GetResult();_=error.GetAwaiter().GetResult();
            var sample=SmartctlParser.Parse(json,index,process.ExitCode);
            diagnostic=sample is null?"RejectedOutputOrCommandFailure":"IdentityMismatch";
            return sample;
        }
        catch(Exception e) when(e is not OutOfMemoryException)
        {
            diagnostic=e is OperationCanceledException?"Timeout":e.GetType().Name;
            return null;
        }
        finally
        {
            try{if(!process.HasExited){process.Kill(true);process.WaitForExit(300);}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
    }
    static async Task<string> ReadBounded(StreamReader reader,int limit,CancellationToken token)
    {
        var value=new StringBuilder();char[] buffer=new char[4096];
        while(true)
        {
            int read=await reader.ReadAsync(buffer.AsMemory(),token);
            if(read==0)return value.ToString();
            if(value.Length+read>limit)throw new InvalidDataException("smartctl output exceeds limit");
            value.Append(buffer,0,read);
        }
    }
}
