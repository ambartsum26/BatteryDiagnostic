using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BatteryDiagnostic;

internal sealed class PerformanceDashboard : UserControl
{
    readonly ComboBox modules=Selector(),gpus=Selector(),disks=Selector(),volumes=Selector();
    readonly LinkLabel cpuActivate=AccessLink("cpu"),diskAccess=AccessLink("diskAccess"),smartDetails=AccessLink("smartDetails");
    readonly ToolTip tips=new() {AutoPopDelay=20000,InitialDelay=450,ReshowDelay=250};
    readonly Queue<(DateTimeOffset Time,double? Usage)> history=[];
    readonly System.Windows.Forms.Timer freshnessTimer=new() {Interval=1000};
    PerformanceLive? FreshLive=>live!=null&&(DateTimeOffset.Now-live.Timestamp).TotalSeconds is >=0 and <=10?live:null;
    PerformanceInventory? inventory;
    PerformanceLive? live;
    StorageSnapshot? storage;
    UiLanguage language=UiLanguage.French;
    bool updating;
    string moduleKey="",gpuKey="",diskKey="",volumeKey="",tipKey="";
    float viewScale=1,viewX,viewY;
    string T(string key)=>PerfText.Get(language,key);
    string Unknown=>T("unknown");
    string N(double value,string format="0.#")=>value.ToString(format,L.Culture(language));
    string Memory(ulong? bytes)=>bytes.HasValue?N(bytes.Value/1073741824d)+" "+T("gib"):Unknown;
    string DiskBytes(ulong? bytes)=>bytes.HasValue?N(bytes.Value/1000000000d)+" "+T("gb"):Unknown;
    string Percent(double? value)=>value.HasValue&&double.IsFinite(value.Value)?L.Percent(language,value.Value):Unknown;
    string Number(object? value)=>value==null?Unknown:Convert.ToString(value,L.Culture(language))??Unknown;
    StorageDisk? SelectedDisk=>storage?.Disks.ElementAtOrDefault(disks.SelectedIndex);
    StorageVolume? SelectedVolume=>SelectedDisk?.Volumes.ElementAtOrDefault(volumes.SelectedIndex);
    MemoryModule? SelectedModule=>inventory?.Memory.Modules.ElementAtOrDefault(modules.SelectedIndex);
    GpuInventory? SelectedGpu=>inventory?.Gpus.ElementAtOrDefault(gpus.SelectedIndex);
    internal ulong? InstalledRamBytes
    {
        get
        {
            var all=inventory?.Memory.Modules;
            if(all is null||all.Count==0||all.Any(m=>m.CapacityBytes is null or 0))return null;
            ulong sum=0;
            foreach(var module in all){if(ulong.MaxValue-sum<module.CapacityBytes!.Value)return null;sum+=module.CapacityBytes.Value;}
            return sum;
        }
    }
    internal string CdiStatus=>T(LibCdiStorage.StatusKey(storage,SelectedDisk,StorageFresh));
    internal bool HasLiveSample=>live!=null;
    internal int SampleCount=>history.Count;
    internal string CurrentDiskCondition=>Health(SelectedDisk);
    internal string CurrentDiskResource=>Resource(SelectedDisk);
    internal IReadOnlyList<ComboBox> Selectors=>[modules,gpus,disks,volumes];
    internal event EventHandler? CpuActivationRequested;
    internal event EventHandler? StorageAccessRequested;
    internal event EventHandler? StorageDetailsRequested;
    internal string? SelectedDiskIdentity=>SelectedDisk?.DeviceId;
    static LinkLabel AccessLink(string tag)=>new() {Tag=tag,TextAlign=ContentAlignment.MiddleRight,LinkColor=Theme.Accent,ActiveLinkColor=Theme.Text,VisitedLinkColor=Theme.Accent,BackColor=Theme.Card,AutoSize=false,TabStop=true,Font=new Font("Segoe UI",8.5f)};
    static ComboBox Selector()
    {
        var combo=new DarkSelector {DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,BackColor=Theme.Header,ForeColor=Theme.Text,Font=new Font("Segoe UI",9),IntegralHeight=true,DropDownHeight=250};
        combo.DrawItem+=(_,e)=>
        {
            using var bg=new SolidBrush((e.State&DrawItemState.Selected)!=0&&((e.State&DrawItemState.ComboBoxEdit)==0)?Theme.Border:Theme.Header);
            e.Graphics.FillRectangle(bg,e.Bounds);
            string value=e.Index>=0&&e.Index<combo.Items.Count?combo.Items[e.Index]?.ToString()??"":"";
            TextRenderer.DrawText(e.Graphics,value,combo.Font,e.Bounds,combo.Enabled?Theme.Text:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.LeftAndRightPadding);
        };
        return combo;
    }

