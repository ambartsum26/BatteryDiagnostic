using System.ComponentModel;
namespace BatteryDiagnostic;

internal sealed partial class MainForm : Form
{
    internal const string Version="0.9.11";
    readonly IContainer components=new Container();
    readonly IBatterySource source;
    readonly SessionMonitor monitor=new();
    readonly System.Windows.Forms.Timer pollTimer;
    readonly ContextMenuStrip languageMenu;
    readonly Dictionary<UiLanguage,ToolStripMenuItem> languageItems=[];
    readonly Button languageButton=ButtonFor(112);
    readonly ComboBox battery=new() { DropDownStyle=ComboBoxStyle.DropDownList,Width=130,Visible=false,BackColor=Theme.Card,ForeColor=Theme.Text,FlatStyle=FlatStyle.Flat,Margin=new Padding(0,7,10,0) };
    readonly Dashboard dashboard=new() { Dock=DockStyle.Fill };
    readonly Panel scroll=new() { Dock=DockStyle.Fill,AutoScroll=false,BackColor=Theme.Background };
    UiLanguage language=UiLanguage.French;
    BatteryData? data;
    bool busy,closing,changingBattery,completed;
    long readStarted;
    bool timedOut;
    string error="",batteryKey="";
    int? selectedSlot;
    internal int UpdateCount { get; private set; }
    internal UiLanguage Language=>language;
    internal BatteryData? CurrentData=>data;
    internal Dashboard View=>dashboard;
    internal ContextMenuStrip LanguageMenu=>languageMenu;
    internal Button LanguageButton=>languageButton;
    string T(string key)=>L.Get(language,key);

