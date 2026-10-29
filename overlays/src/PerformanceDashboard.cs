using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BatteryDiagnostic;

internal sealed class PerformanceDashboard : UserControl
{
    readonly ComboBox modules=Selector(),gpus=Selector(),disks=Selector(),volumes=Selector();
    readonly Button benchmarkButton=CreateBenchmarkButton();
    CancellationTokenSource? benchmarkCancellation;
    double? benchmarkMilliseconds;
    CpuBenchmarkState benchmarkState=CpuBenchmarkState.Idle;
    enum CpuBenchmarkState { Idle, Running, Complete, Cancelled, Failed }
    readonly LinkLabel cpuActivate=AccessLink("cpu"),diskAccess=AccessLink("diskAccess"),smartDetails=AccessLink("smartDetails");
    readonly Queue<(DateTimeOffset Time,double? Usage)> history=[];
    readonly System.Windows.Forms.Timer freshnessTimer=new() {Interval=1000};
    PerformanceLive? FreshLive=>live!=null&&(DateTimeOffset.Now-live.Timestamp).TotalSeconds is >=0 and <=10?live:null;
    PerformanceInventory? inventory;
    PerformanceLive? live;
    StorageSnapshot? storage;
    UiLanguage language=UiLanguage.French;
    bool updating;
    bool lastLiveFresh,lastStorageFresh;
    string moduleKey="",gpuKey="",diskKey="",volumeKey="";
    float viewScale=1,viewX,viewY;
    string T(string key)=>PerfText.Get(language,key);
    string Unknown=>T("unknown");
    string N(double value,string format="0.#")=>value.ToString(format,L.Culture(language));
    string Memory(ulong? bytes)=>bytes.HasValue?N(bytes.Value/1073741824d)+" "+T("gib"):Unknown;
    string DiskBytes(ulong? bytes)=>bytes.HasValue?N(bytes.Value/1000000000d)+" "+T("gb"):Unknown;
    string Percent(double? value)=>value.HasValue&&double.IsFinite(value.Value)?L.Percent(language,value.Value):Unknown;
    string Number(object? value)=>value==null?Unknown:Convert.ToString(value,L.Culture(language))??Unknown;
    StorageDisk? SelectedDisk=>storage?.Disks.ElementAtOrDefault(disks.SelectedIndex);
    StorageVolume? SelectedVolume=>SummarizedVolume(SelectedDisk);
    MemoryModule? SelectedModule=>inventory?.Memory.Modules.ElementAtOrDefault(modules.SelectedIndex);
    GpuInventory? SelectedGpu=>inventory?.Gpus.ElementAtOrDefault(gpus.SelectedIndex);
    StorageVolume? SummarizedVolume(StorageDisk? disk)
    {
        if(disk?.Volumes==null||disk.Volumes.Count==0)return null;
        if(disk.Volumes.Count==1)return disk.Volumes[0];
        bool shared=disk.Volumes.Any(v=>v.SharedAcrossDisks);
        ulong? total=0,free=0;
        foreach(var volume in disk.Volumes)
        {
            if(total is not null)total=total.HasValue&&volume.TotalBytes.HasValue&&ulong.MaxValue-total.Value>=volume.TotalBytes.Value?total.Value+volume.TotalBytes.Value:null;
            if(free is not null)free=free.HasValue&&volume.FreeBytes.HasValue&&ulong.MaxValue-free.Value>=volume.FreeBytes.Value?free.Value+volume.FreeBytes.Value:null;
        }
        string name=string.Join(", ",disk.Volumes.Select(v=>v.Name));
        string? fs=disk.Volumes.Select(v=>v.FileSystem).FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v));
        return new StorageVolume(name,fs,total,free,shared);
    }
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
    internal string CurrentDiskHealthPercentage=>HealthPercentText(SelectedDisk);
    internal Button BenchmarkButton=>benchmarkButton;
    internal bool BenchmarkRunning=>benchmarkCancellation!=null;
    internal double? BenchmarkMilliseconds=>benchmarkMilliseconds;
    internal IReadOnlyList<ComboBox> Selectors=>[modules,gpus,disks,volumes];
    internal event EventHandler? CpuActivationRequested;
    internal event EventHandler? StorageAccessRequested;
    internal event EventHandler? StorageDetailsRequested;
    internal string? SelectedDiskIdentity=>SelectedDisk?.DeviceId;
    static LinkLabel AccessLink(string tag)=>new() {Tag=tag,TextAlign=ContentAlignment.MiddleRight,LinkColor=Theme.Accent,ActiveLinkColor=Theme.Text,VisitedLinkColor=Theme.Accent,BackColor=Theme.Card,AutoSize=false,TabStop=true,Font=new Font("Segoe UI",8.5f)};
    static Button CreateBenchmarkButton()
    {
        var button=new Button
        {
            Tag="cpuBenchmark",FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false,
            BackColor=Theme.Header,ForeColor=Theme.Accent,Cursor=Cursors.Hand,
            TextAlign=ContentAlignment.MiddleCenter,TabStop=true,Font=new Font("Segoe UI Semibold",9f)
        };
        button.FlatAppearance.BorderColor=Theme.Border;
        button.FlatAppearance.BorderSize=1;
        return button;
    }
    string BenchmarkStatusText()=>benchmarkState switch
    {
        CpuBenchmarkState.Running=>T("benchmarkRunning"),
        CpuBenchmarkState.Complete when benchmarkMilliseconds.HasValue=>PerfText.BenchmarkSeconds(language,benchmarkMilliseconds.Value),
        CpuBenchmarkState.Cancelled=>T("benchmarkCancelled"),
        CpuBenchmarkState.Failed=>T("benchmarkError"),
        _=>""
    };
    void UpdateBenchmarkButton()
    {
        benchmarkButton.Text=T(benchmarkCancellation is null?"benchmarkStart":"benchmarkStop");
        benchmarkButton.AccessibleName=benchmarkButton.Text;
        benchmarkButton.AccessibleDescription=BenchmarkStatusText()+". "+T("benchmarkTip");
        UpdateAccessibleSummary();
    }
    async void BenchmarkButtonClicked(object? sender,EventArgs e)
    {
        if(benchmarkCancellation is {} active)
        {
            active.Cancel();
            benchmarkButton.Enabled=false; // Stop requested; wait for worker to exit
            return;
        }
        var cancellation=new CancellationTokenSource();
        benchmarkCancellation=cancellation;
        benchmarkMilliseconds=null;
        benchmarkState=CpuBenchmarkState.Running;
        benchmarkButton.Enabled=true;
        UpdateBenchmarkButton();Invalidate();
        try
        {
            double milliseconds=await CpuBenchmark.RunAsync(cancellation.Token);
            if(!IsDisposed&&!Disposing)
            {
                benchmarkMilliseconds=milliseconds;
                benchmarkState=CpuBenchmarkState.Complete;
            }
        }
        catch(OperationCanceledException)
        {
            benchmarkState=CpuBenchmarkState.Cancelled;
        }
        catch(Exception)
        {
            benchmarkState=CpuBenchmarkState.Failed;
        }
        finally
        {
            if(ReferenceEquals(benchmarkCancellation,cancellation))benchmarkCancellation=null;
            cancellation.Dispose();
            if(!IsDisposed&&!Disposing)
            {
                benchmarkButton.Enabled=true;
                UpdateBenchmarkButton();Invalidate();
            }
        }
    }
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
        freshnessTimer.Tick+=(_,_)=>
        {
            if(lastLiveFresh!=(FreshLive!=null)||lastStorageFresh!=StorageFresh)
                SetData(inventory,live,storage,language);
            else Invalidate();
        };freshnessTimer.Start();
        Controls.AddRange([modules,gpus,disks,volumes,cpuActivate,diskAccess,smartDetails,benchmarkButton]);
        benchmarkButton.Click+=BenchmarkButtonClicked;
        UpdateBenchmarkButton();
        cpuActivate.LinkClicked+=(_,_)=>CpuActivationRequested?.Invoke(this,EventArgs.Empty);
        diskAccess.LinkClicked+=(_,_)=>StorageAccessRequested?.Invoke(this,EventArgs.Empty);
        smartDetails.LinkClicked+=(_,_)=>StorageDetailsRequested?.Invoke(this,EventArgs.Empty);
        foreach(var c in new[]{modules,gpus,disks,volumes})c.SelectedIndexChanged+=(_,_)=>
        {
            if(updating)return;
            if(c==disks)UpdateVolumes();
            UpdateAccessibleSummary();Invalidate();
        };
    }
    internal void AddSample(PerformanceLive sample)
    {
        history.Enqueue((sample.Timestamp,sample.CpuUsagePercent));while(history.Count>60)history.Dequeue();
    }
    internal void SetData(PerformanceInventory? inv,PerformanceLive? current,StorageSnapshot? drives,UiLanguage lang)
    {
        inventory=inv;live=current;storage=drives;language=lang;
        updating=true;
        try
        {
            Populate(modules,ref moduleKey,inv?.Memory.Modules.Select((m,i)=>(m.Slot??(T("module")+" "+(i+1)))+" · "+Memory(m.CapacityBytes)+" · "+(m.MemoryType??Unknown)).ToArray()??[],inv?.Memory.Modules.Select((m,i)=>(m.Slot??i.ToString())+":"+m.PartNumber).ToArray()??[]);
            Populate(gpus,ref gpuKey,inv?.Gpus.Select(g=>g.Model??Unknown).ToArray()??[],inv?.Gpus.Select(g=>g.Id).ToArray()??[]);
            Populate(disks,ref diskKey,drives?.Disks.Select(DiskCaption).ToArray()??[],drives?.Disks.Select(d=>d.DeviceId).ToArray()??[]);
            UpdateVolumes();
        }
        finally {updating=false;}
        modules.AccessibleName=T("module");gpus.AccessibleName=T("gpu");disks.AccessibleName=T("disk");volumes.AccessibleName=T("volume");volumes.Visible=false;
        cpuActivate.Text=T("cpuActivate");cpuActivate.AccessibleName=T("cpuActivate");
        cpuActivate.Visible=false;
        diskAccess.Text=T("diskAccess");diskAccess.AccessibleName=T("diskAccess");
        smartDetails.Text=T("smartDetails");smartDetails.AccessibleName=T("smartDetails");smartDetails.Visible=SelectedDisk!=null;
        UpdateBenchmarkButton();
        diskAccess.Visible=!PerformanceCpuTemperature.IsAdministrator&&(drives?.Diagnostics.Concat(drives.Disks.SelectMany(d=>d.Diagnostics)).Any(d=>d.Contains("AccessDenied",StringComparison.Ordinal))??false);
        AccessibleName=T("heading");
        lastLiveFresh=FreshLive!=null;lastStorageFresh=StorageFresh;
        UpdateAccessibleSummary();
        Invalidate();
    }
    void UpdateAccessibleSummary()
    {
        var sample=FreshLive;var gpu=SelectedGpu;
        var gpuLive=sample?.Gpus.FirstOrDefault(v=>v.Id==gpu?.Id);
        var disk=StorageFresh?SelectedDisk:null;var volume=StorageFresh?SelectedVolume:null;
        ulong? gpuFree=gpu?.DedicatedMemoryBytes is {} total&&gpuLive?.DedicatedUsedBytes is {} used&&used<=total?total-used:null;
        string Temperature(double? value)=>value.HasValue?N(value.Value)+" °C":Unknown;
        string Frequency(double? value)=>value.HasValue?N(value.Value)+" "+T("mhz"):Unknown;
        var lines=new List<string>
        {
            T("cpu")+": "+(inventory?.Cpu.Model??Unknown),
            T("usage")+": "+Percent(sample?.CpuUsagePercent),
            T("temperature")+": "+Temperature(sample?.CpuTemperatureC),
            T("frequencyWindows")+": "+Frequency(sample?.CpuFrequencyMHz),
            T("cores")+": "+Number(inventory?.Cpu.PhysicalCores)+" / "+Number(inventory?.Cpu.LogicalProcessors),
            T("installedRam")+": "+Memory(InstalledRamBytes),
            T("used")+": "+Memory(sample?.Memory.UsedBytes),
            T("available")+": "+Memory(sample?.Memory.AvailableBytes),
            T("modules")+": "+Number(inventory?.Memory.Modules.Count)+" / "+Number(inventory?.Memory.TotalSlots),
            T("module")+": "+modules.Text,
            T("memoryType")+": "+(SelectedModule?.MemoryType??Unknown),
            T("configuredSpeed")+": "+(SelectedModule?.ConfiguredSpeedMTs is {} speed?N(speed,"0")+" "+T("mt"):SelectedModule?.ConfiguredClockMHz is {} clock?N(clock,"0")+" "+T("mhz"):Unknown),
            T("manufacturer")+": "+(MemoryManufacturer.Display(SelectedModule?.Manufacturer)??Unknown),
            T("gpu")+": "+(gpu?.Model??Unknown),
            T("usage")+": "+Percent(gpuLive?.UsagePercent),
            T("temperature")+": "+Temperature(gpuLive?.TemperatureC),
            T("frequencyDriver")+": "+Frequency(gpuLive?.FrequencyMHz),
            T("videoMemory")+"; "+T("total")+": "+Memory(gpu?.DedicatedMemoryBytes)+"; "+T("used")+": "+Memory(gpuLive?.DedicatedUsedBytes)+"; "+T("free")+": "+Memory(gpuFree),
            T("sharedMemory")+": "+Memory(gpuLive?.SharedUsedBytes),
            T("disk")+": "+(SelectedDisk?.Model??Unknown),
            T("health")+": "+Health(SelectedDisk),
            T("healthPercent")+": "+HealthPercentText(SelectedDisk),
            T("temperature")+": "+Temperature(disk?.TemperatureC),
            T("powerCycles")+": "+Number(disk?.PowerOnCount),
            T("powerHours")+": "+Number(disk?.PowerOnHours),
            T("total")+": "+DiskBytes(volume?.TotalBytes)+"; "+T("usedSpace")+": "+DiskBytes(volume?.UsedBytes)+"; "+T("free")+": "+DiskBytes(volume?.FreeBytes),
            T("notHealthTest")
        };
        if(live!=null&&sample==null)lines.Insert(0,T("liveStale"));
        if(BenchmarkStatusText() is {Length:>0} result)lines.Add(result);
        AccessibleDescription=string.Join(Environment.NewLine,lines);
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
    string DiskCaption(StorageDisk disk)
    {
        double? life=StorageFresh?DiskAssessmentEngine.HealthPercentage(disk):null;
        return (disk.Index.HasValue?PerfText.F(language,"diskNumber",disk.Index.Value)+" · ":"")+
            (life.HasValue?L.Percent(language,life.Value)+" · ":"")+
            (string.IsNullOrWhiteSpace(disk.Model)?Unknown:disk.Model);
    }
    string HealthPercentText(StorageDisk? disk)
    {
        if(disk is null)return Unknown;
        if(!StorageFresh)return T("storageStale");
        if(disk.MediaType is "HDD" or "Rotating")return T("hddNoPercent");
        double? value=DiskAssessmentEngine.HealthPercentage(disk);
        return value.HasValue?L.Percent(language,value.Value):Unknown;
    }
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
    protected override void OnResize(EventArgs e){base.OnResize(e);PlaceSelectors();}
    void PlaceSelectors()
    {
        if(modules==null||Width<=0||Height<=0)return;
        viewScale=Math.Max(.1f,Math.Min(1.65f*DeviceDpi/96f,Math.Min(Width/1000f,Height/710f)));viewX=(Width-1000*viewScale)/2;viewY=Math.Min(24*DeviceDpi/96f,Math.Max(0,(Height-710*viewScale)/2));
        Place(modules,520,210,446);Place(gpus,32,343,449);Place(disks,519,343,449);Place(volumes,519,551,449);
        float benchmarkFont=Math.Max(8,9f*viewScale*96/DeviceDpi);
        if(Math.Abs(benchmarkButton.Font.Size-benchmarkFont)>.1){var old=benchmarkButton.Font;benchmarkButton.Font=new Font("Segoe UI Semibold",benchmarkFont);old.Dispose();}
        benchmarkButton.SetBounds((int)(viewX+349*viewScale),(int)(viewY+81*viewScale),
            Math.Max(50,(int)(130*viewScale)),Math.Max(20,(int)(27*viewScale)));
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
        DrawText(g,T("heading"),18,9,960,32,22,true);
        DrawText(g,live!=null&&FreshLive==null?T("liveStale"):T("subtitle"),18,44,960,18,12,false,live!=null&&FreshLive==null?Theme.Warning:Theme.Muted);
        Card(g,18,68,477,228,T("cpu"),HardwareArt.Cpu);Card(g,505,68,477,228,T("ram"),HardwareArt.Ram);
        Card(g,18,306,477,366,T("gpu"),HardwareArt.Gpu);Card(g,505,306,477,366,T("disk"),HardwareArt.Storage);
        DrawCpu(g);DrawMemory(g);DrawGpu(g);DrawStorage(g);
        // Retain the diagnostic caveat, but only as screen-reader/help content.
    }
    void Card(Graphics g,float x,float y,float w,float h,string name,HardwareArt art)
    {
        using var path=Theme.Round(new RectangleF(x,y,w,h),9);
        using var bg=new SolidBrush(Theme.Card);using var border=new Pen(Theme.Border,1);
        g.FillPath(bg,path);g.DrawPath(border,path);
        DevicePulseArt.Draw(g,art,new RectangleF(x+10,y+7,34,34));
        DrawText(g,name,x+48,y+12,w-62,25,14,true);
    }
    static void DrawText(Graphics g,string text,float x,float y,float w,float h,float size=12,bool bold=false,Color? color=null)
    {
        using var font=new Font(bold?"Segoe UI Semibold":"Segoe UI",Math.Max(12,size),FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(color??Theme.Text);using var format=new StringFormat {Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.LineLimit};
        g.DrawString(text,font,brush,new RectangleF(x,y,w,h),format);
    }
    void Stat(Graphics g,string label,string value,float x,float y,float w=86,float valueSize=16,Color? valueColor=null)
    {
        DrawText(g,label,x,y,w,27,12,false,Theme.Muted);
        bool missing=value==Unknown;
        if(missing&&w<110)value="—";
        float fitted=valueSize;
        while(fitted>12)
        {
            using var font=new Font("Segoe UI Semibold",fitted,FontStyle.Regular,GraphicsUnit.Pixel);
            if(g.MeasureString(value,font).Width<=w-2)break;
            fitted=Math.Max(12,fitted-.5f);
        }
        DrawText(g,value,x,y+26,w,29,fitted,true,missing?Theme.Muted:valueColor);
    }
    void DrawCpu(Graphics g)
    {
        DrawText(g,inventory?.Cpu.Model??Unknown,32,112,447,23,12,true);
        var rect=new RectangleF(33,151,249,72);
        using var pen=new Pen(Theme.Border);g.DrawRectangle(pen,rect);
        for(int row=1;row<4;row++)g.DrawLine(pen,rect.Left,rect.Top+row*rect.Height/4,rect.Right,rect.Top+row*rect.Height/4);
        var samples=history.ToArray();using var line=new Pen(Theme.Accent,2);
        bool hasPoint=false;
        for(int i=1;FreshLive!=null&&i<samples.Length;i++)
        {
            if(samples[i-1].Usage is not { } a||samples[i].Usage is not { } b||(samples[i].Time-samples[i-1].Time).TotalSeconds>6)continue;
            double window=Math.Max(60,(samples[^1].Time-samples[0].Time).TotalSeconds);
            float x1=rect.Right-(float)((samples[^1].Time-samples[i-1].Time).TotalSeconds/window)*rect.Width;
            float x2=rect.Right-(float)((samples[^1].Time-samples[i].Time).TotalSeconds/window)*rect.Width;
            g.DrawLine(line,x1,rect.Bottom-(float)Math.Clamp(a/100,0,1)*rect.Height,x2,rect.Bottom-(float)Math.Clamp(b/100,0,1)*rect.Height);hasPoint=true;
        }
        DrawText(g,"100%",37,152,57,16,11,false,Theme.Muted);
        if(!hasPoint)DrawText(g,T("noSamples"),50,177,220,29,11,false,Theme.Muted);
        Stat(g,T("usage"),Percent(FreshLive?.CpuUsagePercent),295,151,90,20,Theme.Accent);
        Stat(g,T("temperature"),FreshLive?.CpuTemperatureC is {} temp?N(temp)+" °C":Unknown,390,151,90,19,FreshLive?.CpuTemperatureC.HasValue==true?Theme.Accent:null);
        Stat(g,T("frequencyWindows"),FreshLive?.CpuFrequencyMHz is {} freq?N(freq/1000,"0.00")+" GHz":Unknown,295,222,90,16);
        Stat(g,T("cores"),Number(inventory?.Cpu.PhysicalCores)+" / "+Number(inventory?.Cpu.LogicalProcessors),390,222,90,17);
        string result=BenchmarkStatusText();
        if(result.Length>0)DrawText(g,result,32,278,447,16,11,false,
            benchmarkState==CpuBenchmarkState.Failed?Theme.Warning:Theme.Accent);
    }
    void DrawMemory(Graphics g)
    {
        MemoryCell(g,T("installedRam"),Memory(InstalledRamBytes),519,114);
        MemoryCell(g,T("used"),Memory(FreshLive?.Memory.UsedBytes),669,114);
        MemoryCell(g,T("available"),Memory(FreshLive?.Memory.AvailableBytes),819,114);
        using var bg=new SolidBrush(Theme.Border);g.FillRectangle(bg,520,166,446,7);
        if(FreshLive?.Memory.TotalBytes is >0&&FreshLive.Memory.UsedBytes.HasValue)
        {using var fill=new SolidBrush(Theme.Accent);g.FillRectangle(fill,520,166,(float)Math.Clamp(FreshLive.Memory.UsedBytes.Value/(double)FreshLive.Memory.TotalBytes.Value,0,1)*446,7);}
        DrawText(g,T("modules"),520,180,242,20,12,false,Theme.Muted);
        DrawText(g,(inventory?.Memory.Modules.Count is >0?inventory.Memory.Modules.Count.ToString(L.Culture(language)):Unknown)+" / "+Number(inventory?.Memory.TotalSlots),770,180,196,20,12,true);
        var m=SelectedModule;
        DrawText(g,T("memoryType")+": "+(m?.MemoryType??Unknown),520,244,184,22,12,true,Theme.Accent);
        DrawText(g,T("configuredSpeed")+": "+(m?.ConfiguredSpeedMTs is {} speed?N(speed,"0")+" "+T("mt"):m?.ConfiguredClockMHz is {} clock?N(clock,"0")+" "+T("mhz"):Unknown),710,244,256,23,12,true);
        DrawText(g,T("manufacturer")+": "+(MemoryManufacturer.Display(m?.Manufacturer)??Unknown),520,269,445,20,12,true);
    }
    void DrawGpu(Graphics g)
    {
        var gpu=SelectedGpu;var status=FreshLive?.Gpus.FirstOrDefault(v=>v.Id==gpu?.Id);
        Stat(g,T("usage"),Percent(status?.UsagePercent),32,385,140,22,Theme.Accent);
        Stat(g,T("temperature"),status?.TemperatureC is {} temp?N(temp)+" °C":Unknown,181,385,140,17);
        Stat(g,T("frequencyDriver"),status?.FrequencyMHz is {} f?N(f)+" "+T("mhz"):Unknown,333,385,148,16);
        DrawSeparator(g,32,462,449);
        DrawText(g,T("videoMemory"),32,473,449,24,15,true);
        ulong? free=gpu?.DedicatedMemoryBytes is {} total&&status?.DedicatedUsedBytes is {} used&&used<=total?total-used:null;
        MemoryCell(g,T("total"),Memory(gpu?.DedicatedMemoryBytes),32,512);
        MemoryCell(g,T("used"),Memory(status?.DedicatedUsedBytes),182,512);
        MemoryCell(g,T("free"),Memory(free),332,512);
        DrawText(g,T("capacityDriver"),32,568,449,19,12,false,Theme.Muted);
        DrawText(g,T("sharedMemory")+": "+Memory(status?.SharedUsedBytes),32,602,449,25,13);
    }
    static void DrawSeparator(Graphics g,float x,float y,float w)
    {using var pen=new Pen(Theme.Border);g.DrawLine(pen,x,y,x+w,y);}
    static void DrawDiskRing(Graphics g,float x,float y,float diameter,double? percentage,Color color)
    {
        using var track=new Pen(Theme.Border,10){StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round};
        using var active=new Pen(color,10){StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round};
        var bounds=new RectangleF(x+6,y+6,diameter-12,diameter-12);
        g.DrawArc(track,bounds,0,360);
        if(percentage.HasValue)g.DrawArc(active,bounds,-90,(float)Math.Clamp(percentage.Value,0,100)*3.6f);
    }
    void DrawStorage(Graphics g)
    {
        var d=SelectedDisk;var v=StorageFresh?SelectedVolume:null;var freshDisk=StorageFresh?d:null;
        string type=d==null?Unknown:d.BusType=="NVMe"?"NVMe · "+DiskBytes(d.TotalBytes):(d.MediaType=="Unknown"?T("typeUnknown"):d.MediaType=="NonRotating"?T("nonRotating"):d.MediaType)+" · "+(d.BusType??Unknown)+" · "+DiskBytes(d.TotalBytes);
        DrawText(g,type,519,379,449,20,12,false,Theme.Muted);
        double? life=freshDisk!=null?DiskAssessmentEngine.HealthPercentage(freshDisk):null;
        var assessment=freshDisk!=null?DiskAssessmentEngine.Evaluate(freshDisk):null;
        Color color=assessment?.Color??Theme.Muted;
        // Keep hardware warnings prominent even if SSD endurance is high.
        if(life.HasValue&&color!=Theme.Danger&&color!=Theme.Warning)
            color=life.Value<=10?Theme.Warning:Theme.Good;
        DrawDiskRing(g,524,406,104,life,color);
        using(var pctFont=new Font("Segoe UI",life.HasValue?24:12,FontStyle.Bold,GraphicsUnit.Pixel))
        using(var pctBrush=new SolidBrush(color))
        using(var pctAlignment=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter})
            g.DrawString(HealthPercentText(d),pctFont,pctBrush,new RectangleF(524,406,104,104),pctAlignment);
        DrawText(g,Health(d),654,400,310,20,12,true,assessment?.Color??Theme.Muted);
        DrawText(g,T("temperature"),654,426,310,20,12,false,Theme.Muted);
        DrawText(g,freshDisk?.TemperatureC is {} temp?N(temp)+" °C":Unknown,654,447,310,26,18,true);
        DrawText(g,T("powerCycles"),654,480,145,20,12,false,Theme.Muted);
        DrawText(g,Number(freshDisk?.PowerOnCount),654,501,145,24,15,true);
        DrawText(g,T("powerHours"),814,480,150,20,12,false,Theme.Muted);
        DrawText(g,Number(freshDisk?.PowerOnHours),814,501,150,24,15,true);
        DrawSeparator(g,520,533,447);
        MemoryCell(g,T("total"),DiskBytes(v?.TotalBytes),520,545);
        MemoryCell(g,T("usedSpace"),DiskBytes(v?.UsedBytes),669,545);
        MemoryCell(g,T("free"),DiskBytes(v?.FreeBytes),819,545);
        using var bg=new SolidBrush(Theme.Border);g.FillRectangle(bg,520,612,446,8);
        if(v?.TotalBytes is >0&&v.UsedBytes is {} used)
        {using var fill=new SolidBrush(Theme.Accent);g.FillRectangle(fill,520,612,(float)Math.Clamp(used/(double)v.TotalBytes.Value,0,1)*446,8);}
    }
    static void MemoryCell(Graphics g,string caption,string value,float x,float y)
    {DrawText(g,caption,x,y,145,18,12,false,Theme.Muted);DrawText(g,value,x,y+19,145,22,15,true);}
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            benchmarkCancellation?.Cancel();
            freshnessTimer.Stop();freshnessTimer.Dispose();
        }
        base.Dispose(disposing);
    }
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

