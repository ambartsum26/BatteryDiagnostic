using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace BatteryDiagnostic;
internal static class Native {
 [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid,IntPtr enumerator,IntPtr parent,uint flags);
 [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr dev,ref Guid guid,uint index,ref InterfaceData data);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr dev);
 [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle h,uint code,byte[] input,int size,byte[] output,int outputSize,out int returned,IntPtr overlapped);
 [StructLayout(LayoutKind.Sequential)] internal struct Power { public byte AC,Flags,Percent,Reserved; public uint Lifetime,FullLifetime; }
 [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool GetSystemPowerStatus(out Power power);
 static byte[] U(params uint[] values) {var b=new byte[values.Length*4];for(int i=0;i<values.Length;i++) BitConverter.GetBytes(values[i]).CopyTo(b,i*4);return b;}
 static byte[] Query(SafeFileHandle h,uint code,byte[] input,int size) {var b=new byte[size]; if(!DeviceIoControl(h,code,input,input.Length,b,size,out int n,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); if(n<size) throw new IOException("Short response: "+n+"/"+size);return b;}
 static uint V(byte[] b,int o)=>BitConverter.ToUInt32(b,o);
 static double? Valid(uint n)=>n==uint.MaxValue?null:n;
 internal static double? DecodeTemperature(uint tenthsKelvin)
 {
  if(tenthsKelvin==0||tenthsKelvin==uint.MaxValue)return null;
  double c=tenthsKelvin/10.0-273.15;
  return c is >= -40 and <= 125?c:null;
 }
 internal static string? DecodeUnicodeText(byte[] bytes,int count)
 {
  if(count<2||count>bytes.Length||(count&1)!=0)return null;
  string text=System.Text.Encoding.Unicode.GetString(bytes,0,count).TrimEnd('\0').Trim();
  return text.Length==0||text.Any(char.IsControl)?null:text;
 }
 static string? QueryBatteryText(SafeFileHandle h,uint tag,uint informationLevel)
 {
  var bytes=new byte[1024];
  if(!DeviceIoControl(h,0x294044,U(tag,informationLevel,0),12,bytes,bytes.Length,out int count,IntPtr.Zero))return null;
  return DecodeUnicodeText(bytes,count);
 }
 internal static List<Reading> Read() {
 var result=new List<Reading>(); var guid=new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
 var set=SetupDiGetClassDevs(ref guid,IntPtr.Zero,IntPtr.Zero,18);
 if(set==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
 try {for(uint i=0;i<100;i++) {
 var d=new InterfaceData{Size=Marshal.SizeOf<InterfaceData>()};
 if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref d)){int e=Marshal.GetLastWin32Error();if(e==259)break;throw new System.ComponentModel.Win32Exception(e);}
 SetupDiGetDeviceInterfaceDetail(set,ref d,IntPtr.Zero,0,out uint required,IntPtr.Zero);
 if(required<8||required>65536)throw new IOException("Invalid interface size");
 var p=Marshal.AllocHGlobal((int)required);
 try {Marshal.WriteInt32(p,IntPtr.Size==8?8:6);if(!SetupDiGetDeviceInterfaceDetail(set,ref d,p,required,out _,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
 var path=Marshal.PtrToStringUni(p+4)!;var r=new Reading{Slot=(int)i};result.Add(r);
 using var h=CreateFile(path,0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
 if(h.IsInvalid){r.Errors.Add("Open Win32="+Marshal.GetLastWin32Error());continue;}
 try {var tag=V(Query(h,0x294040,U(0),4),0);if(tag==0)throw new IOException("No battery tag");
 r.Identity=path+":"+tag;
 var info=Query(h,0x294044,U(tag,0,0),36);var caps=V(info,0);
 if((caps&0x80000000)==0||(caps&0x20000000)!=0){result.Remove(r);continue;}
 try {r.BatteryModel=QueryBatteryText(h,tag,4);r.Manufacturer=QueryBatteryText(h,tag,6);}catch { }
 r.Relative=(caps&0x40000000)!=0;r.Design=Valid(V(info,12));r.Full=Valid(V(info,16));uint cycles=V(info,32);r.Cycles=cycles==0||cycles==uint.MaxValue?null:cycles;
 try {var s=Query(h,0x29404c,U(tag,0,0,0,0),16);r.State=V(s,0);r.Capacity=Valid(V(s,4));r.Voltage=Valid(V(s,8));int rate=BitConverter.ToInt32(s,12);r.Rate=rate==int.MinValue?null:rate;} catch(Exception ex){r.Errors.Add("Status: "+ex.Message);}
 try {r.Temperature=DecodeTemperature(V(Query(h,0x294044,U(tag,2,0),4),0));}catch { }
 }catch(Exception ex){r.Errors.Add(ex.Message);}
 }finally{Marshal.FreeHGlobal(p);}
 }}finally{SetupDiDestroyDeviceInfoList(set);}return result;
 }
}
