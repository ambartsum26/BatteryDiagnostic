using System.Reflection;
namespace BatteryDiagnostic;
internal static class LayoutTests
{
    internal static void Run()
    {
        var failures=new List<string>();int count=0;
        void Check(bool value,string message){count++;if(!value)failures.Add(message);}
        object? Field(object obj,string name)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(obj);
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            using var view=new PerformanceDashboard{Size=new Size(1000,710)};
            var inv=new PerformanceInventory(new("Intel Core i3-8100",4,4,3600),new([new("DIMM1",8UL<<30,2400,2400,"SK hynix",null,null,MemoryType:"DDR4")],2),[],[]);
            var disk=new StorageDisk{DeviceId="layout",Model="SK hynix SC311 SATA 256GB",MediaType="SSD",RemainingLifePercent=99,TemperatureC=28,
                PowerOnCount=1959,PowerOnHours=7684,TotalBytes=256000000000,Volumes=[new("C:","NTFS",256000000000,120000000000,false)]};
            view.SetData(inv,new(DateTimeOffset.Now,29.5,3600,41,new(8UL<<30,2UL<<30,6UL<<30),[],[]),new(DateTimeOffset.Now,[disk],[]),lang);
            var smart=view.Controls.Cast<Control>().Single(c=>Equals(c.Tag,"smartDetails"));
            Check(smart is Button,"SMART is a button / "+lang);
            Check(smart.Right>=955&&smart.Right<=970&&smart.Top>=310&&smart.Bottom<=345,"SMART is in the top-right card corner / "+lang);
            typeof(PerformanceDashboard).GetField("benchmarkMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,68.312);
            var state=typeof(PerformanceDashboard).GetField("benchmarkState",BindingFlags.Instance|BindingFlags.NonPublic)!;
            state.SetValue(view,Enum.Parse(state.FieldType,"Complete"));
            using var image=new Bitmap(1000,710);view.DrawToBitmap(image,new Rectangle(0,0,1000,710));
            int Cyan(Rectangle rect)
            {
                int n=0;for(int y=rect.Top;y<rect.Bottom;y++)for(int x=rect.Left;x<rect.Right;x++)
                {var c=image.GetPixel(x,y);if(c.R<60&&c.G>180&&c.B>220)n++;}return n;
            }
            foreach(var (rect,name) in new[]{(new Rectangle(295,248,90,29),"CPU clock"),(new Rectangle(520,133,140,22),"RAM installed"),
                (new Rectangle(669,133,140,22),"RAM used"),(new Rectangle(654,501,145,24),"drive starts"),
                (new Rectangle(814,501,150,24),"drive hours"),(new Rectangle(540,438,70,42),"drive percentage")})
                Check(Cyan(rect)>10,"Unified metric color: "+name+" / "+lang);
            Check(Cyan(new Rectangle(345,112,140,28))>10,"CPU result directly beneath test button / "+lang);

            using var host=new Form{ClientSize=new Size(1000,652)};
            using var battery=new Dashboard{Dock=DockStyle.Fill};host.Controls.Add(battery);
            var reading=new Reading{Design=60000,Full=54000,Capacity=27000,State=1,BatteryModel=new string('W',85),Manufacturer=new string('M',80),Observation=new(12,40,[],12)};
            battery.SetData(new([reading],null,DateTimeOffset.Now,"Fixture"),reading,lang,"",true);
            host.Show();Application.DoEvents();battery.PerformLayout();
            var details=(FlowLayoutPanel)Field(battery,"details")!;
            Check(!details.AutoScroll&&!details.VerticalScroll.Visible&&!details.HorizontalScroll.Visible,"Battery metadata has no scrollbars / "+lang);
            foreach(string name in new[]{"batteryModel","manufacturer","detailTime"})
            {
                var label=(Label)Field(battery,name)!;
                Check(label.Visible&&label.Top>=0&&label.Bottom<=details.ClientSize.Height&&label.Right<=details.ClientSize.Width,"Battery metadata fully visible: "+name+" / "+lang);
            }
            host.Close();
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"layout-tests.txt"),failures.Count==0?$"PASS {count} layout/color checks in FR/EN/RU.":$"FAIL {failures.Count}/{count}: "+string.Join("; ",failures));
        if(failures.Count>0)throw new Exception("Layout: "+string.Join("; ",failures));
    }
}
