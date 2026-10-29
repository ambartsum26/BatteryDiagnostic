namespace BatteryDiagnostic;
internal sealed record MemoryInventory(IReadOnlyList<MemoryModule> Modules,int? TotalSlots);
internal sealed record MemoryModule(string? Slot,ulong? CapacityBytes,uint? ConfiguredSpeedMTs,
    uint? RatedSpeedMTs,string? Manufacturer,string? PartNumber,string? ChipManufacturer,
    uint? ConfiguredClockMHz=null,uint? RatedClockMHz=null,string? MemoryType=null);
internal static class MemoryCompatibility
{
    // DMTF DSP0134 Type 17 / Win32_PhysicalMemory.SMBIOSMemoryType.
    internal static string? TypeName(byte code)=>code switch
    {
        0x0f=>"SDRAM",0x11=>"RDRAM",0x12=>"DDR",0x13=>"DDR2",
        0x14=>"DDR2 FB-DIMM",0x18=>"DDR3",0x19=>"FBD2",0x1a=>"DDR4",
        0x1b=>"LPDDR",0x1c=>"LPDDR2",0x1d=>"LPDDR3",0x1e=>"LPDDR4",
        0x20=>"HBM",0x21=>"HBM2",0x22=>"DDR5",0x23=>"LPDDR5",0x24=>"HBM3",_=>null
    };
    internal static MemoryInventory Merge(MemoryInventory firmware,IReadOnlyList<MemoryModule> windows)
    {
        static string Slot(MemoryModule m)=>(m.Slot??"").Trim().ToUpperInvariant();
        if(firmware.Modules.Count==0)
        {
            // Only a complete Windows enumeration may replace missing firmware.
            if(windows.Count==0||windows.Any(m=>m.CapacityBytes is null or 0||Slot(m).Length==0)||
                windows.Select(Slot).Distinct().Count()!=windows.Count)return firmware;
            return new(windows.ToArray(),firmware.TotalSlots>=windows.Count?firmware.TotalSlots:null);
        }
        return firmware with {Modules=firmware.Modules.Select(module=>
        {
            string slot=Slot(module);
            if(slot.Length==0||module.CapacityBytes is null||firmware.Modules.Count(m=>Slot(m)==slot)!=1)return module;
            var matches=windows.Where(m=>Slot(m)==slot).ToArray();
            if(matches.Length!=1||matches[0].CapacityBytes!=module.CapacityBytes)return module;
            var extra=matches[0];
            string? type=module.MemoryType;
            if(type!=null&&extra.MemoryType!=null&&type!=extra.MemoryType)type=null;
            else type??=extra.MemoryType;
            return module with {MemoryType=type,Manufacturer=module.Manufacturer??extra.Manufacturer,
                PartNumber=module.PartNumber??extra.PartNumber};
        }).ToArray()};
    }
}
