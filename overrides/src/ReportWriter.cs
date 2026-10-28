using System.Globalization;
using System.Net;
using System.Text;
namespace BatteryDiagnostic;
internal static class ReportWriter
{
    internal static void SelfTest()
    {
        string path=Path.Combine(Path.GetTempPath(),"battery-diagnostic-report-test-"+Guid.NewGuid().ToString("N")+".html");
        try
        {
            var reading=new Reading { Slot=0,BatteryModel="Pack \"X\"",Manufacturer="Maker, Inc.",Design=60000,Full=48000,Capacity=24000,State=2,Voltage=12000,Rate=-500 };
            var data=new BatteryData([reading],new Native.Power { AC=0,Flags=0,Percent=50 },DateTimeOffset.Parse("2026-01-01T12:00:00Z"),"Dell TestModel");
            Write(path,data,UiLanguage.English);
            string html=File.ReadAllText(path),csv=File.ReadAllText(Path.ChangeExtension(path,".csv"));
            if(!html.Contains("Dell TestModel",StringComparison.Ordinal)||!html.Contains("Pack &quot;X&quot;",StringComparison.Ordinal))throw new Exception("Report test failed: HTML model values");
            if(!csv.StartsWith("timestamp,computer_model,battery,battery_model,manufacturer,",StringComparison.Ordinal)||!csv.Contains("\"Pack \"\"X\"\"\"",StringComparison.Ordinal)||!csv.Contains("\"Maker, Inc.\"",StringComparison.Ordinal))throw new Exception("Report test failed: CSV model fields or escaping");
        }
        finally
        {
            foreach(string file in new[]{path,Path.ChangeExtension(path,".csv")})if(File.Exists(file))File.Delete(file);
        }
    }
    internal static void Write(string path,BatteryData data,UiLanguage lang)
    {
        string T(string key)=>L.Get(lang,key);
        string N(double? value,string unit="")=>value.HasValue?value.Value.ToString("0.###",L.Culture(lang))+" "+unit:T("unknown");
        var body=new StringBuilder();
        body.AppendLine("Battery Diagnostic Portable "+MainForm.Version);
        body.AppendLine(T("time")+": "+data.Time.ToString("G",L.Culture(lang))+" "+data.Time.ToString("zzz",CultureInfo.InvariantCulture));body.AppendLine(T("model")+": "+data.Machine);
        body.AppendLine(T("live"));
        var charge=Engine.Charge(null,data);
        body.AppendLine(T(charge.Online==true?"online":charge.Online==false?"offline":"powerUnknown"));
        if(data.Batteries.Count==0)body.AppendLine(T("noBattery"));
        foreach(var r in data.Batteries)
        {
            var state=Engine.Charge(r,data);var assessment=Engine.Evaluate(r,lang);
            string units=r.Relative?T("relative"):"mWh";
            body.AppendLine("\n"+L.F(lang,"battery",r.Slot+1));
            body.AppendLine(T("batteryModel")+": "+(r.BatteryModel??T("unknown")));
            body.AppendLine(T("manufacturer")+": "+(r.Manufacturer??T("unknown")));
            body.AppendLine(T("declared")+": "+N(r.Design,units));
            body.AppendLine(T("full")+": "+N(r.Full,units));
            string grade=T(Engine.GradeLabelKey(assessment.Grade));
            body.AppendLine(T("health")+": "+grade);
            if(assessment.Retained.HasValue)body.AppendLine(L.F(lang,"healthTip",N(r.Full,units),N(r.Design,units),L.Percent(lang,assessment.Retained.Value)));
            body.AppendLine(T("charge")+": "+(state.Percent.HasValue?L.Percent(lang,state.Percent.Value):T("unknown"))+" · "+T(state.StateKey));
            body.AppendLine(T("source")+": "+T(state.SourceKey));
            body.AppendLine(T("rawRemaining")+": "+N(r.Capacity,units));
            body.AppendLine(T("voltage")+": "+N(r.Voltage,"mV"));
            body.AppendLine(T("rate")+": "+N(r.Rate,r.Relative?T("relative")+"/h":"mW"));
            body.AppendLine(T("temperature")+": "+N(r.Temperature,"°C"));
            body.AppendLine(T("cycles")+": "+(r.Cycles?.ToString()??T("unknown")));
            body.AppendLine(T("powerState")+": "+(r.State?.ToString()??T("unknown")));
                        if(r.Observation is {} observation){
                body.AppendLine(L.F(lang,"observation",observation.Samples,observation.Seconds.ToString("0",L.Culture(lang))));
                body.AppendLine(T("passive"));
                body.AppendLine(T(observation.ValidSamples<10||observation.Seconds<30?"shortObservation":observation.Findings.Length==0&&assessment.Signals.Count==0&&assessment.Notes.Count==0?"noObservedSignal":"quality"));
                foreach(var finding in observation.Findings)body.AppendLine(T(finding));
            }
            foreach(var signal in assessment.Signals)body.AppendLine(T("signals")+": "+signal);
            foreach(var note in assessment.Notes)body.AppendLine(T("quality")+": "+note);
        }
        body.AppendLine("\n"+T("scope"));
        File.WriteAllText(path,"<!doctype html><html lang='"+L.Code(lang)+"'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Battery Diagnostic</title><style>body{background:#101923;color:#e8eff6;font:16px Segoe UI,sans-serif;margin:clamp(12px,4vw,40px) auto;padding:0 12px;max-width:1100px}h1{color:#69cfea}pre{overflow-wrap:anywhere;white-space:pre-wrap;line-height:1.65;background:#182633;padding:24px;border-radius:12px}</style><h1>"+WebUtility.HtmlEncode(T("report"))+"</h1><pre>"+WebUtility.HtmlEncode(body.ToString())+"</pre></html>",Encoding.UTF8);
        const string header="timestamp,computer_model,battery,battery_model,manufacturer,relative,design,full,remaining,raw_percent,display_percent,percent_source,voltage_mV,rate,temperature_C,cycles,power_state,system_ac,system_percent,capacity_condition,errors\r\n";
        var csv=new StringBuilder(header);
        string Num(double? n)=>n?.ToString("R",CultureInfo.InvariantCulture)??"";
        static string Csv(string? value)=>"\""+(value??"").Replace("\"","\"\"")+"\"";
        foreach(var r in data.Batteries)
        {
            var state=Engine.Charge(r,data);
            var assessment=Engine.Evaluate(r,lang);
            string condition=T(Engine.GradeLabelKey(assessment.Grade));
            csv.AppendLine(string.Join(",",Csv(data.Time.ToString("O")),Csv(data.Machine),r.Slot,Csv(r.BatteryModel),Csv(r.Manufacturer),r.Relative,Num(r.Design),Num(r.Full),Num(r.Capacity),Num(r.Percent),Num(state.Percent),Csv(state.SourceKey),Num(r.Voltage),Num(r.Rate),Num(r.Temperature),r.Cycles?.ToString()??"",r.State?.ToString()??"",data.Power?.AC.ToString()??"",data.Power?.Percent<=100?data.Power.Value.Percent.ToString():"",Csv(condition),Csv(string.Join("; ",r.Errors))));
        }
        File.WriteAllText(Path.ChangeExtension(path,".csv"),csv.ToString(),new UTF8Encoding(true));
    }
}
