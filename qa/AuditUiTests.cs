using System.Reflection;
namespace BatteryDiagnostic;

internal static class AuditUiTests
{
    internal static void Run()
    {
        var failures=new List<string>();
        void Check(bool value,string reason){if(!value)failures.Add(reason);}
        object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
        void Tick(PerformanceDashboard view)=>typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Field(view,"freshnessTimer"),[EventArgs.Empty]);
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            string T(string key)=>PerfText.Get(lang,key);
            using var view=new PerformanceDashboard{Size=new Size(1000,710)};
            var inv=new PerformanceInventory(new("Audit CPU",4,8,3600),new([new("DIMM",16UL<<30,3200,3200,"Audit",null,null)],1),[new("gpu","Audit GPU",GpuKind.Dedicated,8UL<<30,null,null)],[]);
            var live=new PerformanceLive(DateTimeOffset.Now,37,3600,63,new(16UL<<30,5UL<<30,11UL<<30),[new("gpu",29,2UL<<30,1UL<<30,1500,47)],[]);
            var disk=new StorageDisk{DeviceId="audit",Model="Audit SSD",MediaType="SSD",RemainingLifePercent=87,RemainingLifeSource="TestFixture",HealthCode="Warning",TemperatureC=43,Volumes=[new("C:","NTFS",100000000000,23000000000,false)]};
            var storage=new StorageSnapshot(DateTimeOffset.Now,[disk],[]);
            view.SetData(inv,live,storage,lang);
            string fresh=view.AccessibleDescription??"";
            foreach(var key in new[]{"usage","temperature","used","available","gpu","health","usedSpace","free"})
                Check(fresh.Contains(T(key)),"accessible live measurement label "+key+" / "+lang);
            Check(fresh.Contains(L.Percent(lang,37))&&fresh.Contains(L.Percent(lang,29)),"accessible CPU/GPU usage values / "+lang);
            Check(fresh.Contains(view.CurrentDiskCondition),"accessible drive warning / "+lang);
            // Age the already-present payload without SetData: this exercises
            // the independent timer, not a conveniently refreshed fixture.
            typeof(PerformanceDashboard).GetField("live",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,live with {Timestamp=DateTimeOffset.Now.AddMinutes(-2)});
            typeof(PerformanceDashboard).GetField("storage",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,storage with {Timestamp=DateTimeOffset.Now.AddMinutes(-2)});
            Tick(view);
            var tips=(ToolTip)Field(view,"tips");
            Check(!view.Selectors[2].Text.Contains("87"),"expired disk selector clears percentage / "+lang);
            Check(!tips.GetToolTip(view.Selectors[2]).Contains("87")&&!tips.GetToolTip(view.Selectors[2]).Contains("43"),"expired disk tooltip clears measurements / "+lang);
            var cpu=view.Controls.OfType<LinkLabel>().Single(c=>Equals(c.Tag,"cpu"));
            Check(!tips.GetToolTip(cpu).Contains("63"),"expired CPU help clears temperature / "+lang);
            string stale=view.AccessibleDescription??"";
            Check(stale.Contains(T("storageStale"))&&stale.Contains(T("liveStale")),"accessible stale status / "+lang);
            Check(!stale.Contains(L.Percent(lang,37))&&!stale.Contains("23 "+T("gb")),"accessible stale usage and volume values cleared / "+lang);
            view.SetData(null,null,null,lang);
            Check((view.AccessibleDescription??"").Contains(T("unknown")),"missing accessible measurements / "+lang);

            using var battery=new Dashboard{Size=new Size(1000,652)};
            var reading=new Reading{Design=60000,Full=54000,Capacity=27000,State=1};
            foreach(var window in new[]{new Size(1000,740),new Size(1044,788)})
            {
                using var host=new Form{ClientSize=new Size(window.Width,window.Height-88)};
                using var layoutView=new Dashboard{Dock=DockStyle.Fill};
                host.Controls.Add(layoutView);_=host.Handle;_=layoutView.Handle;
                var fixture=new Reading{Design=60000,Full=54000,Capacity=27000,State=1};
                layoutView.SetData(new([fixture],null,DateTimeOffset.Now,"Audit"),fixture,lang,"",true);
                void Layout(Control control){control.PerformLayout();foreach(Control child in control.Controls)Layout(child);}
                Layout(host);
                foreach(var name in new[]{"declaredCaption","fullCaption","declaredValue","fullValue"})
                {
                    var label=(Label)Field(layoutView,name);
                    var preferred=label.GetPreferredSize(new Size(label.ClientSize.Width,0));
                    Check(label.ClientSize.Height>=preferred.Height,"capacity text height "+name+" / "+window+" / "+lang);
                    Rectangle visible=label.ClientRectangle;
                    Control current=label;
                    while(current.Parent is {} parent)
                    {
                        visible.Offset(current.Left,current.Top);
                        visible=Rectangle.Intersect(visible,parent.ClientRectangle);
                        current=parent;
                    }
                    Check(visible.Width>=label.ClientSize.Width&&visible.Height>=label.ClientSize.Height,"capacity ancestor clipping "+name+" / "+window+" / "+lang);
                }
            }

            foreach(var (flags,state) in new (uint,string)[]{(1,"connectedIdle"),(7,"conflict"),(9,"critical"),(0,"idle"),(2,"discharging"),(5,"charging")})
            {
                reading.State=flags;
                battery.SetData(new([reading],null,DateTimeOffset.Now,"Audit"),reading,lang,"",true);
                var bottom=(Label)Field(battery,"bottomState");
                Check(bottom.Text.Contains(L.Get(lang,state)),"visible battery state "+state+" / "+lang);
                Check(((ToolTip)Field(battery,"tips")).GetToolTip(battery.Gauge).Contains(L.Get(lang,state)),"visible gauge help "+state+" / "+lang);
            }
        }
        Check(L.Get(UiLanguage.English,"health")=="Capacity retention","health grade is explicitly capacity retention");
        Check(L.Get(UiLanguage.English,"full")=="Full-charge capacity","full-charge capacity label is precise");
        if(failures.Count>0)throw new Exception("UI audit regression ("+failures.Count+"): "+string.Join("; ",failures));
    }
}
