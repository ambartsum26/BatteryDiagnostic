using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatteryDiagnostic;

/// <summary>Explicit, optional activation only. Never installs or downloads a driver.</summary>
internal static class CpuActivation
{
    internal const string OfficialDriverPage="https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0";
    internal static UiLanguage LanguageFromArguments(string[] args)
    {
        int i=Array.IndexOf(args,"--language");
        return i>=0&&i+1<args.Length?args[i+1] switch {"en"=>UiLanguage.English,"ru"=>UiLanguage.Russian,_=>UiLanguage.French}:UiLanguage.French;
    }

    internal static async Task<bool> Activate(Form owner,UiLanguage language)
    {
        string T(string key)=>PerfText.Get(language,key);
        var processor=await Task.Run(PerformanceCpuTemperature.CheckProcessorSupport);
        if(owner.IsDisposed||owner.Disposing)return false;
        if(processor.State==CpuTemperatureState.UnsupportedProcessor&&!LibrarySensors.IsSupportedAmd)
        {Show(owner,language,T("cpuUnsupported"));return false;}
        var installed=await Task.Run(PerformanceCpuTemperature.CheckInstalledDriver);
        if(owner.IsDisposed||owner.Disposing)return false;
        if(installed.State!=CpuTemperatureState.Ready)
        {
            if(Show(owner,language,T(installed.State==CpuTemperatureState.DriverRejected?"cpuDriverRejected":"cpuDriverMissing"),T("cpuOfficialPage")))
            {
                try {using var page=Process.Start(new ProcessStartInfo(OfficialDriverPage) {UseShellExecute=true});}
                catch {Show(owner,language,T("cpuBrowserError")+Environment.NewLine+OfficialDriverPage);}
            }
            return false;
        }
        if(PerformanceCpuTemperature.IsAdministrator)
        {PerformanceCpuTemperature.EnableForProcess();return false;}
        if(!Show(owner,language,T("cpuElevate"),T("cpuRestart")))return false;
        return RestartElevated(owner,language,true);
    }
    internal static bool RequestStorageAccess(Form owner,UiLanguage language)
    {
        if(!Show(owner,language,PerfText.Get(language,"diskElevate"),PerfText.Get(language,"cpuRestart")))return false;
        return RestartElevated(owner,language,PerformanceCpuTemperature.Enabled);
    }
    static bool RestartElevated(Form owner,UiLanguage language,bool enableCpu)
    {
        try
        {
            string executable=Environment.ProcessPath??throw new InvalidOperationException("Executable unavailable");
            var start=new ProcessStartInfo(executable) {UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
            if(enableCpu)start.ArgumentList.Add("--cpu-sensors");start.ArgumentList.Add("--language");start.ArgumentList.Add(L.Code(language));
            using var process=Process.Start(start);
            return process!=null;
        }
        catch(Win32Exception ex) when(ex.NativeErrorCode==1223){return false;}
        catch {Show(owner,language,PerfText.Get(language,"cpuRestartError"));return false;}
    }

    static bool Show(Form owner,UiLanguage language,string message,string? accept=null)
    {
        using var dialog=BuildDialog(language,message,accept);
        return dialog.ShowDialog(owner)==DialogResult.OK;
    }
    internal static Form BuildDialog(UiLanguage language,string message,string? accept=null)
    {
        string T(string key)=>PerfText.Get(language,key);
        var dialog=new Form {Text=T("sensorAccess"),BackColor=Theme.Background,ForeColor=Theme.Text,
            Font=new Font("Segoe UI",10),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,
            ShowInTaskbar=false,StartPosition=FormStartPosition.CenterParent,AutoScaleDimensions=new SizeF(96,96),AutoScaleMode=AutoScaleMode.Dpi,ClientSize=new Size(540,310)};
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=2};
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        var label=new Label {Text=message,Dock=DockStyle.Fill,AutoSize=false,ForeColor=Theme.Text};
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty};
        Button Make(string text,DialogResult result)=>new() {Text=text,DialogResult=result,AutoSize=true,MinimumSize=new Size(112,34),
            FlatStyle=FlatStyle.Flat,BackColor=Theme.Card,ForeColor=Theme.Text,Margin=new Padding(10,6,0,0),Padding=new Padding(10,0,10,0)};
        var close=Make(T(accept==null?"cpuClose":"cpuCancel"),DialogResult.Cancel);buttons.Controls.Add(close);dialog.CancelButton=close;
        if(accept!=null){var next=Make(accept,DialogResult.OK);buttons.Controls.Add(next);dialog.AcceptButton=next;}else dialog.AcceptButton=close;
        layout.Controls.Add(label,0,0);layout.Controls.Add(buttons,0,1);dialog.Controls.Add(layout);
        dialog.HandleCreated+=(_,_)=>
        {
            int dark=1;
            try {if(DwmSetWindowAttribute(dialog.Handle,20,ref dark,4)!=0)_=DwmSetWindowAttribute(dialog.Handle,19,ref dark,4);}catch { }
        };
        return dialog;
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    internal static void SelfTest()
    {
        if(LanguageFromArguments([])!=UiLanguage.French||LanguageFromArguments(["--language","en"])!=UiLanguage.English||
            LanguageFromArguments(["--language","ru"])!=UiLanguage.Russian||LanguageFromArguments(["--language"])!=UiLanguage.French)
            throw new Exception("CPU activation language arguments");
    }
}
