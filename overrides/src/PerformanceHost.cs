namespace BatteryDiagnostic;

internal sealed partial class MainForm
{
    readonly PerformanceDashboard performanceView=new() { Dock=DockStyle.Fill };
    readonly Button performanceTab=TabButton(),batteryTab=TabButton();
    System.Windows.Forms.Timer performanceTimer=null!;
    PerformanceInventory? inventory;
    PerformanceLive? livePerformance;
    StorageSnapshot? storage;
    bool showPerformance=true,performanceBusy,inventoryBusy,storageBusy,cpuActivationBusy;
    DateTimeOffset inventoryAttempt=DateTimeOffset.MinValue,storageAttempt=DateTimeOffset.MinValue;
    long performanceStarted;
    internal int PerformanceUpdateCount { get; private set; }
    internal PerformanceDashboard PerformanceView=>performanceView;
    internal bool IsPerformanceSelected=>showPerformance;
    internal StorageSnapshot? CurrentStorage=>storage;

    static Button TabButton()=>new() { Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat,BackColor=Theme.Header,ForeColor=Theme.Muted,Font=new Font("Segoe UI Semibold",10,FontStyle.Regular),Margin=Padding.Empty,Cursor=Cursors.Hand,UseVisualStyleBackColor=false };
    void SetupPerformance()
    {
        performanceView.StorageDetailsRequested+=(_,_)=>
        {
            if(closing||performanceView.SelectedDiskIdentity is not {} identity)return;
            using var details=new StorageDetailsForm(()=>storage,identity,language);details.ShowDialog(this);
        };
        performanceView.CpuActivationRequested+=async(_,_)=>
        {
            if(closing||cpuActivationBusy)return;
            cpuActivationBusy=true;
            try {if(await CpuActivation.Activate(this,language)&&!closing)Close();}
            finally {cpuActivationBusy=false;}
        };
        performanceView.StorageAccessRequested+=(_,_)=>
        {
            if(closing||cpuActivationBusy)return;
            cpuActivationBusy=true;
            try {if(CpuActivation.RequestStorageAccess(this,language)&&!closing)Close();}
            finally {cpuActivationBusy=false;}
        };
        performanceTimer=new System.Windows.Forms.Timer(components) {Interval=2000};
        performanceTimer.Tick+=async(_,_)=>
        {
            if(inventoryBusy&&(DateTimeOffset.UtcNow-inventoryAttempt).TotalSeconds>30)inventory=null;
            if(performanceBusy)
            {
                if(System.Diagnostics.Stopwatch.GetElapsedTime(performanceStarted).TotalSeconds>8)
                {livePerformance=null;performanceView.SetData(inventory,null,storage,language);}
                return;
            }
            await RefreshPerformanceOnce();
        };
        var tabs=new TableLayoutPanel {Dock=DockStyle.Top,Height=40,ColumnCount=2,RowCount=1,BackColor=Theme.Header,Margin=Padding.Empty,Padding=Padding.Empty};
        tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));tabs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        performanceTab.FlatAppearance.BorderSize=batteryTab.FlatAppearance.BorderSize=0;
        tabs.Controls.Add(performanceTab,0,0);tabs.Controls.Add(batteryTab,1,0);
        performanceTab.Click+=(_,_)=>SelectPage(true);batteryTab.Click+=(_,_)=>SelectPage(false);
        scroll.Controls.Add(performanceView);Controls.Add(tabs);
        SelectPage(true);
    }
    internal void SelectPage(bool performance)
    {
        showPerformance=performance;dashboard.Visible=!performance;performanceView.Visible=performance;
        if(performance)performanceView.BringToFront();else dashboard.BringToFront();
        battery.Visible=!performance&&battery.Items.Count>1;
        performanceTab.ForeColor=performance?Theme.Accent:Theme.Muted;batteryTab.ForeColor=performance?Theme.Muted:Theme.Accent;
        performanceTab.BackColor=Theme.Header;batteryTab.BackColor=Theme.Header;
        performanceTab.AccessibleDescription=performance?PerfText.Get(language,"selected"):"";batteryTab.AccessibleDescription=performance?"":PerfText.Get(language,"selected");
    }
    void LocalizePerformance()
    {
        performanceTab.Text=PerfText.Get(language,"heading");batteryTab.Text=PerfText.Get(language,"batteryTab");
        performanceView.SetData(inventory,livePerformance,storage,language);SelectPage(showPerformance);
    }
    void StartPerformance()
    {
        performanceTimer.Start();_ = RefreshPerformanceOnce();
    }
    internal async Task RefreshPerformanceOnce()
    {
        if(closing||performanceBusy)return;
        performanceBusy=true;performanceStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        // Static inventory and disk providers have independent workers. A slow provider
        // cannot prevent native CPU/RAM sampling or the first paint of the window.
        if(!inventoryBusy&&(DateTimeOffset.UtcNow-inventoryAttempt).TotalSeconds>=60)_=RefreshInventory();
        if(!storageBusy&&(DateTimeOffset.UtcNow-storageAttempt).TotalSeconds>=15)_=RefreshStorage();
        try
        {
            var sample=await Task.Run(PerformanceSensors.ReadLive);
            if(closing||IsDisposed)return;
            livePerformance=sample;PerformanceUpdateCount++;performanceView.AddSample(sample);
        }
        catch {livePerformance=null;}
        finally
        {
            performanceBusy=false;
            if(!closing&&!IsDisposed)performanceView.SetData(inventory,livePerformance,storage,language);
        }
    }
    async Task RefreshInventory()
    {
        inventoryBusy=true;inventoryAttempt=DateTimeOffset.UtcNow;
        try {var value=await Task.Run(PerformanceSensors.ReadInventory);if(!closing&&!IsDisposed)inventory=value;}
        catch {inventory=null;}
        finally {inventoryBusy=false;if(!closing&&!IsDisposed)performanceView.SetData(inventory,livePerformance,storage,language);}
    }
    async Task RefreshStorage()
    {
        storageBusy=true;storageAttempt=DateTimeOffset.UtcNow;
        try {var value=await Task.Run(PerformanceStorage.Read);if(!closing&&!IsDisposed)storage=value;}
        catch {storage=null;}
        finally {storageBusy=false;if(!closing&&!IsDisposed)performanceView.SetData(inventory,livePerformance,storage,language);}
    }
}
