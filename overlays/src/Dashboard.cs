using System.Globalization;
namespace BatteryDiagnostic;

internal sealed class Dashboard : UserControl
{
    readonly TableLayoutPanel root = new() {Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(20,16,20,16)};
    readonly Label heading=LabelFor(19,true),model=LabelFor(10,false,Theme.Muted);
    readonly Label system=LabelFor(13,true),batteryTitle=LabelFor(11,true),bottomState=LabelFor(12,true);
    readonly Label healthCaption=LabelFor(13,true),declaredCaption=LabelFor(12,true),fullCaption=LabelFor(12,true);
    readonly Label healthValue=LabelFor(17,true),declaredValue=LabelFor(19,true),fullValue=LabelFor(19,true);
    readonly Label batteryModel=LabelFor(11,false,Theme.Muted),manufacturer=LabelFor(11,false,Theme.Muted);
    readonly Label detailTime=LabelFor(11,false,Theme.Muted),notice=LabelFor(10,false,Theme.Warning),scope=LabelFor(10,false,Theme.Muted);
    readonly Label capacityTitle=LabelFor(13,true),detailsTitle=LabelFor(13,true);
    readonly FlowLayoutPanel details=new() {Dock=DockStyle.Fill,AutoSize=false,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(0,5,0,0),Margin=Padding.Empty};
    readonly ToolTip tips=new() {AutoPopDelay=20000,InitialDelay=350};
    readonly BatteryHealthRing healthRing=new() {Dock=DockStyle.Fill};
    internal readonly BatteryGauge Gauge=new() {Dock=DockStyle.Fill,Margin=Padding.Empty};
    internal readonly IndicatorWidget HealthIndicator=new(true), ChargeIndicator=new(false);

    UiLanguage lang=UiLanguage.French;
    BatteryData? data;
    Reading? reading;
    string error="";
    bool loaded;
    internal ChargeView CurrentCharge { get; private set; } = new(null,"sourceUnknown",null,null,"unknown");
    internal string CurrentHealth => healthValue.Text;
    string T(string key)=>L.Get(lang,key);
    string N(double value,string format="0.0")=>value.ToString(format,L.Culture(lang));

