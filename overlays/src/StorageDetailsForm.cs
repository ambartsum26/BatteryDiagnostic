using System.Runtime.InteropServices;

namespace BatteryDiagnostic;

/// <summary>Read-only presentation of the collector's actual payload; no device commands.</summary>
internal sealed class StorageDetailsForm : Form
{
    readonly Func<StorageSnapshot?> read;
    readonly string identity;
    readonly UiLanguage language;
    readonly Label description=new(){Dock=DockStyle.Fill,ForeColor=Theme.Muted,AutoSize=false,Margin=new Padding(0,0,0,8)};
    readonly DataGridView table;
    readonly TextBox diagnostics=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Theme.Background,ForeColor=Theme.Muted,BorderStyle=BorderStyle.FixedSingle};
    readonly System.Windows.Forms.Timer refresh=new(){Interval=2000};
    string revision="";
    string T(string key)=>PerfText.Get(language,key);
    string N(object? value)=>Convert.ToString(value,L.Culture(language))??T("unknown");
    internal StorageDetailsForm(Func<StorageSnapshot?> source,string deviceId,UiLanguage lang)
    {
        read=source;identity=deviceId;language=lang;
        Text=T("smartDetails");Font=new Font("Segoe UI",9);BackColor=Theme.Background;ForeColor=Theme.Text;
        FormBorderStyle=FormBorderStyle.None;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        StartPosition=FormStartPosition.CenterParent;AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(820,550);
        table=new DataGridView {Dock=DockStyle.Fill,ReadOnly=true,ShowCellToolTips=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            AllowUserToResizeRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor=Theme.Card,BorderStyle=BorderStyle.None,GridColor=Theme.Border,EnableHeadersVisualStyles=false,
            ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.Single,CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,ClipboardCopyMode=DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText};
        table.DefaultCellStyle=new DataGridViewCellStyle {BackColor=Theme.Card,ForeColor=Theme.Text,SelectionBackColor=Theme.Border,SelectionForeColor=Theme.Text,NullValue=T("unknown")};
        table.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle {BackColor=Theme.Header,ForeColor=Theme.Text,SelectionBackColor=Theme.Header,Font=new Font(Font,FontStyle.Bold)};
        foreach(var (id,caption,weight) in new[]{("id","ID",35f),("name",T("attribute"),180f),("current",T("current"),65f),("worst",T("worst"),65f),("threshold",T("threshold"),65f),("raw",T("raw"),120f)})
            table.Columns.Add(new DataGridViewTextBoxColumn{Name=id,HeaderText=caption,FillWeight=weight,SortMode=DataGridViewColumnSortMode.NotSortable});
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),RowCount=5,ColumnCount=1};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,122));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,96));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
        layout.Controls.Add(description,0,0);layout.Controls.Add(table,0,1);
        layout.Controls.Add(diagnostics,0,2);
        layout.Controls.Add(new Label{Text=T("detailsNote"),Dock=DockStyle.Fill,ForeColor=Theme.Muted,AutoSize=false,Margin=new Padding(0,7,0,0)},0,3);
        var close=new Button{Text=T("cpuClose"),Dock=DockStyle.Right,Width=120,DialogResult=DialogResult.Cancel,FlatStyle=FlatStyle.Flat,BackColor=Theme.Card,ForeColor=Theme.Text};
        var actions=new Panel{Dock=DockStyle.Fill};
        var copy=new Button{Text=T("copyDiagnostics"),Dock=DockStyle.Left,Width=240,FlatStyle=FlatStyle.Flat,BackColor=Theme.Card,ForeColor=Theme.Text};
        copy.Click+=(_,_)=>{try{Clipboard.SetText(diagnostics.Text);}catch(ExternalException){MessageBox.Show(this,T("copyFailed"),Text);}};
        actions.Controls.Add(close);actions.Controls.Add(copy);
        layout.Controls.Add(actions,0,4);Controls.Add(layout);AcceptButton=CancelButton=close;
        var captionBar=new Panel {Dock=DockStyle.Top,Height=36,BackColor=Theme.Header};
        var captionText=new Label {Text="SMART",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(14,0,0,0),ForeColor=Theme.Text};
        var captionClose=new CaptionButton(true){Dock=DockStyle.Right,AccessibleName=T("cpuClose")};captionClose.Click+=(_,_)=>Close();
        captionBar.Controls.Add(captionText);captionBar.Controls.Add(captionClose);Controls.Add(captionBar);
        MouseEventHandler drag=(_,e)=>{if(e.Button!=MouseButtons.Left||e.Clicks>1)return;ReleaseCapture();SendMessage(Handle,0xA1,(IntPtr)2,IntPtr.Zero);};
        captionBar.MouseDown+=drag;captionText.MouseDown+=drag;
        refresh.Tick+=(_,_)=>Render();Shown+=(_,_)=>{Render();refresh.Start();};
    }
    internal void Render()
    {
        var snapshot=read();var disk=snapshot?.Disks.FirstOrDefault(d=>d.DeviceId==identity);
        bool fresh=snapshot is not null&&(DateTimeOffset.Now-snapshot.Timestamp).Duration().TotalSeconds<=45;
        string key=snapshot?.Timestamp.ToString("O")+":"+(disk?.DeviceId??"")+":"+fresh;
        if(key==revision)return;revision=key;
        table.Rows.Clear();
        diagnostics.Text="Battery Diagnostic "+Application.ProductVersion+Environment.NewLine+
            (disk?.Model??T("unknown"))+Environment.NewLine+
            "Administrator: "+PerformanceCpuTemperature.IsAdministrator+Environment.NewLine+
            string.Join(Environment.NewLine,(snapshot?.Diagnostics??[]).Concat(disk?.Diagnostics??[]));
        string Available(string? value)=>string.IsNullOrEmpty(value)||value=="None"?T("unknown"):value;
        description.Text=(disk?.Model??T("unknown"))+Environment.NewLine+T("readingTime")+": "+(snapshot?.Timestamp.ToString("T",L.Culture(language))??T("unknown"))+Environment.NewLine+
            "CrystalDiskInfo: "+T(LibCdiStorage.StatusKey(snapshot,disk,fresh))+Environment.NewLine+T("basis")+": "+Available(disk?.HealthSource)+Environment.NewLine+T("profile")+": "+Available(disk?.AtaProfile)+" · "+T("transport")+": "+Available(disk?.AtaSmartSource);
        if(disk is null||!fresh){table.Rows.Add("—",T(fresh?"unknown":"storageStale"),"—","—","—","—");return;}
        foreach(var a in disk.SmartAttributes)table.Rows.Add(a.Id.ToString("X2"),"SMART "+a.Id.ToString("X2"),N(a.Current),N(a.Worst),a.Threshold.HasValue?N(a.Threshold):"—",a.Raw.ToString("X12"));
        void Add(string label,object? value,string unit=""){if(value!=null)table.Rows.Add("—",label,"—","—","—",N(value)+unit);}
        Add(T("nvmeFlags"),disk.NvmeCriticalWarning.HasValue?"0x"+disk.NvmeCriticalWarning.Value.ToString("X2"):null);
        Add(T("spare"),disk.NvmeAvailableSparePercent," %");Add(T("threshold"),disk.NvmeSpareThresholdPercent," %");
        Add(T("healthPercent"),DiskAssessmentEngine.HealthPercentage(disk)," %");Add(T("temperature"),disk.TemperatureC," °C");
        for(int i=0;i<disk.NvmeSensorTemperaturesC.Count;i++)Add(T("temperature")+" #"+(i+1),disk.NvmeSensorTemperaturesC[i]," °C");
        Add(T("hours"),disk.PowerOnHours);Add(T("powerCycles"),disk.PowerOnCount);
        // The original CDI display labels these figures GB, but uses GiB
        // (512-byte sectors divided by 2^30). Label our units precisely.
        Add(T("hostReads"),disk.HostReadsBytes.HasValue ? Math.Floor(disk.HostReadsBytes.Value/1073741824d) : null," GiB");
        Add(T("hostWrites"),disk.HostWritesBytes.HasValue ? Math.Floor(disk.HostWritesBytes.Value/1073741824d) : null," GiB");
        Add(T("mediaErrors"),disk.NvmeMediaErrors);
        if(table.Rows.Count==0)table.Rows.Add("—",T("unknown"),"—","—","—","—");
        if(disk.Diagnostics.Any(d=>d.Contains("AccessDenied",StringComparison.Ordinal)))description.Text+=Environment.NewLine+T("accessLimited");
    }
    internal int ReadingRows=>table.Rows.Count;
    protected override void Dispose(bool disposing){if(disposing)refresh.Dispose();base.Dispose(disposing);}
    [DllImport("user32.dll")]static extern bool ReleaseCapture();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
}
