using System.Reflection;
namespace BatteryDiagnostic;
internal static class AuditVisualTests
{
    sealed class FixtureSource : IBatterySource
    {
        internal BatteryData Data=new([],null,DateTimeOffset.Now,"Synthetic fixture");
        internal bool Fail;
        public Task<BatteryData> ReadAsync()=>Fail?Task.FromException<BatteryData>(new IOException("Synthetic read failure")):Task.FromResult(Data);
    }
    internal static void Run()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"final-audit-preview");Directory.CreateDirectory(output);
        var report=new List<string>();var failures=new List<string>();
        void Check(bool valid,string reason){if(!valid)failures.Add(reason);}
        double Luminance(Color c)
        {double F(byte x){double n=x/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}return .2126*F(c.R)+.7152*F(c.G)+.0722*F(c.B);}
        double Contrast(Color a,Color b){var x=Luminance(a);var y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
        foreach(var (name,color) in new[]{("text",Theme.Text),("muted",Theme.Muted),("accent",Theme.Accent),("warning",Theme.Warning),("danger",Theme.Danger)})
        {double value=Contrast(color,Theme.Card);Check(value>=4.5,"text contrast "+name);report.Add("Text contrast on cards: "+name+" = "+value.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));}

        var source=new FixtureSource();
        using var form=new MainForm(source);
        form.Show();Application.DoEvents();
        IEnumerable<Control> All(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(All(x)));
        var maximize=All(form).OfType<Button>().Single(b=>Equals(b.Tag,"maximizeRestore"));
        var click=typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Rectangle normal=form.Bounds;
        click.Invoke(maximize,[EventArgs.Empty]);Application.DoEvents();
        Check(form.WindowState==FormWindowState.Maximized,"visible maximize state");
        Check(form.Bounds==Screen.FromControl(form).WorkingArea,"maximize uses actual monitor working area");
        click.Invoke(maximize,[EventArgs.Empty]);Application.DoEvents();
        Check(form.WindowState==FormWindowState.Normal&&form.Bounds==normal,"restore actual normal bounds");
        var brand=All(form).OfType<Label>().Single(l=>l.Text=="DevicePulse");
        var doubleClick=typeof(Control).GetMethod("OnMouseDoubleClick",BindingFlags.Instance|BindingFlags.NonPublic)!;
        doubleClick.Invoke(brand,[new MouseEventArgs(MouseButtons.Left,2,10,10,0)]);Application.DoEvents();
        Check(form.WindowState==FormWindowState.Maximized,"caption double-click maximizes");
        doubleClick.Invoke(brand,[new MouseEventArgs(MouseButtons.Left,2,10,10,0)]);Application.DoEvents();
        Check(form.WindowState==FormWindowState.Normal,"caption double-click restores");

        var initial=new Reading{Slot=0,Identity="fixture-0",Design=60000,Full=54000,Capacity=27000,State=1};
        source.Data=new([initial,new Reading{Slot=1,Identity="fixture-1",Design=50000,Full=40000,Capacity=20000,State=1}],null,DateTimeOffset.Now,"Synthetic");
        form.SelectPage(false);form.RefreshOnce().GetAwaiter().GetResult();
        Check(form.CurrentData?.Batteries.Count==2,"two battery inventory");
        var batterySelector=(ComboBox)typeof(MainForm).GetField("battery",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        batterySelector.SelectedIndex=1;
        source.Data=new([initial],null,DateTimeOffset.Now,"Synthetic");form.RefreshOnce().GetAwaiter().GetResult();
        Check(batterySelector.SelectedIndex==0&&form.CurrentData?.Batteries.Count==1,"disappearing selected battery handled");
        source.Fail=true;form.RefreshOnce().GetAwaiter().GetResult();
        Check(form.CurrentData==null&&form.View.CurrentHealth==L.Get(form.Language,"unknown"),"read error clears prior health");
        source.Fail=false;form.RefreshOnce().GetAwaiter().GetResult();Check(form.CurrentData?.Batteries.Count==1,"read error recovery");
        typeof(MainForm).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,true);
        typeof(MainForm).GetField("readStarted",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,System.Diagnostics.Stopwatch.GetTimestamp()-11*System.Diagnostics.Stopwatch.Frequency);
        var timer=(System.Windows.Forms.Timer)typeof(MainForm).GetField("pollTimer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(timer,[EventArgs.Empty]);
        Check(form.CurrentData==null,"read timeout clears prior health");
        typeof(MainForm).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,false);
        form.RefreshOnce().GetAwaiter().GetResult();Check(form.CurrentData!=null,"timeout recovery");

        var inv=new PerformanceInventory(new("Synthetic Intel/AMD CPU",8,16,4800),
            new([new("DIMM A",16UL<<30,4800,5200,"Samsung","Fixture-DDR5",null,MemoryType:"DDR5")],2),
            [new("gpu","Synthetic GPU",GpuKind.Dedicated,8UL<<30,8UL<<30,null)],[]);
        var live=new PerformanceLive(DateTimeOffset.Now,36,3600,61,new(16UL<<30,10UL<<30,6UL<<30),[new("gpu",25,2UL<<30,1UL<<30,1500,47)],[]);
        var disk=new StorageDisk{DeviceId="audit",Model="Synthetic NVMe SSD",MediaType="SSD",BusType="NVMe",RemainingLifePercent=95,RemainingLifeSource="TestFixture",HealthCode="ProviderHealthy",TemperatureC=38,PowerOnCount=123,PowerOnHours=456,TotalBytes=512000000000,Volumes=[new("C:","NTFS",512000000000,350000000000,false)]};
        void Capture(string name)
        {
            form.PerformLayout();form.View.PerformLayout();form.PerformanceView.PerformLayout();Application.DoEvents();
            using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
            bitmap.Save(Path.Combine(output,name+".png"));report.Add(name+": "+form.Width+"x"+form.Height+", DPI="+form.DeviceDpi);
        }
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            form.SelectLanguage(lang);form.Size=new Size(1120,860);form.SelectPage(true);
            foreach(string scene in new[]{"normal","missing","zero","stale","disk-warning","disk-critical","disk-conflict","hdd"})
            {
                var d=scene switch
                {"disk-warning"=>disk with{HealthCode="Warning"},"disk-critical"=>disk with{FailurePredicted=true},"disk-conflict"=>disk with{Diagnostics=["ata-life:ConflictingSources"]},"hdd"=>disk with{MediaType="HDD",AtaSectorWarning=true,RemainingLifePercent=null},_=>disk};
                var sample=scene=="zero"?live with{CpuUsagePercent=0,CpuTemperatureC=0,Memory=new(16UL<<30,16UL<<30,0)}:live;
                var snapshot=new StorageSnapshot(DateTimeOffset.Now,[d],[]);
                if(scene=="stale"){sample=sample with{Timestamp=DateTimeOffset.Now.AddMinutes(-2)};snapshot=snapshot with{Timestamp=DateTimeOffset.Now.AddMinutes(-2)};}
                form.PerformanceView.SetData(scene=="missing"?null:inv,scene=="missing"?null:sample,scene=="missing"?null:snapshot,lang);
                Capture(L.Code(lang)+"-performance-"+scene);
            }
            form.SelectPage(false);
            foreach(string scene in new[]{"normal","critical","medium","above-design","conflict","no-battery","error","long-metadata"})
            {
                var r=new Reading{Design=60000,Full=54000,Capacity=27000,State=1,BatteryModel="Synthetic battery",Manufacturer="Synthetic manufacturer",Observation=new(12,40,[],12)};
                if(scene=="critical"){r.Full=15000;r.Capacity=1000;r.State=10;}
                if(scene=="medium")r.Full=36000;
                if(scene=="above-design")r.Full=65000;
                if(scene=="conflict")r.State=7;
                if(scene=="long-metadata"){r.BatteryModel=new string('W',85);r.Manufacturer="Fabricant de démonstration / Производитель — "+new string('M',80);}
                form.Size=scene=="long-metadata"?new Size(1000,740):new Size(1120,860);
                var data=new BatteryData(scene is "no-battery" or "error"?[]:[r],null,DateTimeOffset.Now,"Synthetic test system");
                form.View.SetData(scene=="error"?null:data,scene is "no-battery" or "error"?null:r,lang,scene=="error"?"Synthetic source read failure":"",true);
                Capture(L.Code(lang)+"-battery-"+scene);
                foreach(string field in new[]{"batteryModel","manufacturer","detailTime"})
                {
                    var label=(Label)typeof(Dashboard).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form.View)!;
                    Check((label.Parent as ScrollableControl)?.AutoScroll==true||label.Bottom<=label.Parent.ClientSize.Height,"battery metadata vertically reachable: "+field+" / "+lang+" / "+scene);
                    Check(label.Right<=label.Parent.ClientSize.Width,"battery metadata fits width: "+field+" / "+lang+" / "+scene);
                }
            }
            var smartDisk=disk with{SmartAttributes=[new(5,100,100,10,0,true),new(0xC5,100,100,0,1,false)],Diagnostics=["Synthetic diagnostics: no physical disk accessed"]};
            using var smart=new StorageDetailsForm(()=>new(DateTimeOffset.Now,[smartDisk],[]),smartDisk.DeviceId,lang);
            smart.Show(form);smart.Render();Application.DoEvents();
            using(var bitmap=new Bitmap(smart.Width,smart.Height)){smart.DrawToBitmap(bitmap,new Rectangle(Point.Empty,smart.Size));bitmap.Save(Path.Combine(output,L.Code(lang)+"-smart-details.png"));}
            Check(smart.ReadingRows>0,"SMART detail rows / "+lang);smart.Close();
        }
        report.Add("All screenshots use synthetic labelled fixtures; not physical sensor readings. Actual runner DPI only; other physical DPI/multi-monitor systems remain unvalidated.");
        report.AddRange(failures.Select(x=>"FAIL: "+x));File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"audit-visual.txt"),report);
        if(failures.Count>0)throw new Exception("Visual audit: "+string.Join("; ",failures));
    }
}