    internal Dashboard()
    {
        BackColor=Theme.Background;DoubleBuffered=true;AutoSize=false;
        foreach(var label in new[]{heading,model,notice,scope})label.BackColor=Theme.Background;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,29));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,47));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,25));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
        heading.TextAlign=ContentAlignment.MiddleLeft;
        model.TextAlign=ContentAlignment.TopLeft;

        // Two original result groups, reorganized without changing the data source.
        var primary=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        primary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));
        primary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        var healthCard=Card(0,6);var chargeCard=Card(6,0);
        var healthLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        healthLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,43));healthLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var healthHeader=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        healthHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));healthHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,109));
        // Hide the decorative mini indicator and preserve the primary health ring.
        HealthIndicator.Visible=false;
        healthHeader.ColumnStyles[1].Width=0;
        healthHeader.Controls.Add(healthCaption,0,0);
        var healthBody=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        healthBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,47));healthBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,53));
        var healthCopy=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
        healthCopy.RowStyles.Add(new RowStyle(SizeType.Percent,35));
        healthCopy.RowStyles.Add(new RowStyle(SizeType.Percent,40));
        healthCopy.RowStyles.Add(new RowStyle(SizeType.Percent,25));
        healthValue.TextAlign=ContentAlignment.MiddleLeft;
        healthCopy.Controls.Add(batteryTitle,0,0);healthCopy.Controls.Add(healthValue,0,1);
        healthBody.Controls.Add(healthRing,0,0);healthBody.Controls.Add(healthCopy,1,0);
        healthLayout.Controls.Add(healthHeader,0,0);healthLayout.Controls.Add(healthBody,0,1);
        healthCard.Controls.Add(healthLayout);

        var chargeLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
        chargeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,43));
        chargeLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        chargeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
        var chargeHeader=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        chargeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));chargeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,109));
        // Hide the decorative mini indicator and preserve the live charge gauge.
        ChargeIndicator.Visible=false;
        chargeHeader.ColumnStyles[1].Width=0;
        chargeHeader.Controls.Add(system,0,0);
        bottomState.TextAlign=ContentAlignment.MiddleCenter;
        chargeLayout.Controls.Add(chargeHeader,0,0);chargeLayout.Controls.Add(Gauge,0,1);chargeLayout.Controls.Add(bottomState,0,2);
        chargeCard.Controls.Add(chargeLayout);
        primary.Controls.Add(healthCard,0,0);primary.Controls.Add(chargeCard,1,0);

        var capacityCard=Card(0,0);
        var capacityLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        capacityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));capacityLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var capacities=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        capacities.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));capacities.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        var original=CapacityColumn(declaredCaption,declaredValue);
        var current=CapacityColumn(fullCaption,fullValue);
        capacities.Controls.Add(original,0,0);capacities.Controls.Add(current,1,0);
        capacityLayout.Controls.Add(capacityTitle,0,0);capacityLayout.Controls.Add(capacities,0,1);capacityCard.Controls.Add(capacityLayout);

        var metadataCard=Card(0,0);
        var metadataLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        metadataLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,34));metadataLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        foreach(var label in new[]{batteryModel,manufacturer,detailTime})
        {
            label.Dock=DockStyle.None;label.AutoSize=true;label.Margin=new Padding(0,0,0,9);details.Controls.Add(label);
        }
        metadataLayout.Controls.Add(detailsTitle,0,0);metadataLayout.Controls.Add(details,0,1);metadataCard.Controls.Add(metadataLayout);
        notice.TextAlign=ContentAlignment.MiddleLeft;scope.TextAlign=ContentAlignment.MiddleLeft;
        root.Controls.Add(heading,0,0);root.Controls.Add(model,0,1);root.Controls.Add(primary,0,2);
        root.Controls.Add(capacityCard,0,3);root.Controls.Add(metadataCard,0,4);root.Controls.Add(notice,0,5);root.Controls.Add(scope,0,6);
        Controls.Add(root);
        SetData(null,null,UiLanguage.French,"",false);
    }
    static RoundedPanel Card(int left,int right)=>new() {Dock=DockStyle.Fill,Padding=new Padding(20,14,20,14),Margin=new Padding(left,6,right,6)};
    static TableLayoutPanel CapacityColumn(Label caption,Label value)
    {
        var group=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        group.RowStyles.Add(new RowStyle(SizeType.Percent,42));group.RowStyles.Add(new RowStyle(SizeType.Percent,58));
        caption.TextAlign=ContentAlignment.BottomLeft;value.TextAlign=ContentAlignment.TopLeft;
        group.Controls.Add(caption,0,0);group.Controls.Add(value,0,1);return group;
    }
    static Label LabelFor(float size,bool bold=false,Color? color=null)=>new()
    {
        Dock=DockStyle.Fill, ForeColor=color??Theme.Text, BackColor=Theme.Card,
        Font=new Font(bold?"Segoe UI Semibold":"Segoe UI",size,FontStyle.Regular), Margin=Padding.Empty,
        TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=false, UseCompatibleTextRendering=true
    };
    static void AddField(TableLayoutPanel table,int row,Label caption,Label value){table.Controls.Add(caption,0,row);table.Controls.Add(value,1,row);}

    internal string Capacity(double? value)
    {
        if(!value.HasValue)return T("unknown");
        if(reading?.Relative==true)return N(value.Value,"0.##")+" "+T("relative");
        return N(value.Value/1000,"0.00")+" Wh";
    }
    internal void SetData(BatteryData? current,Reading? selected,UiLanguage language,string sourceError,bool completed)
    {
        data=current; reading=selected; lang=language; error=sourceError; loaded=completed;
        var charge=Engine.Charge(reading,data); CurrentCharge=charge;
        var assessment=reading==null?null:Engine.Evaluate(reading,lang);
        string percentage=charge.Percent.HasValue?L.Percent(lang,charge.Percent.Value):T("unknown");
        string state=T(charge.StateKey);
        string power=charge.StateKey=="charging"?T("charging")
            :charge.Online==true?T("online")
            :charge.Online==false?T("offline"):T("powerUnknown");
        heading.Text=T("information"); model.Text=data?.Machine is { Length:>0 } machine?machine:T("model")+": "+T("unknown");
        system.Text=T("charge"); capacityTitle.Text=T("capacityHeading");detailsTitle.Text=T("batteryInfoHeading");batteryTitle.Text=L.F(lang,"battery",(reading?.Slot??0)+1);
        healthCaption.Text=T("health");
        declaredCaption.Text=T("declared"); fullCaption.Text=T("full");
        string gradeText=assessment!=null?T(Engine.GradeLabelKey(assessment.Grade)):T("unknown");
        healthValue.Text=assessment?.Retained.HasValue==true?gradeText:T("unknown");
        Color healthColor=IndicatorWidget.HealthColor(assessment?.Grade);
        healthCaption.ForeColor=Theme.Text;
        healthValue.ForeColor=healthColor;
        declaredValue.Text=Capacity(reading?.Design); fullValue.Text=Capacity(reading?.Full);
        bottomState.Text=power;
        Gauge.UpdateCharge(charge,charge.Percent.HasValue?L.Percent(lang,charge.Percent.Value):"?",T("charge")+": "+percentage+"; "+state+"; "+power);
        string healthTip=assessment?.Retained is { } retainedForTip
            ?L.F(lang,"healthTip",Capacity(reading?.Full),Capacity(reading?.Design),L.Percent(lang,retainedForTip))
            :T("healthUnknown");
        string chargeTip=L.F(lang,"chargeTip",percentage+" · "+state,power,T(charge.SourceKey));
        HealthIndicator.SetValue(assessment?.Retained,T("health"),healthValue.Text+Environment.NewLine+healthTip,assessment?.Grade);
        ChargeIndicator.SetValue(charge.Percent,T("charge"),chargeTip);
        tips.SetToolTip(healthValue,healthTip); tips.SetToolTip(healthCaption,healthTip);
        batteryModel.Text=T("batteryModel")+": "+(reading?.BatteryModel??T("unknown"));
        tips.SetToolTip(batteryModel,T("batteryModelTip"));
        manufacturer.Text=T("manufacturer")+": "+(reading?.Manufacturer??T("unknown"));
        tips.SetToolTip(manufacturer,manufacturer.Text);
        var observation=reading?.Observation;
        detailTime.Text=observation!=null?L.F(lang,"observation",observation.Samples,observation.Seconds.ToString("0",L.Culture(lang))):T("observationUnknown");
                int observationNotes=(observation?.Findings.Length??0)+(assessment?.Signals.Count??0)+(assessment?.Notes.Count??0);
        if(observation!=null)detailTime.Text+=Environment.NewLine+(observationNotes>0?L.F(lang,"stabilityNotes",observationNotes):observation.ValidSamples<10||observation.Seconds<30?T("stabilityUnknown"):T("stabilityClear"));
        var evidence=new List<string> {T("passive")};
        if(observation==null || observation.Seconds<30 || observation.ValidSamples<10)evidence.Add(T("shortObservation"));
        else if(observationNotes==0)evidence.Add(T("noObservedSignal"));
        if(observation!=null)evidence.AddRange(observation.Findings.Select(T));
        if(assessment!=null){evidence.AddRange(assessment.Signals);evidence.AddRange(assessment.Notes);}
        tips.SetToolTip(detailTime,string.Join(Environment.NewLine,evidence));
        detailTime.Cursor=Cursors.Help;
        scope.Text=T("scope");
        var messages=new List<string>();
        if(!loaded)messages.Add(T("waiting"));
        else if(error.Length>0)messages.Add(T("errorRead")+" "+error);
        else if(reading==null)messages.Add(T("noBattery"));
        if(assessment!=null)
        {
            messages.AddRange(assessment.Signals.Select(s=>T("signals")+": "+s));
            messages.AddRange(assessment.Notes.Select(s=>T("quality")+": "+s));
        }
                if(observation!=null)messages.AddRange(observation.Findings.Select(code=>T("quality")+": "+T(code)));
        string allMessages=string.Join(Environment.NewLine,messages);
        notice.Text=messages.Count>0?messages[0]+(messages.Count>1?L.F(lang,"moreFindings",messages.Count-1):""):"";
        notice.Visible=messages.Count>0;notice.Cursor=Cursors.Help;
        tips.SetToolTip(notice,allMessages+Environment.NewLine+string.Join(Environment.NewLine,evidence));
        detailTime.AccessibleDescription=string.Join(Environment.NewLine,evidence);
        notice.AccessibleDescription=allMessages;
        notice.ForeColor=assessment?.Signals.Count>0?Theme.Danger:Theme.Warning;
        AccessibleName=T("information"); AccessibleDescription=healthCaption.Text+": "+healthValue.Text+"; "+bottomState.Text+"; "+declaredCaption.Text+": "+declaredValue.Text+"; "+fullCaption.Text+": "+fullValue.Text+"; "+notice.Text;
        healthRing.SetValue(assessment?.Retained,assessment?.Grade);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if(root==null)return;
        int width=Math.Max(200,Width-root.Padding.Horizontal);
        foreach(var label in new[]{notice,scope})label.MaximumSize=new Size(width,0);
        foreach(var label in new[]{batteryModel,manufacturer,detailTime})label.MaximumSize=new Size(Math.Max(180,width-42),0);
    }
    protected override void Dispose(bool disposing){if(disposing)tips.Dispose();base.Dispose(disposing);}
}
