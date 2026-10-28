using System.Reflection;
namespace BatteryDiagnostic;

internal static class InstallerTests
{
    internal static void Run()
    {
        var type=typeof(MainForm).Assembly.GetType("BatteryDiagnostic.PawnIoInstaller");
        if(type==null)throw new Exception("Installer regression: integrated PawnIO installer is missing");
        object? Call(string method,params object[] args)=>type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,args);
        void Check(bool valid,string reason){if(!valid)throw new Exception("Installer regression: "+reason);}
        bool Offer(bool supported,bool admin,CpuTemperatureState state,string[] args)=>(bool)Call("ShouldOffer",supported,admin,state,args)!;
        Check(Offer(true,true,CpuTemperatureState.DriverNotInstalled,[]),"missing driver offered on supported elevated computer");
        foreach(var state in new[]{CpuTemperatureState.Ready,CpuTemperatureState.DriverRejected,CpuTemperatureState.SensorUnavailable})
            Check(!Offer(true,true,state,[]),"existing or unverified driver never replaced: "+state);
        Check(!Offer(false,true,CpuTemperatureState.DriverNotInstalled,[]),"unsupported processor is not offered a driver");
        Check(!Offer(true,false,CpuTemperatureState.DriverNotInstalled,[]),"non-elevated process cannot install driver");
        foreach(string argument in new[]{"--self-test","--upgrade-test","--installer-self-test","--ui-smoke","--pawnio-status","--performance-probe","--probe"})
            Check(!Offer(true,true,CpuTemperatureState.DriverNotInstalled,[argument]),"diagnostic path cannot install: "+argument);
        Check(Equals(Call("ClassifyExitCode",0),"Ready"),"zero installer exit code");
        Check(Equals(Call("ClassifyExitCode",3010),"RestartRequired"),"restart required is distinct from ready");
        Check(Equals(Call("ClassifyExitCode",1602),"Cancelled")&&Equals(Call("ClassifyExitCode",1223),"Cancelled"),"cancellation never treated as installed");
        Check(Equals(Call("ClassifyExitCode",5),"Failed"),"installation failure remains failure");
        Check((bool)Call("HasVerifiedPayload")!,"embedded official installer SHA256 verified");
        string verificationDirectory=Path.Combine(Path.GetTempPath(),"DevicePulse-Signature-Test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verificationDirectory);
        try
        {
            string verificationPath=Path.Combine(verificationDirectory,"PawnIO_setup.exe");
            File.WriteAllBytes(verificationPath,(byte[])Call("ReadPayload")!);
            using var readOnly=new FileStream(verificationPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            Call("VerifySignature",verificationPath); // Signature check only. Never run setup in CI.
        }
        finally{Directory.Delete(verificationDirectory,true);}
        using var dialog=(Form)Call("BuildDialog")!;
        dialog.Show();dialog.PerformLayout();
        var layout=dialog.Controls.OfType<TableLayoutPanel>().Single();layout.PerformLayout();
        var label=layout.Controls.OfType<Label>().Single();
        var buttons=layout.Controls.OfType<FlowLayoutPanel>().Single();buttons.PerformLayout();
        Check(label.Text.Contains("pilote")&&label.Text.Contains("Windows"),"French dialog explains persistent driver");
        Check(buttons.Controls.OfType<Button>().Any(b=>b.Text=="Installer")&&buttons.Controls.OfType<Button>().Any(b=>b.Text=="Plus tard"),"French install and defer choices");
        Check(dialog.AcceptButton!=null&&dialog.CancelButton!=null,"keyboard install and cancel");
        var required=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.ClientSize.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl);
        Check(required.Height<=label.ClientSize.Height,"French installation explanation fits");
        Check(buttons.Controls.OfType<Button>().All(b=>b.Left>=0&&b.Top>=0&&b.Right<=buttons.ClientSize.Width&&b.Bottom<=buttons.ClientSize.Height),"French installation buttons fit");
        using var image=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));
        image.Save(Path.Combine(AppContext.BaseDirectory,"pawnio-install-fr.png"));dialog.Close();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"installer-tests.txt"),"PASS: embedded PawnIO setup hash; consent policy; diagnostics bypass; installed/rejected driver preservation; installer exit codes; French dialog layout. Installer NOT executed; no driver installed in CI.");
    }
}
