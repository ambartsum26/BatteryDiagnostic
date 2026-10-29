namespace BatteryDiagnostic;

// Pure fixtures exercise the exact production parsers. No device access.
internal static class CompatibilityTests
{
    internal static void Run()
    {
        var failures=new List<string>();int count=0;
        void Test(string name,Func<bool> check)
        { count++;try {if(!check())failures.Add(name);}catch(Exception e){failures.Add(name+": "+e.Message);} }
        string Json(string fields,string model="Samsung SSD 870 EVO 1TB",string protocol="ATA",int index=0,int status=0)=>
            $$"""{"json_format_version":[1,0],"smartctl":{"exit_status":{{status}}},"device":{"name":"/dev/pd{{index}}","protocol":"{{protocol}}"},"model_name":"{{model}}",{{fields}}} """;
        const string ssd="\"rotation_rate\":0,\"in_smartctl_database\":true,\"smart_status\":{\"passed\":true},\"temperature\":{\"current\":36},\"endurance_used\":{\"current_percent\":17},\"power_on_time\":{\"hours\":8123},\"power_cycle_count\":240";
        var ata=SmartctlParser.Parse(Json(ssd),0,0);
        Test("ATA model-specific remaining life",()=>ata?.RemainingLifePercent==83);
        Test("ATA counters and temperature",()=>ata is {TemperatureC:36,PowerOnHours:8123,PowerOnCount:240,Passed:true});
        Test("Unknown ATA profile cannot guess endurance",()=>SmartctlParser.Parse(Json(ssd.Replace("\"in_smartctl_database\":true","\"in_smartctl_database\":false")),0,0)?.RemainingLifePercent is null);
        Test("HDD has no universal percentage",()=>SmartctlParser.Parse(Json(ssd.Replace("\"rotation_rate\":0","\"rotation_rate\":7200"),"WDC WD10EZEX"),0,0)?.RemainingLifePercent is null);
        Test("No SMART data means no invented percentage",()=>SmartctlParser.Parse(Json("\"rotation_rate\":0"),0,0)?.RemainingLifePercent is null);
        Test("Malformed JSON rejected",()=>SmartctlParser.Parse("{",0,0) is null);
        Test("Duplicate fields rejected",()=>SmartctlParser.Parse(Json(ssd+",\"temperature\":{\"current\":90}"),0,0) is null);
        Test("Open failure rejected",()=>SmartctlParser.Parse(Json(ssd,status:2),0,2) is null);
        Test("Process and JSON exit mismatch rejected",()=>SmartctlParser.Parse(Json(ssd),0,2) is null);
        Test("Partial command cannot assert healthy",()=>SmartctlParser.Parse(Json(ssd,status:4),0,4)?.Passed is null);
        Test("Failed health survives exit bit 3",()=>SmartctlParser.Parse(Json(ssd.Replace("\"passed\":true","\"passed\":false"),status:8),0,8)?.Passed==false);
        Test("Wrong physical device rejected",()=>SmartctlParser.Parse(Json(ssd,index:1),0,0) is null);
        Test("Invalid temperature discarded",()=>SmartctlParser.Parse(Json(ssd.Replace("\"current\":36","\"current\":999")),0,0)?.TemperatureC is null);
        Test("Negative counter discarded",()=>SmartctlParser.Parse(Json(ssd.Replace("\"hours\":8123","\"hours\":-1")),0,0)?.PowerOnHours is null);
        const string nvme="\"smart_status\":{\"passed\":true},\"nvme_smart_health_information_log\":{\"critical_warning\":1,\"percentage_used\":123,\"available_spare\":9,\"available_spare_threshold\":10,\"media_errors\":2,\"data_units_read\":4,\"data_units_written\":5}";
        var n=SmartctlParser.Parse(Json(nvme,"Samsung SSD 990 PRO","NVMe"),0,0);
        Test("NVMe above 100 used means zero remaining",()=>n is {RemainingLifePercent:0,WearPercent:123});
        Test("NVMe warning and reserve retained",()=>n is {CriticalWarning:1,AvailableSpare:9,SpareThreshold:10,MediaErrors:2});
        Test("NVMe units are 512000 bytes",()=>n is {HostReadsBytes:2048000,HostWritesBytes:2560000});
        Test("NVMe overflow rejected per counter",()=>SmartctlParser.Parse(Json(nvme.Replace("\"data_units_read\":4","\"data_units_read\":18446744073709551615"),"NVMe","NVMe"),0,0)?.HostReadsBytes is null);
        Test("NVMe conflicting endurance rejected",()=>SmartctlParser.Parse(Json(nvme+",\"endurance_used\":{\"current_percent\":20}","NVMe","NVMe"),0,0)?.Diagnostics.Contains("ata-life:ConflictingSmartctlValues")==true);
        Test("Physical index and model both required",()=>ata!=null&&SmartctlParser.IdentityMatches(ata,0,"Samsung SSD 870 EVO 1TB")&&!SmartctlParser.IdentityMatches(ata,1,ata.Model)&&!SmartctlParser.IdentityMatches(ata,0,"Other SSD"));
        Test("SCSI endurance uses protocol value",()=>SmartctlParser.Parse(Json(ssd.Replace("\"in_smartctl_database\":true,",""),"SAS SSD","SCSI"),0,0)?.RemainingLifePercent==83);
        const string attr="\"ata_smart_attributes\":{\"table\":[{\"id\":177,\"value\":83,\"worst\":80,\"thresh\":0,\"flags\":{\"prefailure\":true},\"raw\":{\"value\":600}}]}";
        Test("ATA attributes preserved without generic reinterpretation",()=>SmartctlParser.Parse(Json(ssd+","+attr),0,0)?.Attributes.Single() is {Id:177,Current:83,Raw:600,Prefailure:true});
        var disk=new StorageDisk{Index=0,Model="Samsung SSD 870 EVO 1TB",MediaType="SSD",HealthCode="Unknown"};
        Test("Fallback supplies missing real values",()=>ata!=null&&SmartctlMerge.Apply(disk,ata) is {RemainingLifePercent:83,PowerOnHours:8123,TemperatureC:36});
        Test("A different physical disk is untouched",()=>ata!=null&&SmartctlMerge.Apply(disk with{Index=1},ata).PowerOnHours is null);
        Test("Existing temperatures are retained",()=>ata!=null&&SmartctlMerge.Apply(disk with{TemperatureC=42},ata).TemperatureC==42);
        Test("Conflicting independent resource stays unknown",()=>ata!=null&&SmartctlMerge.Apply(disk with{RemainingLifePercent=50,RemainingLifeSource="NvmeHealthLog"},ata) is {RemainingLifePercent:null});
        Test("Existing failure is not erased",()=>ata!=null&&SmartctlMerge.Apply(disk with{HealthCode="Unhealthy",FailurePredicted=true},ata) is {HealthCode:"Unhealthy",FailurePredicted:true});
        Test("Fallback failure outranks healthy provider",()=>ata!=null&&SmartctlMerge.Apply(disk with{HealthCode="ProviderHealthy"},ata with{Passed=false}) is {HealthCode:"Unhealthy",FailurePredicted:true});
        Test("NVMe media errors survive a clear flag",()=>n!=null&&SmartctlMerge.Apply(disk with{Model=n.Model,HealthCode="ProviderHealthy"},n).NvmeMediaErrors==2);
        Test("Duplicate SMART IDs rejected",()=>SmartctlParser.Parse(Json(ssd+","+attr.Replace("}]}","},{\"id\":177,\"value\":20,\"worst\":20,\"raw\":{\"value\":0}}]}")),0,0) is null);
        Test("Intel library eligibility",()=>CpuSensorSupport.Supports("GenuineIntel",6,true));
        Test("Intel without DTS not promised",()=>!CpuSensorSupport.Supports("GenuineIntel",6,false));
        foreach(int family in new[]{0x0f,0x17,0x19,0x1a})Test("AMD supported family "+family,()=>CpuSensorSupport.Supports("AuthenticAMD",family,false));
        foreach(int family in new[]{0x10,0x11,0x12,0x14,0x15,0x16,0x1b})Test("AMD unavailable family "+family,()=>!CpuSensorSupport.Supports("AuthenticAMD",family,false));
        foreach(var (code,type) in new(byte,string)[]{(0x18,"DDR3"),(0x1a,"DDR4"),(0x22,"DDR5"),(0x1d,"LPDDR3"),(0x1e,"LPDDR4"),(0x23,"LPDDR5")})
            Test("Firmware RAM type "+type,()=>MemoryCompatibility.TypeName(code)==type);
        Test("Unknown RAM is not inferred from speed",()=>MemoryCompatibility.TypeName(255) is null);
        var m=new MemoryModule("DIMM_A1",8UL<<30,3200,3200,"Vendor","Part",null);
        Test("RAM type filled by exact slot and capacity",()=>MemoryCompatibility.Merge(new([m],2),[m with{MemoryType="DDR4"}]).Modules.Single().MemoryType=="DDR4");
        Test("RAM cannot bind another slot",()=>MemoryCompatibility.Merge(new([m],2),[m with{Slot="DIMM_B1",MemoryType="DDR5"}]).Modules.Single().MemoryType is null);
        Test("Duplicate WMI slots rejected",()=>MemoryCompatibility.Merge(new([m],2),[m with{MemoryType="DDR4"},m with{MemoryType="DDR5"}]).Modules.Single().MemoryType is null);
        Test("Firmware and WMI type conflict stays unknown",()=>MemoryCompatibility.Merge(new([m with{MemoryType="DDR3"}],2),[m with{MemoryType="DDR4"}]).Modules.Single().MemoryType is null);
        Test("Complete WMI fallback when firmware unavailable",()=>MemoryCompatibility.Merge(new([],null),[m with{MemoryType="DDR4"}]).Modules.Single().MemoryType=="DDR4");
        Test("Partial WMI capacity not used as complete fallback",()=>MemoryCompatibility.Merge(new([],null),[m,m with{Slot="DIMM_B1",CapacityBytes=null}]).Modules.Count==0);
        if(failures.Count>0)throw new Exception($"FAIL {failures.Count}/{count}: "+string.Join("; ",failures));
        Console.WriteLine($"PASS {count} compatibility fixtures: SMART/identity/counters, Intel/AMD eligibility, DDR3/4/5 and RAM binding. No hardware accessed.");
    }
}
