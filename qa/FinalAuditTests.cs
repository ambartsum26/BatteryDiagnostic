namespace BatteryDiagnostic;
internal static class FinalAuditTests
{
    internal static void Run()
    {
        var failures=new List<string>();
        var results=new List<string>();
        foreach(var (name,test) in new (string,Action)[]{("Storage/battery source integration",AuditHealthTests.Run),("CPU/GPU source validation",AuditSensorTests.Run),("Visible status/freshness/accessibility",AuditUiTests.Run)})
        {
            try{test();results.Add("PASS: "+name);}
            catch(Exception ex){failures.Add(name+": "+ex);results.Add("FAIL: "+name+": "+ex.Message);}
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"audit-regressions.txt"),results);
        if(failures.Count==0)AuditVisualTests.Run();
        if(failures.Count>0)throw new Exception("FINAL AUDIT REGRESSION: "+string.Join(Environment.NewLine,failures));
    }
}