    internal PerformanceDashboard()
    {
        DoubleBuffered=true;ResizeRedraw=true;BackColor=Theme.Background;AutoScroll=false;
        freshnessTimer.Tick+=(_,_)=>Invalidate();freshnessTimer.Start();
        Controls.AddRange([modules,gpus,disks,volumes,cpuActivate,diskAccess,smartDetails]);
        cpuActivate.LinkClicked+=(_,_)=>CpuActivationRequested?.Invoke(this,EventArgs.Empty);
        diskAccess.LinkClicked+=(_,_)=>StorageAccessRequested?.Invoke(this,EventArgs.Empty);
        smartDetails.LinkClicked+=(_,_)=>StorageDetailsRequested?.Invoke(this,EventArgs.Empty);
        foreach(var c in new[]{modules,gpus,disks,volumes})c.SelectedIndexChanged+=(_,_)=>
        {
            if(updating)return;
            if(c==disks)UpdateVolumes();
            UpdateTips();Invalidate();
        };
    }
    internal void AddSample(PerformanceLive sample)
    {
        history.Enqueue((sample.Timestamp,sample.CpuUsagePercent));while(history.Count>60)history.Dequeue();
    }
    internal void SetData(PerformanceInventory? inv,PerformanceLive? current,StorageSnapshot? drives,UiLanguage lang)
    {
        bool changedLanguage=language!=lang;
        inventory=inv;live=current;storage=drives;language=lang;
        if(changedLanguage){tipKey="";tips.SetToolTip(this,null);}
        updating=true;
        try
        {
            Populate(modules,ref moduleKey,inv?.Memory.Modules.Select((m,i)=>(m.Slot??(T("module")+" "+(i+1)))+" · "+Memory(m.CapacityBytes)).ToArray()??[],inv?.Memory.Modules.Select((m,i)=>(m.Slot??i.ToString())+":"+m.PartNumber).ToArray()??[]);
            Populate(gpus,ref gpuKey,inv?.Gpus.Select(g=>g.Model??Unknown).ToArray()??[],inv?.Gpus.Select(g=>g.Id).ToArray()??[]);
            Populate(disks,ref diskKey,drives?.Disks.Select(d=>(d.Index.HasValue?PerfText.F(language,"diskNumber",d.Index.Value)+" · ":"")+(string.IsNullOrWhiteSpace(d.Model)?Unknown:d.Model)).ToArray()??[],drives?.Disks.Select(d=>d.DeviceId).ToArray()??[]);
            UpdateVolumes();
        }
        finally {updating=false;}
        modules.AccessibleName=T("module");gpus.AccessibleName=T("gpu");disks.AccessibleName=T("disk");volumes.AccessibleName=T("volume");
        cpuActivate.Text=T("cpuActivate");cpuActivate.AccessibleName=T("cpuActivate");
        cpuActivate.Visible=false;
        diskAccess.Text=T("diskAccess");diskAccess.AccessibleName=T("diskAccess");
        smartDetails.Text=T("smartDetails");smartDetails.AccessibleName=T("smartDetails");smartDetails.Visible=SelectedDisk!=null;
        diskAccess.Visible=!PerformanceCpuTemperature.IsAdministrator&&(drives?.Diagnostics.Concat(drives.Disks.SelectMany(d=>d.Diagnostics)).Any(d=>d.Contains("AccessDenied",StringComparison.Ordinal))??false);
        AccessibleName=T("heading");
        AccessibleDescription=T("cpu")+": "+(inv?.Cpu.Model??Unknown)+"; "+T("ram")+": "+Memory(current?.Memory.TotalBytes)+"; "+T("notHealthTest");
        UpdateTips();Invalidate();
    }
    void Populate(ComboBox combo,ref string key,string[] values,string[] identities)
    {
        string next=L.Code(language)+":"+string.Join("\n",values)+":"+string.Join("\n",identities);
        if(next==key)return;
        key=next;string? previous=(combo.Tag as string[])?.ElementAtOrDefault(combo.SelectedIndex);
        int selected=previous==null?0:Array.IndexOf(identities,previous);combo.Tag=identities;
        combo.BeginUpdate();
        try {combo.Items.Clear();combo.Items.AddRange(values.Length>0?values:[Unknown]);combo.SelectedIndex=Math.Clamp(selected,0,combo.Items.Count-1);combo.Enabled=values.Length>0;}
        finally {combo.EndUpdate();}
    }
    void UpdateVolumes()
    {
        var disk=SelectedDisk;
        Populate(volumes,ref volumeKey,disk?.Volumes.Select(v=>v.Name+" · "+(v.FileSystem??Unknown)+(v.SharedAcrossDisks?" *":"")).ToArray()??[],disk?.Volumes.Select(v=>disk.DeviceId+":"+v.Name).ToArray()??[]);
    }
    bool StorageFresh=>storage!=null&&(DateTimeOffset.Now-storage.Timestamp).Duration().TotalSeconds<=45;
    string Health(StorageDisk? disk)
    {
        if(disk==null)return Unknown;
        if(!StorageFresh)return T("storageStale");
        return T(DiskAssessmentEngine.Evaluate(disk).StateKey);
    }
    string Resource(StorageDisk? disk)
    {
        if(disk is null)return Unknown;
        if(!StorageFresh)return T("storageStale");
        var assessment=DiskAssessmentEngine.Evaluate(disk);
        if(assessment.Remaining is not {} value)return Unknown;
        return L.Percent(language,value)+" · "+T(assessment.Resource switch {DiskResourceGrade.Normal=>"lifeNormal",DiskResourceGrade.Low=>"lifeLow",_=>"lifeExhausted"});
    }
    string DiskExplanation()
    {
        var d=StorageFresh?SelectedDisk:null;
        if(d==null)return T("health")+": "+Health(SelectedDisk)+Environment.NewLine+T("healthTip");
        string source=T(d.HealthSource switch {"WindowsStorage"=>"sourceStorage","NvmeHealthLog"=>"sourceNvme","LibCdi"=>"sourceCdi","FailurePrediction"=>"sourcePrediction","AtaSmartThresholds"=>"sourceAta","AtaSmartSectors"=>"sourceSectors",_=>"unknown"});
        string summary=PerfText.F(language,"diskSummary",d.TemperatureC is {} temperature?N(temperature)+" °C":Unknown,Number(d.PowerOnCount),Number(d.PowerOnHours));
        var lines=new List<string> {d.Model,"CrystalDiskInfo: "+CdiStatus,T("health")+": "+Health(d),summary,T("basis")+": "+source};
        if(d.NvmeAvailableSparePercent.HasValue)lines.Add(PerfText.F(language,"nvmeSpare",Number(d.NvmeAvailableSparePercent),Number(d.NvmeSpareThresholdPercent)));
        lines.Add(T("mediaErrors")+": "+Number(d.NvmeMediaErrors));
        if(d.NvmeCriticalWarning.HasValue)lines.Add(T("nvmeFlags")+": 0x"+d.NvmeCriticalWarning.Value.ToString("X2"));
        var sensorLines=d.NvmeSensorTemperaturesC.Select((value,i)=>value.HasValue?"#"+(i+1)+": "+N(value.Value)+" °C":null).Where(s=>s!=null);
        if(sensorLines.Any())lines.Add(T("temperature")+": "+string.Join("; ",sensorLines));
        if(d.RemainingLifePercent.HasValue)lines.Add(T("lifeTip"));
        if(d.RemainingLifeSource!="None")lines.Add(T("resourceSource")+": "+d.RemainingLifeSource);
        if(d.TemperatureSource!="None")lines.Add(T("temperature")+": "+d.TemperatureSource);
        if(d.PowerOnCount is {} cycles)lines.Add(T("powerCycles")+": "+N(cycles));
        if(d.HostReadsBytes is {} readBytes)lines.Add(T("hostReads")+": "+N(Math.Floor(readBytes/1073741824d))+" GiB");
        if(d.HostWritesBytes is {} writtenBytes)lines.Add(T("hostWrites")+": "+N(Math.Floor(writtenBytes/1073741824d))+" GiB");
        if(d.FailurePredicted==false)lines.Add(T("noPrediction"));
        if(d.HealthCode=="NoFailurePredicted")lines.Add(T("limitedSmart"));
        if(d.SmartAttributes.Count>0)lines.Add(PerfText.F(language,"smartCount",d.SmartAttributes.Count));
        if(d.Diagnostics.Any(code=>code.Contains("AccessDenied",StringComparison.Ordinal)))lines.Add(T("accessLimited"));
        if(d.MediaType=="NonRotating")lines.Add(T("seekBasis"));
        lines.Add(T("healthTip"));return string.Join(Environment.NewLine,lines);
    }
    void UpdateTips()
    {
        tips.SetToolTip(cpuActivate,CpuExplanation());
        tips.SetToolTip(diskAccess,T("diskElevate"));
        tips.SetToolTip(modules,string.Join(Environment.NewLine,new[]{T("installedRam")+": "+Memory(InstalledRamBytes),T("usableRam")+": "+Memory(FreshLive?.Memory.TotalBytes),SelectedModule?.Slot,MemoryManufacturer.Display(SelectedModule?.Manufacturer),SelectedModule?.Manufacturer,SelectedModule?.PartNumber,T("ramTip")}.Where(s=>!string.IsNullOrEmpty(s))));
        tips.SetToolTip(gpus,(SelectedGpu?.Model??Unknown)+Environment.NewLine+T("gpuTip")+Environment.NewLine+T("gpuLoadBasis"));
        tips.SetToolTip(disks,DiskExplanation());
        tips.SetToolTip(volumes,SelectedVolume?.SharedAcrossDisks==true?T("sharedVolume")+Environment.NewLine+T("diskTip"):T("diskTip"));
    }
    string CpuExplanation()
    {
        if(FreshLive?.CpuTemperatureC is {} temp)
        {
            string source=live.Notes.FirstOrDefault(n=>n.StartsWith("CPU temperature source: ",StringComparison.Ordinal))
                ??"CPU temperature source: Unavailable";
            return T("temperature")+": "+N(temp)+" °C"+Environment.NewLine+source;
        }
        string key=PerformanceCpuTemperature.ReadCached().State switch
        {
            CpuTemperatureState.Disabled=>"cpuDisabled",CpuTemperatureState.Starting=>"cpuStarting",
            CpuTemperatureState.AdministratorRequired=>"cpuElevate",CpuTemperatureState.DriverNotInstalled=>"cpuDriverMissing",
            CpuTemperatureState.DriverRejected=>"cpuDriverRejected",CpuTemperatureState.UnsupportedProcessor=>"cpuUnsupported",
            CpuTemperatureState.Ready=>"cpuReady",_=>"cpuUnavailable"
        };
        return T("cpuTip")+Environment.NewLine+T(key);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);float x=(e.X-viewX)/viewScale,y=(e.Y-viewY)/viewScale;
        string next=y is >=68 and <=296?(x<500?"cpuTip":"ramTip"):y is >=306 and <=672?(x<500?"gpuTip":"diskTip"):"";
        if(next!=tipKey){tipKey=next;tips.SetToolTip(this,next=="diskTip"?DiskExplanation():next=="cpuTip"?CpuExplanation():next.Length>0?T(next):null);}
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);PlaceSelectors();}
    void PlaceSelectors()
    {
        if(modules==null||Width<=0||Height<=0)return;
        viewScale=Math.Max(.1f,Math.Min(Width/1000f,Height/710f));viewX=(Width-1000*viewScale)/2;viewY=(Height-710*viewScale)/2;
        Place(modules,520,210,446);Place(gpus,32,343,449);Place(disks,519,343,449);Place(volumes,519,551,449);
        cpuActivate.SetBounds((int)(viewX+335*viewScale),(int)(viewY+80*viewScale),(int)(146*viewScale),(int)(22*viewScale));
        diskAccess.SetBounds((int)(viewX+815*viewScale),(int)(viewY+318*viewScale),(int)(152*viewScale),(int)(22*viewScale));
        smartDetails.SetBounds((int)(viewX+714*viewScale),(int)(viewY+318*viewScale),(int)(90*viewScale),(int)(22*viewScale));
        float size=Math.Max(7.5f,8.5f*viewScale*96/DeviceDpi);
        if(Math.Abs(cpuActivate.Font.Size-size)>.1){var old=cpuActivate.Font;cpuActivate.Font=new Font("Segoe UI",size);old.Dispose();}
        if(Math.Abs(diskAccess.Font.Size-size)>.1){var old=diskAccess.Font;diskAccess.Font=new Font("Segoe UI",size);old.Dispose();}
        if(Math.Abs(smartDetails.Font.Size-size)>.1){var old=smartDetails.Font;smartDetails.Font=new Font("Segoe UI",size);old.Dispose();}
    }
    void Place(ComboBox combo,int x,int y,int width)
    {
        float fontSize=Math.Max(7.5f,9*viewScale*96/DeviceDpi);
        if(Math.Abs(combo.Font.Size-fontSize)>.1){var old=combo.Font;combo.Font=new Font("Segoe UI",fontSize);old.Dispose();}
        combo.ItemHeight=Math.Max(14,(int)(19*viewScale));
        combo.SetBounds((int)(viewX+x*viewScale),(int)(viewY+y*viewScale),(int)(width*viewScale),(int)(24*viewScale));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.Clear(Theme.Background);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
        g.TranslateTransform(viewX,viewY);g.ScaleTransform(viewScale,viewScale);
        DrawText(g,T("heading"),18,10,960,28,20,true);DrawText(g,live!=null&&FreshLive==null?T("liveStale"):T("subtitle"),18,42,960,20,12,false,live!=null&&FreshLive==null?Theme.Warning:Theme.Muted);
        Card(g,18,68,477,228,T("cpu"));Card(g,505,68,477,228,T("ram"));Card(g,18,306,477,366,T("gpu"));Card(g,505,306,477,366,T("disk"));
        DrawCpu(g);DrawMemory(g);DrawGpu(g);DrawStorage(g);
        DrawText(g,T("notHealthTest"),18,683,964,24,12,false,Theme.Muted);
    }
    void Card(Graphics g,float x,float y,float w,float h,string name)
    {
        using var path=Theme.Round(new RectangleF(x,y,w,h),8);using var bg=new SolidBrush(Theme.Card);using var border=new Pen(Theme.Border);
        g.FillPath(bg,path);g.DrawPath(border,path);DrawText(g,name,x+14,y+12,w-28,21,12,true);
    }
    static void DrawText(Graphics g,string text,float x,float y,float w,float h,float size=10,bool bold=false,Color? color=null)
    {
        using var font=new Font("Segoe UI",Math.Max(12,size), bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(color??Theme.Text);using var format=new StringFormat {Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.LineLimit};
        g.DrawString(text,font,brush,new RectangleF(x,y,w,h),format);
    }
    void Stat(Graphics g,string label,string value,float x,float y,float w=86,float valueSize=16,Color? valueColor=null)
    {DrawText(g,label,x,y,w,28,10,false,Theme.Muted);DrawText(g,value,x,y+29,w,34,valueSize,true,valueColor);}
    void DrawCpu(Graphics g)
    {
        DrawText(g,inventory?.Cpu.Model??Unknown,32,105,449,34,12,true);
        string? source=FreshLive?.Notes.FirstOrDefault(n=>n.StartsWith("CPU temperature source: ",StringComparison.Ordinal));
        if(FreshLive?.CpuTemperatureC.HasValue==true && source is not null)
            DrawText(g,source[24..],321,83,160,17,10,false,Theme.Accent);
        var rect=new RectangleF(32,151,252,111);using var pen=new Pen(Theme.Border);g.DrawRectangle(pen,rect);
        for(int row=1;row<4;row++)g.DrawLine(pen,rect.Left,rect.Top+row*rect.Height/4,rect.Right,rect.Top+row*rect.Height/4);
        var samples=history.ToArray();using var line=new Pen(Theme.Accent,1.8f);
        bool hasPoint=false;
        for(int i=1;FreshLive!=null&&i<samples.Length;i++)
        {
            if(samples[i-1].Usage is not { } a||samples[i].Usage is not { } b||(samples[i].Time-samples[i-1].Time).TotalSeconds>6)continue;
            double window=Math.Max(60,(samples[^1].Time-samples[0].Time).TotalSeconds);
            float x1=rect.Right-(float)((samples[^1].Time-samples[i-1].Time).TotalSeconds/window)*rect.Width;
            float x2=rect.Right-(float)((samples[^1].Time-samples[i].Time).TotalSeconds/window)*rect.Width;
            g.DrawLine(line,x1,rect.Bottom-(float)Math.Clamp(a/100,0,1)*rect.Height,x2,rect.Bottom-(float)Math.Clamp(b/100,0,1)*rect.Height);hasPoint=true;
        }
        DrawText(g,"100%",36,153,60,18,12,false,Theme.Muted);
        double duration=samples.Length>1?Math.Max(0,(samples[^1].Time-samples[0].Time).TotalSeconds):0;
        DrawText(g,PerfText.F(language,"graphWindow",N(Math.Max(60,duration),"0")),32,268,254,22,12,false,Theme.Muted);
        if(!hasPoint)DrawText(g,T("noSamples"),43,201,230,38,10,false,Theme.Muted);
        Stat(g,T("usage"),Percent(FreshLive?.CpuUsagePercent),298,151,86,FreshLive?.CpuUsagePercent.HasValue==true?16:12);
        Stat(g,T("temperature"),FreshLive?.CpuTemperatureC is {} temp?N(temp)+" °C":Unknown,391,151,86,
            FreshLive?.CpuTemperatureC.HasValue==true?17:12,FreshLive?.CpuTemperatureC.HasValue==true?Theme.Accent:null);
        Stat(g,T("frequencyWindows"),FreshLive?.CpuFrequencyMHz is {} freq?N(freq/1000,"0.00")+" GHz":Unknown,298,217,86,FreshLive?.CpuFrequencyMHz.HasValue==true?16:12);
        Stat(g,T("cores"),Number(inventory?.Cpu.PhysicalCores)+" / "+Number(inventory?.Cpu.LogicalProcessors),391,217);
    }
    void DrawMemory(Graphics g)
    {
        MemoryCell(g,T("installedRam"),Memory(InstalledRamBytes),520,105);
        MemoryCell(g,T("used"),Memory(FreshLive?.Memory.UsedBytes),670,105);
        MemoryCell(g,T("available"),Memory(FreshLive?.Memory.AvailableBytes),820,105);
        DrawText(g,T("usableRam")+": "+Memory(FreshLive?.Memory.TotalBytes),520,151,446,22,12,false,Theme.Muted);
        using var bg=new SolidBrush(Theme.Border);g.FillRectangle(bg,520,177,446,6);
        if(FreshLive?.Memory.TotalBytes is >0 && FreshLive!.Memory.UsedBytes.HasValue)
        {using var fill=new SolidBrush(Theme.Accent);g.FillRectangle(fill,520,177,(float)Math.Clamp(FreshLive!.Memory.UsedBytes.Value/(double)FreshLive!.Memory.TotalBytes.Value,0,1)*446,6);}
        DrawText(g,T("modules"),520,188,265,19,12,false,Theme.Muted);
        DrawText(g,(inventory?.Memory.Modules.Count is >0?inventory.Memory.Modules.Count.ToString(L.Culture(language)):Unknown)+" / "+Number(inventory?.Memory.TotalSlots),754,188,212,19,12,true);
        var m=SelectedModule;
        DrawText(g,T("configuredSpeed")+": "+(m?.ConfiguredSpeedMTs is {} speed?N(speed,"0")+" "+T("mt"):m?.ConfiguredClockMHz is {} clock?N(clock,"0")+" "+T("mhz"):Unknown),520,240,446,24,12,true);
        DrawText(g,T("manufacturer")+": "+(MemoryManufacturer.Display(m?.Manufacturer)??Unknown),520,268,446,24,12,true);
    }
    string SampleTime(DateTimeOffset? time,string key)
    {
        if(time is null)return T(key=="liveUpdated"?"noSamples":"unknown");
        double age=Math.Max(0,(DateTimeOffset.Now-time.Value).TotalSeconds);
        return PerfText.F(language,key,time.Value.ToLocalTime().ToString("HH:mm:ss",L.Culture(language)),N(age,"0"));
    }
    void DrawGpu(Graphics g)
    {
        var gpu=SelectedGpu;var status=FreshLive?.Gpus.FirstOrDefault(v=>v.Id==gpu?.Id);
        Stat(g,T("usage"),Percent(status?.UsagePercent),32,382,143,20,Theme.Accent);
        Stat(g,T("temperature"),status?.TemperatureC is {} temp?N(temp)+" °C":Unknown,182,382,143,20);
        Stat(g,T("frequencyDriver"),status?.FrequencyMHz is {} f?N(f)+" "+T("mhz"):Unknown,332,382,149,18);
        DrawText(g,T("videoMemory"),32,459,449,23,14,true);
        ulong? free=gpu?.DedicatedMemoryBytes is {} total&&status?.DedicatedUsedBytes is {} used&&used<=total?total-used:null;
        MemoryCell(g,T("total"),Memory(gpu?.DedicatedMemoryBytes),32,488);
        MemoryCell(g,T("used"),Memory(status?.DedicatedUsedBytes),182,488);
        MemoryCell(g,T("free"),Memory(free),332,488);
        DrawText(g,T("capacityDriver"),32,534,449,22,12,false,Theme.Muted);
        DrawText(g,T("sharedMemory")+": "+Memory(status?.SharedUsedBytes),32,561,449,24,13);
        string kind=T(gpu?.Kind switch {GpuKind.Integrated=>"integrated",GpuKind.Dedicated=>"dedicated",GpuKind.Software=>"software",_=>"typeUnknown"});
        DrawText(g,kind+" · "+T("gpuSources"),32,596,449,35,12,false,Theme.Muted);
        DrawText(g,SampleTime(live?.Timestamp,"liveUpdated"),32,632,449,18,12,false,FreshLive==null?Theme.Warning:Theme.Muted);
    }
    void DrawStorage(Graphics g)
    {
        var d=SelectedDisk;var v=StorageFresh?SelectedVolume:null;var freshDisk=StorageFresh?d:null;
        string type=d==null?Unknown:d.BusType=="NVMe"?"NVMe · "+DiskBytes(d.TotalBytes):(d.MediaType=="Unknown"?T("typeUnknown"):d.MediaType=="NonRotating"?T("nonRotating"):d.MediaType)+" · "+(d.BusType??Unknown)+" · "+DiskBytes(d.TotalBytes);
        DrawText(g,type,519,373,449,21,12,false,Theme.Muted);
        string health=Health(d);Color color=freshDisk!=null?DiskAssessmentEngine.Evaluate(freshDisk).Color:Theme.Muted;
        DrawText(g,health,519,399,449,23,14,true,color);
        DrawText(g,"CrystalDiskInfo: "+CdiStatus,519,425,449,22,12,false,Theme.Muted);
        DrawText(g,T("resource")+": "+Resource(d),519,450,449,23,13,true);
        string spare=freshDisk?.NvmeAvailableSparePercent is {} sp?N(sp)+" %":Unknown;
        string errors=Number(freshDisk?.NvmeMediaErrors);
        DrawText(g,T("spareShort")+": "+spare,519,477,221,22,12);
        DrawText(g,T("mediaErrorsShort")+": "+errors,744,477,224,35,12);
        DrawText(g,T("powerCycles")+": "+Number(freshDisk?.PowerOnCount),519,503,449,22,13);
        DrawText(g,T("powerHours")+": "+Number(freshDisk?.PowerOnHours),519,523,449,19,13);
        MemoryCell(g,T("total"),DiskBytes(v?.TotalBytes),519,582);
        MemoryCell(g,T("usedSpace"),DiskBytes(v?.UsedBytes),669,582);
        MemoryCell(g,T("free"),DiskBytes(v?.FreeBytes),819,582);
        DrawText(g,T("temperature")+": "+(freshDisk?.TemperatureC is {} temp?N(temp)+" °C":Unknown),519,627,449,22,13,true);
        DrawText(g,SampleTime(storage?.Timestamp,"diskUpdated"),519,651,449,18,12,false,StorageFresh?Theme.Muted:Theme.Warning);
    }
    static void MemoryCell(Graphics g,string caption,string value,float x,float y)
    {DrawText(g,caption,x,y,145,18,12,false,Theme.Muted);DrawText(g,value,x,y+19,145,22,15,true);}
    protected override void Dispose(bool disposing){if(disposing){freshnessTimer.Stop();freshnessTimer.Dispose();tips.Dispose();}base.Dispose(disposing);}
}

internal sealed class DarkSelector : ComboBox
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if(message.Msg is not (0x000F or 0x0085 or 0x0317 or 0x0318)||IsDisposed||!IsHandleCreated||Width<12||Height<10)return;
        // Native ComboBox paints the drop arrow using the light Windows theme.
        // Cover only its chrome; retain native keyboard/list/accessibility behavior.
        using var g=message.Msg is 0x0317 or 0x0318&&message.WParam!=IntPtr.Zero?Graphics.FromHdc(message.WParam):Graphics.FromHwnd(Handle);int arrow=Math.Max(16,(int)(18*DeviceDpi/96f));
        using var bg=new SolidBrush(Theme.Header);g.FillRectangle(bg,Width-arrow,0,arrow,Height);
        using var border=new Pen(Theme.Border,2);g.DrawRectangle(border,1,1,Width-2,Height-2);
        using var pen=new Pen(Enabled?Theme.Text:Theme.Muted,1.3f);float x=Width-arrow/2f,y=Height/2f;
        g.DrawLine(pen,x-3,y-1,x,y+2);g.DrawLine(pen,x,y+2,x+3,y-1);
    }
}

