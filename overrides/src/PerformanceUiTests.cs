namespace BatteryDiagnostic;

internal static class PerformanceUiTests
{
    internal static void Run()
    {
        static void Check(bool value,string what){if(!value)throw new Exception("Performance UI: "+what);}
        var a=new GpuInventory("gpu-a","Adapter A",GpuKind.Dedicated,8UL<<30,8UL<<30,null);
        var b=new GpuInventory("gpu-b","Adapter B",GpuKind.Integrated,128UL<<20,8UL<<30,null);
        var inv=new PerformanceInventory(new("Test inventory",4,8,3600),new([],null),[a,b],[]);
        var diskA=new StorageDisk {DeviceId="disk-a",Model="Disk A",Volumes=[new("C:","NTFS",1000,250,false)]};
        var diskB=new StorageDisk {DeviceId="disk-b",Model="Disk B",Volumes=[new("D:","NTFS",2000,500,false)]};
        var storage=new StorageSnapshot(DateTimeOffset.Now,[diskA,diskB],[]);
        using var view=new PerformanceDashboard {Size=new Size(1000,538)};
        // All disks and values below are test fixtures. This verifies rendering and
        // selection rules only; it does not claim that any sensor is supported.
        view.SetData(inv with {Memory=new([new("a",16UL<<30,null,null,null,null,null),new("b",16UL<<30,null,null,null,null,null)],2)},null,storage,UiLanguage.French);
        Check(view.InstalledRamBytes==(32UL<<30),"sum installed RAM modules");
        view.SetData(inv with {Memory=new([new("a",null,null,null,null,null,null)],2)},null,storage,UiLanguage.French);
        Check(view.InstalledRamBytes is null,"partial RAM inventory cannot invent total");
        view.SetData(inv with {Memory=new([new("a",ulong.MaxValue,null,null,null,null,null),new("b",1,null,null,null,null,null)],2)},null,storage,UiLanguage.French);
        Check(view.InstalledRamBytes is null,"RAM sum overflow remains unavailable");
        var cpuLink=view.Controls.OfType<LinkLabel>().Single(c=>Equals(c.Tag,"cpu"));
        var diskLink=view.Controls.OfType<LinkLabel>().Single(c=>Equals(c.Tag,"diskAccess"));
        var smartLink=view.Controls.OfType<LinkLabel>().Single(c=>Equals(c.Tag,"smartDetails"));
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            // Desktop/laptop fixture inventories exercise the same controls and
            // collection handling. These are not reported as hardware readings.
            foreach(bool mobile in new[]{false,true})
            {
                var memoryModules=Enumerable.Range(0,mobile?2:4).Select(i=>new MemoryModule((mobile?"SODIMM":"DIMM")+i,8UL<<30,3200,3200,"Fixture",null,null)).ToArray();
                var universal=new PerformanceInventory(new(mobile?"AMD Ryzen mobile fixture":"Intel desktop fixture",mobile?6:8,mobile?12:16,3600),new(memoryModules,mobile?2:4),mobile?[b]:[a,b],[]);
                var drives=storage with {Disks=mobile?[diskA]:[diskA,diskB,new StorageDisk {DeviceId="disk-c",Model="HDD fixture",MediaType="HDD"}]};
                view.SetData(universal,new PerformanceLive(DateTimeOffset.Now,25,3000,null,new(16UL<<30,8UL<<30,8UL<<30),[],[]),drives,lang);
                Check(view.Selectors[0].Items.Count==memoryModules.Length&&view.Selectors[1].Items.Count==universal.Gpus.Count&&view.Selectors[2].Items.Count==drives.Disks.Count,"desktop/laptop multi-device inventory is retained");
                Check(view.AccessibleDescription?.Contains(universal.Cpu.Model!)==true,"CPU model does not depend on an OEM or laptop whitelist");
            }
            foreach(var (messageKey,acceptKey) in new (string,string?)[]
            {
                ("cpuUnsupported",null),("cpuDriverMissing","cpuOfficialPage"),
                ("cpuDriverRejected","cpuOfficialPage"),("cpuElevate","cpuRestart"),
                ("diskElevate","cpuRestart"),("cpuRestartError",null)
            })
            {
                using var dialog=CpuActivation.BuildDialog(lang,PerfText.Get(lang,messageKey),acceptKey==null?null:PerfText.Get(lang,acceptKey));
                // Create/layout and draw the form without showing it or invoking
                // its accept action. No browser, UAC prompt or driver is started.
                _=dialog.Handle;dialog.PerformLayout();
                var layout=dialog.Controls.OfType<TableLayoutPanel>().Single();layout.PerformLayout();
                var message=layout.Controls.OfType<Label>().Single();
                var buttons=layout.Controls.OfType<FlowLayoutPanel>().Single();buttons.PerformLayout();
                Check(dialog.Text==PerfText.Get(lang,"sensorAccess"),"sensor access dialog title is localized");
                var required=TextRenderer.MeasureText(message.Text,message.Font,new Size(message.ClientSize.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl);
                Check(required.Height<=message.ClientSize.Height,"sensor access explanation fits without clipping: "+messageKey+" / "+lang);
                Check(buttons.Controls.OfType<Button>().All(c=>c.Left>=0&&c.Top>=0&&c.Right<=buttons.ClientSize.Width&&c.Bottom<=buttons.ClientSize.Height),"sensor access buttons fit: "+messageKey+" / "+lang);
                Check(dialog.AcceptButton!=null&&dialog.CancelButton!=null,"sensor access dialog supports keyboard acceptance/cancellation");
                using var rendered=new Bitmap(dialog.Width,dialog.Height);
                dialog.DrawToBitmap(rendered,new Rectangle(Point.Empty,dialog.Size));
            }
            view.SetData(inv,null,storage,lang);
            var controls=view.Selectors;
            controls[1].SelectedIndex=1;controls[2].SelectedIndex=1;
            view.SetData(inv with {Gpus=[b,a]},null,storage with {Disks=[diskB,diskA]},lang);
            Check(controls[1].SelectedIndex==0&&controls[2].SelectedIndex==0,"selection follows device identity after reorder");
            Check(controls[3].Text.Contains("D:"),"volume remains attached to selected physical disk");
            view.SetData(inv,null,storage with {Disks=[diskA with {HealthCode="NoFailurePredicted",FailurePredicted=false}]},lang);
            Check(view.CurrentDiskCondition==PerfText.Get(lang,"insufficient"),"negative failure prediction alone cannot certify health");
            foreach(var (remaining,state) in new (double,string)[]{(100,"lifeNormal"),(11,"lifeNormal"),(10,"lifeLow"),(1,"lifeLow"),(0,"lifeExhausted")})
            {
                view.SetData(inv,null,storage with {Disks=[diskA with {RemainingLifePercent=remaining,RemainingLifeSource="TestFixture"}]},lang);
                Check(view.CurrentDiskResource==L.Percent(lang,remaining)+" · "+PerfText.Get(lang,state),"resource boundary and language: "+remaining+" / "+lang);
                Check(view.CurrentDiskCondition==PerfText.Get(lang,remaining<=10?"resourceCaution":"insufficient"),"resource is separate from complete hardware condition");
            }
            foreach(var (health,state) in new[]{("FailurePredicted","predicted"),("Unhealthy","unhealthy"),("AtaThresholdExceeded","smartLimit"),("Warning","warning")})
            {
                view.SetData(inv,null,storage with {Disks=[diskA with {RemainingLifePercent=95,HealthCode=health}]},lang);
                Check(view.CurrentDiskCondition==PerfText.Get(lang,state)&&view.CurrentDiskResource.Contains(L.Percent(lang,95)),"controller warnings override a high remaining percentage: "+health+" / "+lang);
            }
            view.SetData(inv,null,storage with {Disks=[diskA with {MediaType="HDD",HealthCode="AtaSectorWarning"}]},lang);
            Check(view.CurrentDiskCondition==PerfText.Get(lang,"sectorWarning"),"HDD sector caution remains meaningful without an invented resource percentage: "+lang);
            view.SetData(inv,null,storage with {Disks=[diskA with {RemainingLifePercent=0,HealthCode="EnduranceExceeded"}]},lang);
            Check(view.CurrentDiskResource==L.Percent(lang,0)+" · "+PerfText.Get(lang,"lifeExhausted")&&view.CurrentDiskCondition==PerfText.Get(lang,"resourceCaution"),"zero endurance stays separate from failure: "+lang);
            foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-1d,101d})
            {
                view.SetData(inv,null,storage with {Disks=[diskA with {RemainingLifePercent=invalid}]},lang);
                Check(view.CurrentDiskCondition==PerfText.Get(lang,"unknown"),"invalid resource is unavailable rather than a healthy percentage: "+lang);
                Check(view.CurrentDiskResource==PerfText.Get(lang,"unknown"),"invalid resource is never printed as a percentage");
            }
            view.SetData(inv,null,storage with {Timestamp=DateTimeOffset.Now.AddMinutes(-2),Disks=[diskA with {RemainingLifePercent=100,HealthCode="ProviderHealthy"}]},lang);
            Check(view.CurrentDiskCondition==PerfText.Get(lang,"storageStale"),"stale resource percentage cannot remain current");
            Check(view.CurrentDiskResource==PerfText.Get(lang,"storageStale"),"stale resource is hidden independently of health");
            view.SetData(inv,null,storage with {Timestamp=DateTimeOffset.Now.AddMinutes(-2),Disks=[diskA with {HealthCode="ProviderHealthy"}]},lang);
            Check(view.CurrentDiskCondition==PerfText.Get(lang,"storageStale"),"stale health cannot remain current");
            view.SetData(null,null,null,lang);
            Check(!view.HasLiveSample,"missing sample remains missing");
            Check(controls.All(c=>c.Text==PerfText.Get(lang,"unknown")&&!c.Enabled),"missing inventory localized without fictitious devices");
            Check(cpuLink.Text==PerfText.Get(lang,"cpuActivate")&&cpuLink.AccessibleName==PerfText.Get(lang,"cpuActivate")&&cpuLink.Visible,"CPU sensor activation is available when temperature is missing and remains localized");
            view.SetData(inv,new PerformanceLive(DateTimeOffset.Now,25,3000,45,new(16UL<<30,8UL<<30,8UL<<30),[],[]),storage,lang);
            Check(!cpuLink.Visible,"CPU activation link yields its place to a real temperature reading");
            Check(!diskLink.Visible,"drive access link is hidden when no provider reports access denial");
            view.SetData(inv,null,storage with {Diagnostics=["storage:AccessDenied"]},lang);
            Check(diskLink.Text==PerfText.Get(lang,"diskAccess")&&diskLink.AccessibleName==PerfText.Get(lang,"diskAccess"),"drive access link is localized and accessible");
            Check(diskLink.Visible==!PerformanceCpuTemperature.IsAdministrator,"drive access link follows provider access denial and current elevation");
            view.SetData(inv,null,storage with {Disks=[diskA with {Diagnostics=["smart:AccessDenied"]}]},lang);
            Check(diskLink.Visible==!PerformanceCpuTemperature.IsAdministrator,"individual drive access denial also exposes the access action");
            foreach(var size in new[]{new Size(1000,538),new Size(940,516),new Size(800,430)})
            {
                view.Size=size;view.PerformLayout();
                Check(controls.All(c=>c.Left>=0&&c.Top>=0&&c.Right<=size.Width&&c.Bottom<=size.Height),"selectors fit compact viewport");
                Check(cpuLink.Left>=0&&cpuLink.Top>=0&&cpuLink.Right<=size.Width&&cpuLink.Bottom<=size.Height,"CPU activation link fits compact viewport: "+lang);
                Check(diskLink.Left>=0&&diskLink.Top>=0&&diskLink.Right<=size.Width&&diskLink.Bottom<=size.Height,"drive access link fits compact viewport: "+lang);
                Check(smartLink.Left>=0&&smartLink.Top>=0&&smartLink.Right<=size.Width&&smartLink.Bottom<=size.Height,"SMART details link fits compact viewport");
                Check(!view.AutoScroll,"compact dashboard never introduces scrollbars");
                // Exercise the custom draw path in every language and supported
                // compact size, without activating a driver or opening a dialog.
                using var rendered=new Bitmap(size.Width,size.Height);
                view.DrawToBitmap(rendered,new Rectangle(Point.Empty,size));
            }
            var detailStorage=storage with {Disks=[diskA with {MediaType="HDD",SmartAttributes=[new(5,100,100,36,2,true)]}]};
            using var detail=new StorageDetailsForm(()=>detailStorage,"disk-a",lang);
            detail.Render();Check(detail.ReadingRows==1,"raw SMART values can be inspected without activating hardware");
            var grid=detail.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<DataGridView>().Single();
            Check(Convert.ToString(grid.Rows[0].Cells[5].Value)=="000000000002","SMART table preserves original raw hex rather than an invented percentage");
            _=detail.Handle;detail.PerformLayout();
            var detailsClose=detail.Controls.OfType<Panel>().SelectMany(p=>p.Controls.OfType<CaptionButton>()).Single();
            Check(detailsClose.Width>=24&&detailsClose.Height>=24&&detailsClose.Left>=0&&detailsClose.Right<=detailsClose.Parent!.ClientSize.Width,"SMART caption close button remains inside the dark title bar");
            using(var renderedDetail=new Bitmap(detail.Width,detail.Height))detail.DrawToBitmap(renderedDetail,new Rectangle(Point.Empty,detail.Size));
            detailStorage=detailStorage with {Timestamp=DateTimeOffset.Now.AddMinutes(-2)};detail.Render();
            Check(detail.ReadingRows==1&&Convert.ToString(grid.Rows[0].Cells[0].Value)=="—"&&Convert.ToString(grid.Rows[0].Cells[5].Value)=="—","stale SMART table clears earlier readings");
        }
    }
}
