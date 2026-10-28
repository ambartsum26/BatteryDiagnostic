using Microsoft.Win32;
namespace BatteryDiagnostic;

internal sealed record BatteryData(List<Reading> Batteries, Native.Power? Power, DateTimeOffset Time, string Machine);
internal interface IBatterySource { Task<BatteryData> ReadAsync(); }
internal sealed class WindowsBatterySource : IBatterySource
{
    string? machine;
    static string CleanIdentity(object? value)
    {
        string text=value?.ToString()?.Trim()??"";
        if(text.Length==0||text.Any(char.IsControl))return "";
        string normalized=text.Trim().ToLowerInvariant();
        if(normalized is "to be filled by o.e.m." or "to be filled by oem" or "default string" or "system product name" or "not specified" or "unknown" or "none")return "";
        return text;
    }
    static string ReadMachine()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string manufacturer=CleanIdentity(key?.GetValue("SystemManufacturer"));
            string product=CleanIdentity(key?.GetValue("SystemProductName"));
            return manufacturer.Length==0?product:product.Length==0?manufacturer:manufacturer+" "+product;
        }
        catch { return ""; }
    }
    public Task<BatteryData> ReadAsync() => Task.Run(() =>
    {
        Native.Power? power = Native.GetSystemPowerStatus(out var p) ? p : null;
        machine ??= ReadMachine();
        var readings = Native.Read();
        return new BatteryData(readings, power, DateTimeOffset.Now, machine);
    });
}
