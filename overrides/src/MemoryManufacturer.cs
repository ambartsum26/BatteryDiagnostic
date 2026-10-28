using System.Text.RegularExpressions;
namespace BatteryDiagnostic;

internal static class MemoryManufacturer
{
    // Firmware sometimes returns the two JEDEC bytes twice, separated by zeros
    // (for example 80AD000080AD). Decode only this exact repeated form or two bytes;
    // an arbitrary substring/part number is not evidence of a manufacturer.
    internal static string? Display(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return null;
        string code=raw.Trim().ToUpperInvariant();
        if(code.StartsWith("0X",StringComparison.Ordinal))code=code[2..];
        if(Regex.IsMatch(code,"^[0-9A-F]{12}$",RegexOptions.CultureInvariant))
        {
            if(code[4..8]!="0000"||code[..4]!=code[8..])return raw.Trim();
            code=code[..4];
        }
        // These are vendor-ID facts, not a part-number or chip-brand inference.
        return code switch {"80AD"=>"SK hynix","802C"=>"Micron Technology","80CE"=>"Samsung",_=>raw.Trim()};
    }
    internal static void SelfTest()
    {
        void Check(bool value,string name){if(!value)throw new Exception("Memory manufacturer: "+name);}
        Check(Display("80AD000080AD")=="SK hynix","actual screenshot JEDEC form");
        Check(Display("80CE000080CE")=="Samsung","Samsung repeated code");
        Check(Display("802c")=="Micron Technology","case normalization");
        Check(Display("80AD000080CE")=="80AD000080CE","conflicting codes are not guessed");
        Check(Display("Kingston")=="Kingston"&&Display(null)==null,"literal and unknown preserved");
        Check(Display("part80ADnumber")=="part80ADnumber","no substring inference");
    }
}
