using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BatteryDiagnostic;

internal static class PawnIoInstaller
{
    internal const string SetupSha256="1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    const string Resource="DevicePulse.PawnIO.Setup.2.2.0";
    static readonly string[] DiagnosticArguments=["--self-test","--upgrade-test","--installer-self-test","--audit-self-test","--ui-smoke","--pawnio-status","--performance-probe","--probe"];
    static int offered;

    internal static bool ShouldOffer(bool supported,bool administrator,CpuTemperatureState driver,string[] args)=>
        supported&&administrator&&driver==CpuTemperatureState.DriverNotInstalled&&!args.Any(DiagnosticArguments.Contains);

    internal static string ClassifyExitCode(int code)=>code switch
    {0=>"Ready",3010=>"RestartRequired",1602 or 1223=>"Cancelled",_=>"Failed"};

    static byte[] ReadPayload()
    {
        using var input=typeof(PawnIoInstaller).Assembly.GetManifestResourceStream(Resource)
            ??throw new InvalidOperationException("Le programme d’installation intégré est introuvable.");
        if(input.Length is <=0 or >10000000)throw new InvalidOperationException("La taille du programme d’installation est invalide.");
        using var memory=new MemoryStream();input.CopyTo(memory);
        var bytes=memory.ToArray();
        if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(SetupSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le contrôle d’intégrité de PawnIO a échoué. Installation annulée.");
        return bytes;
    }
    internal static bool HasVerifiedPayload()
    {try{_=ReadPayload();return true;}catch{return false;}}

    internal static Form BuildDialog()
    {
        const string message="Pour lire la température du processeur, DevicePulse peut installer le pilote officiel signé PawnIO 2.2.0.\n\n"+
            "Ce pilote sera installé dans Windows et restera présent après la fermeture de DevicePulse. Un redémarrage peut être nécessaire.\n\n"+
            "Le programme d’installation est inclus : aucun téléchargement n’est nécessaire. Vous pouvez choisir « Plus tard » et continuer à utiliser DevicePulse.\n\nInstaller PawnIO maintenant ?";
        var dialog=CpuActivation.BuildDialog(UiLanguage.French,message,"Installer");
        dialog.Text="DevicePulse — installation du pilote CPU";dialog.ClientSize=new Size(580,350);
        foreach(var layout in dialog.Controls.OfType<TableLayoutPanel>())
        foreach(var row in layout.Controls.OfType<FlowLayoutPanel>())
        foreach(var button in row.Controls.OfType<Button>())
            if(button.DialogResult==DialogResult.Cancel)button.Text="Plus tard";
        return dialog;
    }

    internal static async Task EnsureAvailable(Form owner)
    {
        var args=Environment.GetCommandLineArgs();
        if(args.Any(DiagnosticArguments.Contains)||!PerformanceCpuTemperature.IsAdministrator)return;
        try
        {
            var installed=await Task.Run(PerformanceCpuTemperature.CheckInstalledDriver);
            if(owner.IsDisposed||owner.Disposing)return;
            if(installed.State==CpuTemperatureState.Ready)
            {PerformanceCpuTemperature.EnableForProcess();return;}
            bool supported=PerformanceCpuTemperature.CheckProcessorSupport().State==CpuTemperatureState.Ready||LibrarySensors.IsSupportedCpu;
            if(!ShouldOffer(supported,true,installed.State,args)||Interlocked.Exchange(ref offered,1)!=0)return;
            using var dialog=BuildDialog();
            if(dialog.ShowDialog(owner)!=DialogResult.OK)return;
            owner.UseWaitCursor=true;
            int exitCode=await Task.Run(InstallVerified);
            if(owner.IsDisposed||owner.Disposing)return;
            string result=ClassifyExitCode(exitCode);
            if(result=="RestartRequired")
            {
                MessageBox.Show(owner,"PawnIO a été installé. Redémarrez Windows pour activer le pilote. DevicePulse ne redémarrera pas votre ordinateur automatiquement.",
                    "DevicePulse — redémarrage nécessaire",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            if(result=="Cancelled")return;
            if(result!="Ready")throw new InvalidOperationException("Le programme d’installation a retourné le code "+exitCode+".");
            installed=await Task.Run(PerformanceCpuTemperature.CheckInstalledDriver);
            if(owner.IsDisposed||owner.Disposing)return;
            if(installed.State==CpuTemperatureState.Ready)PerformanceCpuTemperature.EnableForProcess();
            else MessageBox.Show(owner,"L’installation est terminée, mais le pilote n’est pas encore disponible ou n’a pas pu être vérifié. Redémarrez Windows puis relancez DevicePulse.",
                "DevicePulse — pilote indisponible",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        catch(Exception ex)
        {
            try{File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"pawnio-setup.log"),DateTimeOffset.Now+" "+ex+Environment.NewLine);}catch{}
            if(!owner.IsDisposed&&!owner.Disposing)
                MessageBox.Show(owner,"L’installation de PawnIO n’a pas pu être terminée. DevicePulse reste utilisable.\n\nLes détails techniques sont enregistrés dans le fichier pawnio-setup.log, à côté de DevicePulse.exe.",
                    "DevicePulse — installation du pilote",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        finally{if(!owner.IsDisposed&&!owner.Disposing)owner.UseWaitCursor=false;}
    }

    static int InstallVerified()
    {
        byte[] bytes=ReadPayload();
        // Program Files is protected against non-elevated file replacement. Never
        // execute an elevated installer from the user's shared temporary directory.
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"DevicePulse-Setup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"PawnIO_setup.exe");
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes);
            // Keep this read-only handle open through verification and execution.
            using var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(!Convert.ToHexString(SHA256.HashData(locked)).Equals(SetupSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le fichier extrait a échoué au contrôle d’intégrité.");
            VerifySignature(path);
            var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory};
            start.ArgumentList.Add("-install");start.ArgumentList.Add("-silent");
            using var process=Process.Start(start)??throw new InvalidOperationException("Impossible de démarrer l’installation de PawnIO.");
            process.WaitForExit();return process.ExitCode;
        }
        finally{try{Directory.Delete(directory,true);}catch{}}
    }

    static void VerifySignature(string path)
    {
        string script="$ErrorActionPreference='Stop'; $s=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath '"+path.Replace("'","''")+
            "'; if($s.Status -ne 'Valid' -or !$s.SignerCertificate -or $s.SignerCertificate.Subject -notmatch 'namazso'){exit 1}; exit 0";
        string powershell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Environment.SystemDirectory};
        start.Environment["PSModulePath"]=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","Modules");
        foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("Impossible de vérifier la signature du pilote.");
        if(!process.WaitForExit(60000)){process.Kill();throw new InvalidOperationException("La vérification de la signature a expiré. Installation annulée.");}
        if(process.ExitCode!=0)throw new InvalidOperationException("La signature officielle de PawnIO n’a pas pu être vérifiée. Installation annulée.");
    }
}
