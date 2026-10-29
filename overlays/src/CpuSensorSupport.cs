namespace BatteryDiagnostic;
internal static class CpuSensorSupport
{
    // Mirrors CpuGroup in LibreHardwareMonitor v0.9.6. AMD 10h-16h
    // implementations are disabled upstream; do not offer their driver as a fix.
    internal static bool Supports(string vendor,int family,bool intelDts)=>
        vendor=="GenuineIntel" ? intelDts&&family>=6 :
        vendor=="AuthenticAMD"&&family is 0x0f or 0x17 or 0x19 or 0x1a;
}
