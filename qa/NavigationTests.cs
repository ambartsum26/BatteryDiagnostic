using System.Reflection;
namespace BatteryDiagnostic;

// Hovering or clicking a readout must never open an automatic help balloon.
internal static class NavigationTests
{
    internal static void Run()
    {
        var failures=new List<string>();int count=0;
        void Check(bool value,string name){count++;if(!value)failures.Add(name);}
        object? Field(object obj,string name)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(obj);
        bool NoTips(object obj)=>!obj.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic)
            .Where(f=>typeof(ToolTip).IsAssignableFrom(f.FieldType)).Any(f=>f.GetValue(obj) is ToolTip);
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            using var view=new PerformanceDashboard{Size=new Size(1000,710)};
            var disk=new StorageDisk{DeviceId="fixture-0",Index=0,Model="Navigation disk A",MediaType="SSD",TemperatureC=43,RemainingLifePercent=87,
                PowerOnHours=456,PowerOnCount=123,Diagnostics=[new string('X',3000)]};
            var snapshot=new StorageSnapshot(DateTimeOffset.Now,[disk],[]);
            var inventory=new PerformanceInventory(new("Navigation CPU",8,16,4800),new([new("DIMM",16UL<<30,3200,3200,"Vendor",null,null)],2),
                [new("gpu","Navigation GPU",GpuKind.Dedicated,8UL<<30,null,null)],[]);
            view.SetData(inventory,new(DateTimeOffset.Now,20,3000,55,new(16UL<<30,8UL<<30,8UL<<30),[],[]),snapshot,lang);
            Check(NoTips(view),"No tooltip provider on performance readouts, lists or actions / "+lang);
            Check(view.AccessibleDescription?.Contains(disk.Model)==true,"Disk metadata retained for accessibility / "+lang);
            int detailsActions=0;view.StorageDetailsRequested+=(_,_)=>detailsActions++;
            var smartLink=view.Controls.OfType<Button>().Single(c=>Equals(c.Tag,"smartDetails"));
            typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(smartLink,[EventArgs.Empty]);
            Check(detailsActions==1,"SMART remains an explicit action / "+lang);
            using var smart=new StorageDetailsForm(()=>snapshot,disk.DeviceId,lang);
            smart.Render();
            Check(Field(smart,"table") is DataGridView{ShowCellToolTips:false},"No native SMART cell balloons / "+lang);
            Check(smart.ReadingRows>0,"SMART values retained / "+lang);

            using var battery=new Dashboard();
            var reading=new Reading{Design=60000,Full=54000,Capacity=27000,State=7,Observation=new(12,40,[],12)};
            battery.SetData(new([reading],null,DateTimeOffset.Now,"Synthetic"),reading,lang,"",true);
            Check(NoTips(battery),"No battery hover reports / "+lang);
            Check(Field(battery,"details") is TableLayoutPanel{AutoScroll:false},"Battery metadata has no scrolling / "+lang);
            Check(Field(battery,"batteryModel") is Label{Visible:true},"Battery model always visible / "+lang);
            Check(Field(battery,"manufacturer") is Label{Visible:true},"Battery manufacturer always visible / "+lang);
            Check(Field(battery,"detailTime") is Label{Visible:true},"Battery observation always visible / "+lang);
            Check(!(Field(battery,"notice") as Label)?.Text.Contains(lang switch{UiLanguage.Russian=>"наведите",UiLanguage.French=>"survoler",_=>"hover"})??true,"No obsolete hover instructions / "+lang);
            using var indicator=new IndicatorWidget(true);
            indicator.SetValue(80,"Fixture",new string('X',1000));
            Check(NoTips(indicator),"No decorative indicator popup / "+lang);
            Check(indicator.AccessibleDescription?.Length==1000,"Screen-reader detail retained / "+lang);
            using var form=new MainForm();
            Check(NoTips(form),"No window-button tooltips / "+lang);
            Check(!form.LanguageMenu.ShowItemToolTips,"No language-menu balloons / "+lang);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"navigation-tests.txt"),failures.Count==0?
            $"PASS {count} navigation checks: no tooltip providers, no SMART balloons, visible battery metadata, explicit SMART, FR/EN/RU.":
            $"FAIL {failures.Count}/{count}: "+string.Join("; ",failures));
        if(failures.Count>0)throw new Exception($"Navigation: {failures.Count}/{count}: "+string.Join("; ",failures));
    }
}