    internal MainForm(IBatterySource? batterySource=null)
    {
        L.ApplyCulture(language); Icon=LoadAppIcon(); source=batterySource??new WindowsBatterySource();
        Text="DevicePulse "+Version; BackColor=Theme.Background; Font=new Font("Segoe UI",10);
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi; ClientSize=new Size(1120,860); MinimumSize=new Size(1000,740); FormBorderStyle=FormBorderStyle.None; MaximizeBox=true; StartPosition=FormStartPosition.CenterScreen;
        pollTimer=new System.Windows.Forms.Timer(components) { Interval=2000 };
        pollTimer.Tick+=async(_,_)=>
        {
            if(busy)
            {
                if(!timedOut && System.Diagnostics.Stopwatch.GetElapsedTime(readStarted).TotalSeconds>=10)
                {
                    timedOut=true;monitor.Gap();completed=true;data=null;error="";UpdateBatteryList();Render();
                }
                return;
            }
            await RefreshOnce();
        };
        languageMenu=new ContextMenuStrip(components) { BackColor=Theme.Card,ForeColor=Theme.Text,Renderer=new DarkMenuRenderer(),ShowImageMargin=false,ShowCheckMargin=true,Font=new Font("Segoe UI",10) };
        foreach(var lang in Enum.GetValues<UiLanguage>())
        {
            var item=new ToolStripMenuItem(L.Name(lang)) { Tag=lang };
            item.Click+=(_,_)=>SelectLanguage(lang);
            languageItems.Add(lang,item);languageMenu.Items.Add(item);
        }
        // The menu belongs to the form. Never dispose it from Closed: ToolStrip still processes the click afterwards.
        languageButton.Click+=(_,_)=>{if(!closing)languageMenu.Show(languageButton,new Point(0,languageButton.Height));};
        battery.SelectedIndexChanged+=(_,_)=>
        {
            if(changingBattery)return;
            if(data!=null && battery.SelectedIndex>=0 && battery.SelectedIndex<data.Batteries.Count)selectedSlot=data.Batteries[battery.SelectedIndex].Slot;
            Render();
        };
        scroll.Controls.Add(dashboard);Controls.Add(scroll);SetupPerformance();SetupChrome();
        FormClosing+=(_,_)=>{closing=true;pollTimer.Stop();performanceTimer.Stop();};
        Labels();Render();
    }
    static Icon LoadAppIcon()
    {
        using var stream=typeof(MainForm).Assembly.GetManifestResourceStream("BatteryDiagnostic.Icon")!;
        using var icon=new Icon(stream);return (Icon)icon.Clone();
    }
    static Button ButtonFor(int width)=>new()
    {
        AutoSize=true,MinimumSize=new Size(width,38),FlatStyle=FlatStyle.Flat,BackColor=Theme.Card,ForeColor=Theme.Text,
        Font=new Font("Segoe UI",9.5f),Margin=new Padding(0,0,10,0),Padding=new Padding(10,4,10,4),Cursor=Cursors.Hand,UseVisualStyleBackColor=false
    };
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area=Screen.FromControl(this).WorkingArea;
        MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-16),Math.Min(MinimumSize.Height,area.Height-16));
        Size=new Size(Math.Min(Width,area.Width-16),Math.Min(Height,area.Height-16));
        Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        if(!closing)await PawnIoInstaller.EnsureAvailable(this);
        if(!closing){pollTimer.Start();StartPerformance();await RefreshOnce();}
    }
    internal void SelectLanguage(UiLanguage value)
    {
        if(closing)return;
        SuspendLayout();
        try{language=value;L.ApplyCulture(language);Labels();Render();}
        finally{ResumeLayout(true);dashboard.PerformLayout();Invalidate(true);}
    }
    void Labels()
    {
        LocalizeChrome();languageButton.Text=L.Name(language)+"  ▾";LocalizePerformance();
        languageButton.AccessibleName=T("language");battery.AccessibleName=T("selection");
        languageButton.FlatAppearance.BorderColor=Theme.Border;
        foreach(var pair in languageItems)pair.Value.Checked=pair.Key==language;
        UpdateBatteryList();
    }
    void UpdateBatteryList()
    {
        string key=L.Code(language)+":"+string.Join(",",data?.Batteries.Select(r=>r.Slot)??[]);
        if(key==batteryKey)return;
        batteryKey=key; int? previous=selectedSlot;
        changingBattery=true;
        try
        {
            battery.Items.Clear();
            if(data!=null)foreach(var r in data.Batteries)battery.Items.Add(L.F(language,"battery",r.Slot+1));
            if(battery.Items.Count>0)
            {
                int index=previous.HasValue?data!.Batteries.FindIndex(r=>r.Slot==previous):0;
                battery.SelectedIndex=Math.Max(0,index);
                selectedSlot=data!.Batteries[battery.SelectedIndex].Slot;
            }
            battery.Visible=!showPerformance&&battery.Items.Count>1;
        }
        finally{changingBattery=false;}
    }
    internal async Task RefreshOnce()
    {
        if(busy||closing)return;
        busy=true;timedOut=false;readStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var current=await source.ReadAsync();
            if(closing||IsDisposed)return;
            monitor.Add(current);data=current;error="";timedOut=false;
        }
        catch(Exception ex)
        {
            if(closing||IsDisposed)return;
            // Failed reads clear the displayed values instead of retaining a falsely live old result.
            monitor.Gap();data=null;error=ex.Message;timedOut=false;
        }
        finally
        {
            busy=false;
            if(!closing&&!IsDisposed){completed=true;UpdateCount++;UpdateBatteryList();Render();}
        }
    }
    void Render()
    {
        if(closing)return;
        var r=data?.Batteries.Count>0?data.Batteries[Math.Clamp(battery.SelectedIndex,0,data.Batteries.Count-1)]:null;
        dashboard.SetData(data,r,language,timedOut?T("timeout"):error,completed);
    }
    internal void SaveReport(string path)
    {
        if(data==null)throw new InvalidOperationException(T("unknown"));
        ReportWriter.Write(path,data,language);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){closing=true;components.Dispose();}
        base.Dispose(disposing);
    }
}
