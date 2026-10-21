using System.Drawing;
using System.Windows.Forms;
namespace BatteryDiagnostic;
internal static class QaEntry
{
    [STAThread] static int Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            LibCdiStorage.SelfTest();
            string nativeVersion=LibCdiStorage.CheckNativeApi(Path.Combine(Environment.CurrentDirectory,"publish-win-x64","libcdi.dll"));
            Console.WriteLine("Verified libcdi native API version: "+nativeVersion+"; no hardware enumeration");
            PerformanceStorage.SelfTest();DiskAssessmentEngine.SelfTest();PerformanceSensors.SelfTest();
            PerfText.SelfTest();LibrarySensors.SelfTest();PerformanceTemperatures.SelfTest();MemoryManufacturer.SelfTest();
            PerformanceUiTests.Run();
            Directory.CreateDirectory("qa-output");
            var now=DateTimeOffset.Now;
            var gpu=new GpuInventory("qa","NVIDIA GeForce RTX 4070 SUPER [TEST DATA]",GpuKind.Dedicated,12562612224,16UL<<30,null);
            var inventory=new PerformanceInventory(new("Intel Core i7-14700K [TEST DATA]",20,28,3400),new([new("DIMMA2",16UL<<30,6000,6000,"Corsair",null,null),new("DIMMB2",16UL<<30,6000,6000,"Corsair",null,null)],4),[gpu],[]);
            var live=new PerformanceLive(now,3.8,3400,47,new((32UL<<30)-(256UL<<20),17UL<<30,(15UL<<30)-(256UL<<20)),[new("qa",1.7,751619276,107374182,2640,40)],["CPU temperature source: Intel package DTS / PawnIO"]);
            var disk=new StorageDisk {Diagnostics=["libcdi:MatchedByPhysicalDriveAndModel"],Index=2,DeviceId="test",Model="Samsung SSD 990 PRO 1TB [TEST DATA]",MediaType="SSD",BusType="NVMe",TotalBytes=1000204886016,HealthCode="NvmeNoCriticalWarning",HealthSource="NvmeHealthLog",NvmeCriticalWarning=0,NvmeAvailableSparePercent=100,NvmeSpareThresholdPercent=10,NvmeMediaErrors=0,PowerOnCount=42,PowerOnHours=5122,RemainingLifePercent=98,RemainingLifeSource="NvmeHealthLog:PercentageUsed",TemperatureC=44.9,Volumes=[new("C:","NTFS",899000000000,444600000000,false)]};
            foreach(var language in Enum.GetValues<UiLanguage>())
            foreach(var size in new[]{new Size(1000,710),new Size(1120,795)})
            {
                using var host=new Form {ClientSize=size,FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false};
                using var view=new PerformanceDashboard {Dock=DockStyle.Fill};host.Controls.Add(view);host.Show();Application.DoEvents();
                view.SetData(inventory,live,new(now,[disk],[]),language);
                if(view.InstalledRamBytes!=(32UL<<30))throw new Exception("Installed RAM must sum module capacities, not usable memory");
                if(view.CdiStatus!=PerfText.Get(language,"cdiMatched"))throw new Exception("Per-disk CDI status missing");
                for(int i=0;i<31;i++)view.AddSample(live with {Timestamp=now.AddSeconds(-60+i*2),CpuUsagePercent=4+(i%7)});
                _=view.Handle;view.PerformLayout();Application.DoEvents();
                foreach(var c in view.Selectors)if(c.Left<0||c.Right>view.Width||c.Bottom>view.Height)throw new Exception("Selector outside viewport");
                using var bitmap=new Bitmap(size.Width,size.Height);
                view.DrawToBitmap(bitmap,new Rectangle(Point.Empty,size));
                bitmap.Save($"qa-output/{language}-{size.Width}.png");
                view.SetData(inventory,live with {Timestamp=now.AddMinutes(-2)},new(now.AddMinutes(-2),[disk],[]),language);
                Application.DoEvents();using var stale=new Bitmap(size.Width,size.Height);view.DrawToBitmap(stale,new Rectangle(Point.Empty,size));stale.Save($"qa-output/{language}-stale-{size.Width}.png");
                view.SetData(inventory,null,new(now,[disk with {PowerOnCount=null,RemainingLifePercent=null,NvmeMediaErrors=null,NvmeAvailableSparePercent=null}],[]),language);
                Application.DoEvents();using var missing=new Bitmap(size.Width,size.Height);view.DrawToBitmap(missing,new Rectangle(Point.Empty,size));missing.Save($"qa-output/{language}-missing-{size.Width}.png");
            }
            File.WriteAllText("qa-output/result.txt","Native libcdi exports and version: "+nativeVersion+" (no hardware enumeration). PASS: storage decoder, overflow boundaries, disk assessment, translations, inventory parsing, sensor parsing, existing UI fixture tests; installed RAM versus usable RAM; CDI missing/error/unmatched/matched/stale states; rendered FR/EN/RU at two sizes, fresh/stale/missing. No physical hardware probe, no elevation, no driver install.");
            Console.WriteLine(File.ReadAllText("qa-output/result.txt"));return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
