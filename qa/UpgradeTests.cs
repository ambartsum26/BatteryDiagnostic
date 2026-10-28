using System.Reflection;
namespace BatteryDiagnostic;

internal static class UpgradeTests
{
    internal static void Run()
    {
        var failures=new List<string>();
        void Check(bool value,string reason){if(!value)failures.Add(reason);}
        var type=typeof(MemoryModule).GetProperty("MemoryType");
        MemoryModule Module(byte code)
        {
            byte[] raw=new byte[73];raw[1]=3;raw[2]=2;
            BitConverter.GetBytes((uint)(raw.Length-8)).CopyTo(raw,4);
            raw[8]=16;raw[9]=15;raw[10]=1;raw[13]=3;raw[21]=2;
            int m=25;raw[m]=17;raw[m+1]=40;raw[m+4]=1;raw[m+18]=code;
            BitConverter.GetBytes((ushort)8192).CopyTo(raw,m+12);
            BitConverter.GetBytes((ushort)3200).CopyTo(raw,m+21);
            BitConverter.GetBytes((ushort)2933).CopyTo(raw,m+32);
            raw[67]=127;raw[68]=4;
            return PerformanceSensors.ParseSmbiosMemory(raw).Modules.Single();
        }
        foreach(var (code,name) in new (byte,string)[]{(0x18,"DDR3"),(0x1a,"DDR4"),(0x22,"DDR5"),(0x1d,"LPDDR3"),(0x1e,"LPDDR4"),(0x23,"LPDDR5")})
            Check(Equals(type?.GetValue(Module(code)),name),"DDR decoder "+name);
        foreach(byte code in new byte[]{0,1,2,0xff})
            Check(type?.GetValue(Module(code))==null,"unknown firmware memory type stays unknown "+code);
        Check(Module(2).ConfiguredSpeedMTs==2933,"unknown type retains speed without guessing DDR");

        using var form=new MainForm();
        _=form.Handle;form.PerformLayout();
        IEnumerable<Control> All(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(All(c)));
        var maximize=All(form).OfType<Button>().FirstOrDefault(c=>Equals(c.Tag,"maximizeRestore"));
        Check(form.MaximizeBox&&maximize!=null,"maximize and restore button exists");
        if(maximize!=null)
        {
            var click=typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!;
            foreach(var lang in Enum.GetValues<UiLanguage>())
            {
                form.SelectLanguage(lang);form.WindowState=FormWindowState.Normal;
                string? before=maximize.AccessibleName;
                click.Invoke(maximize,[EventArgs.Empty]);
                Check(form.WindowState==FormWindowState.Maximized,"maximize click "+lang);
                Check(maximize.AccessibleName!=before,"restore action localized "+lang);
                click.Invoke(maximize,[EventArgs.Empty]);
                Check(form.WindowState==FormWindowState.Normal,"restore click "+lang);
            }
        }
        if(failures.Count>0)throw new Exception("Upgrade regression: "+string.Join("; ",failures));

        // CI renders a QA-only form whose OnShown sensor startup is removed.
        form.Show();Application.DoEvents();
        var ram3=Module(0x18);var ram4=Module(0x1a);var ram5=Module(0x22);
        var inv=new PerformanceInventory(new("Intel Core i7 — demonstration fixture",8,16,4800),new([ram3,ram4,ram5],4),
            [new("gpu-a","NVIDIA GeForce RTX — demonstration fixture",GpuKind.Dedicated,8UL<<30,8UL<<30,null)],[]);
        var live=new PerformanceLive(DateTimeOffset.Now,31,3600,54,new(24UL<<30,16UL<<30,8UL<<30),[],[]);
        var disk=new StorageDisk{DeviceId="fixture",Model="NVMe — demonstration fixture",MediaType="SSD",BusType="NVMe",RemainingLifePercent=96,RemainingLifeSource="TestFixture",TotalBytes=512000000000,TemperatureC=36,PowerOnCount=123,PowerOnHours=456,Volumes=[new("C:","NTFS",512000000000,350000000000,false)]};
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"ui-preview"));
        foreach(var lang in Enum.GetValues<UiLanguage>())
        foreach(var size in new[]{new Size(1000,740),new Size(1120,860),new Size(1600,1000)})
        {
            form.WindowState=FormWindowState.Normal;form.Size=size;form.SelectLanguage(lang);
            form.PerformanceView.SetData(inv,live,new(DateTimeOffset.Now,[disk],[]),lang);
            for(int i=0;i<3;i++)
            {
                form.PerformanceView.Selectors[0].SelectedIndex=i;
                Check(form.PerformanceView.Selectors[0].Text.Contains(new[]{"DDR3","DDR4","DDR5"}[i]),"DDR type visible per selected module");
            }
            form.PerformLayout();form.PerformanceView.PerformLayout();
            foreach(var selector in form.PerformanceView.Selectors.Take(3))
                Check(selector.Left>=0&&selector.Top>=0&&selector.Right<=form.PerformanceView.Width&&selector.Bottom<=form.PerformanceView.Height,"selector bounds "+lang+" / "+size);
            using var image=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));
            var colors=new HashSet<int>();
            for(int y=0;y<image.Height;y+=16)for(int x=0;x<image.Width;x+=16)colors.Add(image.GetPixel(x,y).ToArgb());
            Check(colors.Count>10,"rendered window contains real controls, not a hidden-form background");
            image.Save(Path.Combine(AppContext.BaseDirectory,"ui-preview",L.Code(lang)+"-"+size.Width+".png"));
        }
        if(failures.Count>0)throw new Exception("Upgrade regression: "+string.Join("; ",failures));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"upgrade-tests.txt"),"PASS: DDR3/4/5 and LPDDR parser; unknown memory; maximize/restore in FR/EN/RU; selector type and bounds; 9 synthetic UI screenshots. No hardware activation.");
    }
}
