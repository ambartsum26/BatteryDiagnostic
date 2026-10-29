using System.Diagnostics;
using System.Text.Json;
namespace BatteryDiagnostic;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        try
        {
            // Automatically activate verified, already installed PawnIO when elevated.
            // Never install/start a driver; synthetic tests never activate CPU hardware.
            if(!args.Contains("--self-test") && !args.Contains("--ui-smoke") && !args.Contains("--pawnio-status") &&
                (args.Contains("--cpu-sensors") ||
                 (PerformanceCpuTemperature.IsAdministrator &&
                  PerformanceCpuTemperature.CheckInstalledDriver().State==CpuTemperatureState.Ready)))
                PerformanceCpuTemperature.EnableForProcess();
            // Installer preflight: read-only, no driver loading or installation.
            if(args.Contains("--pawnio-status"))
            {
                var installed = PerformanceCpuTemperature.CheckInstalledDriver();
                var cpu = PerformanceCpuTemperature.CheckProcessorSupport();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"pawnio-status.json"),
                    JsonSerializer.Serialize(new {Version=MainForm.Version,
                        DriverState=installed.State.ToString(),DriverDiagnostic=installed.Diagnostic,
                        ProcessorState=cpu.State.ToString(),ProcessorDiagnostic=cpu.Diagnostic},
                        new JsonSerializerOptions {WriteIndented=true}));
                return;
            }
            if(args.Contains("--self-test"))
            {
                CompatibilityTests.Run();SensorProviderTests.Run();Engine.SelfTest();LibrarySensors.SelfTest();IntegrationTests.Run();CpuBenchmark.SelfTest();
                PerformanceSensors.SelfTest();PerformanceStorage.SelfTest();DiskAssessmentEngine.SelfTest();PerfText.SelfTest();MemoryManufacturer.SelfTest();PerformanceGpuCounters.SelfTest();PerformanceTemperatures.SelfTest();PerformanceCpuTemperature.SelfTest();ExternalCpuTemperature.SelfTest();CpuActivation.SelfTest();
                ApplicationConfiguration.Initialize();PerformanceUiTests.Run();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test.txt"),"PASS "+MainForm.Version+": battery calculations/indicators; CPU deltas, Intel DTS/module integrity (no driver activation); SMBIOS/RAM; GPU PDH/NVAPI; ATA/SAT12/SAT16 buffers/read whitelist/identity; SSD profiles; NVMe spare/temperature fields; separate condition/resource and fault priority; FR/EN/RU compact UI and raw SMART/stale table; CPU benchmark arithmetic/median/cancellation. Synthetic inputs are not hardware validation. Optional libcdi interop and identity parser are statically exercised only. External AIDA64/HWiNFO CPU temperature parsing and stale/identity guards pass synthetic tests; hardware not validated.");
                return;
            }
            if(args.Contains("--performance-probe"))
            {
                var inventory=PerformanceSensors.ReadInventory();
                _=PerformanceSensors.ReadLive();
                var storage=PerformanceStorage.Read();
                Thread.Sleep(3200);
                _=PerformanceSensors.ReadLive();
                Thread.Sleep(700);
                var live=PerformanceSensors.ReadLive();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"performance-probe.json"),JsonSerializer.Serialize(new {Version=MainForm.Version,Inventory=inventory,Live=live,Storage=storage,CpuTemperature=PerformanceCpuTemperature.ReadCached(),ExternalCpuTemperature=ExternalCpuTemperature.ReadCached()},new JsonSerializerOptions {WriteIndented=true}));
                return;
            }
            if(args.Contains("--probe"))
            {
                var result=new WindowsBatterySource().ReadAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"probe.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true,IncludeFields=true }));
                return;
            }
            var startupLanguage=CpuActivation.LanguageFromArguments(args);
            L.ApplyCulture(startupLanguage); ApplicationConfiguration.Initialize();
            using var form=new MainForm();
            if(startupLanguage!=UiLanguage.French)form.SelectLanguage(startupLanguage);
            if(args.Contains("--ui-smoke"))
            {
                form.Shown+=async(_,_)=>
                {
                    try
                    {
                        var elapsed=Stopwatch.StartNew();
                        while(form.UpdateCount<2 && elapsed.ElapsedMilliseconds<10000)await Task.Delay(100);
                        if(form.UpdateCount<2)throw new Exception("Automatic refresh did not run twice");
                        while(form.PerformanceUpdateCount<2 && elapsed.ElapsedMilliseconds<15000)await Task.Delay(100);
                        if(form.PerformanceUpdateCount<2||!form.PerformanceView.HasLiveSample)throw new Exception("Performance live sampling did not run twice");
                        if(!form.IsPerformanceSelected)throw new Exception("Performance must be the first selected tab");
                        // Allow bounded inventory providers to finish before capturing real values.
                        await Task.Delay(8500);
                        if(form.Language!=UiLanguage.French)throw new Exception("French must be the startup language");
                        if(ContainsText(form,"ⓘ"))throw new Exception("Removed health information icon is still present");
                        foreach(var language in Enum.GetValues<UiLanguage>())
                        {
                            form.SelectLanguage(language);
                            await Task.Delay(100);
                            form.SelectPage(true);
                            if(!form.PerformanceView.HasLiveSample)throw new Exception("Live Performance sample lost during language switch");
                            Capture(form,"performance-smoke-"+L.Code(language)+".png");
                            if(form.CurrentStorage?.Disks.FirstOrDefault() is {} disk)
                            {
                                using var details=new StorageDetailsForm(()=>form.CurrentStorage,disk.DeviceId,language);
                                Exception? detailsError=null;
                                details.Shown+=async(_,_)=>
                                {
                                    try {await Task.Delay(150);Capture(details,"smart-smoke-"+L.Code(language)+".png");}
                                    catch(Exception ex){detailsError=ex;}
                                    finally {details.Close();}
                                };
                                details.ShowDialog(form);
                                if(detailsError!=null)throw detailsError;
                            }
                            foreach(var selector in form.PerformanceView.Selectors)
                                if(selector.Right>form.PerformanceView.Width||selector.Bottom>form.PerformanceView.Height||selector.Left<0||selector.Top<0)throw new Exception("Performance selector clipped: "+selector.AccessibleName);
                            form.SelectPage(false);
                            if(form.CurrentData?.Batteries.Count==0 && form.View.CurrentHealth!=L.Get(language,"unknown"))throw new Exception("No battery must not receive a health grade");
                            if(!ContainsText(form.View,L.Get(language,"batteryModel")))throw new Exception("Battery model label missing for "+L.Code(language));
                            if(ContainsText(form.View,L.Get(language,"source")+":"))throw new Exception("Percentage source line should not be visible");
                            var code=L.Code(language);
                            form.SaveReport(Path.Combine(AppContext.BaseDirectory,"report-"+code+"-smoke.html"));
                            Capture(form,"ui-smoke-"+code+".png");
                        }
                        form.SelectLanguage(UiLanguage.French);
                        form.SelectPage(true);
                        Capture(form,"ui-smoke.png");
                        form.Size=new Size(940,600);await Task.Delay(100);Capture(form,"ui-smoke-compact.png");
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"ui-smoke-result.txt"),"PASS "+MainForm.Version+": battery API and CPU/RAM automatic refresh >=2; Performance first tab; FR/EN/RU tabs and actual SMART detail dialogs/screenshots; compact layout; no-battery unknown; reports. Hardware-blocked readings remain unavailable.");
                    }
                    catch(Exception ex)
                    {
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"error.txt"),ex.ToString());Environment.ExitCode=1;
                    }
                    finally{form.Close();}
                };
            }
            Application.Run(form);
        }
        catch(Exception ex)
        {
            if(args.Length>0){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"error.txt"),ex.ToString());Environment.ExitCode=1;}
            else MessageBox.Show(ex.Message,"DevicePulse");
        }
    }
    static void Capture(Form form,string name)
    {
        using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory,name));
    }
    static bool ContainsText(Control control,string text)
    {
        if(control.Text.Contains(text,StringComparison.Ordinal))return true;
        foreach(Control child in control.Controls)if(ContainsText(child,text))return true;
        return false;
    }
}
