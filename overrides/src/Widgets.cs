using System.Drawing.Drawing2D;
using System.Drawing.Text;
namespace BatteryDiagnostic;

internal static class Theme
{
    // Approved DevicePulse / Pulse Chip identity. Only visual colors change.
    internal static readonly Color Background = Color.FromArgb(8, 20, 33), Header = Color.FromArgb(14, 34, 50), Card = Color.FromArgb(19, 40, 56);
    internal static readonly Color Border = Color.FromArgb(42, 75, 94), Text = Color.FromArgb(239, 247, 251), Muted = Color.FromArgb(151, 178, 194);
    internal static readonly Color Accent = Color.FromArgb(0, 229, 255), Warning = Color.FromArgb(240, 186, 101), Danger = Color.FromArgb(241, 116, 112);
    internal static readonly Color Good = Color.FromArgb(20, 216, 167);
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right-d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); path.AddArc(r.X, r.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
    }
}
internal sealed class RoundedPanel : Panel
{
    internal RoundedPanel() { DoubleBuffered = true; BackColor = Theme.Card; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Theme.Round(new RectangleF(.5f,.5f,Width-1,Height-1),10*DeviceDpi/96f);
        using var pen = new Pen(Theme.Border); e.Graphics.DrawPath(pen,p);
    }
}
internal sealed class BatteryGauge : Control
{
    ChargeView charge = new(null,"sourceUnknown",null,null,"unknown");
    string percentage = "?";
    internal BatteryGauge() { DoubleBuffered=true; ResizeRedraw=true; AccessibleRole=AccessibleRole.ProgressBar; }
    internal void UpdateCharge(ChargeView value, string text, string description)
    {
        bool changed=charge!=value||percentage!=text;
        charge=value; percentage=text; AccessibleName=description; AccessibleDescription=description;
        if(changed) Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
        float scale=Math.Min(Width/240f,Height/260f);
        g.TranslateTransform((Width-240*scale)/2,(Height-260*scale)/2); g.ScaleTransform(scale,scale);
        var body=new RectangleF(53,18,134,228);
        using var outline=Theme.Round(body,14); using var pen=new Pen(Theme.Muted,3); using var terminal=new SolidBrush(Theme.Muted);
        g.FillRectangle(terminal,96,6,48,11); g.DrawPath(pen,outline);
        var inside=RectangleF.Inflate(body,-7,-7); using var insidePath=Theme.Round(inside,9); using var bg=new SolidBrush(Theme.Border); g.FillPath(bg,insidePath);
        if(charge.Percent is { } pct)
        {
            var saved=g.Save(); g.SetClip(insidePath); float height=inside.Height*(float)Math.Clamp(pct/100,0,1);
            using var fill=new SolidBrush(pct<=15?Theme.Warning:Theme.Accent); g.FillRectangle(fill,inside.X,inside.Bottom-height,inside.Width,height); g.Restore(saved);
        }
        using var font=new Font("Segoe UI",charge.Percent.HasValue?29:43,FontStyle.Bold,GraphicsUnit.Pixel);
        using var fmt=new StringFormat { Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center };
        using var halo=new SolidBrush(Color.FromArgb(220,Theme.Card)); using var badge=Theme.Round(new RectangleF(63,99,114,48),7); g.FillPath(halo,badge);
        using var text=new SolidBrush(Theme.Text); g.DrawString(percentage,font,text,new RectangleF(56,99,128,48),fmt);
        if(charge.Online==true)
        {
            using var plug=new Pen(Theme.Text,6) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
            g.DrawLine(plug,109,175,109,190); g.DrawLine(plug,131,175,131,190);
            g.DrawLine(plug,101,191,139,191); g.DrawLine(plug,103,201,137,201);
            g.DrawArc(plug,103,188,34,30,0,180); g.DrawLine(plug,120,217,120,229);
        }
    }
}
internal sealed class IndicatorWidget : Control
{
    readonly bool health;
    readonly ToolTip tips=new() { AutoPopDelay=15000,InitialDelay=350 };
    double? value;
    CapacityGrade? grade;
    string explanation="";
    internal static Color HealthColor(CapacityGrade? condition)=>condition switch
    {
        CapacityGrade.Critical=>Theme.Danger,
        CapacityGrade.Medium=>Theme.Warning,
        CapacityGrade.Perfect=>Theme.Accent,
        _=>Theme.Muted
    };
    internal static int HealthDotCount(CapacityGrade? condition)=>condition switch
    {
        CapacityGrade.Critical=>1,
        CapacityGrade.Medium=>2,
        CapacityGrade.Perfect=>3,
        _=>0
    };
    internal static Color ChargeColor(double? percent)=>!percent.HasValue?Theme.Muted:percent<=15?Theme.Warning:Theme.Accent;
    internal static int ChargeDotCount(double? percent)=>percent.HasValue?(int)Math.Ceiling(Math.Clamp(percent.Value,0,100)/25):0;
    internal IndicatorWidget(bool isHealth)
    {
        health=isHealth; DoubleBuffered=true; ResizeRedraw=true; Size=new Size(100,34); Margin=new Padding(6,0,0,0); TabStop=true;
        AccessibleRole=AccessibleRole.ProgressBar; Cursor=Cursors.Help;
    }
    internal void SetValue(double? percent, string name, string tooltip, CapacityGrade? condition=null)
    {
        bool changed=value!=percent||grade!=condition; value=percent; grade=condition; AccessibleName=name; AccessibleDescription=tooltip;
        if(explanation!=tooltip){explanation=tooltip;tips.SetToolTip(this,tooltip);}
        if(changed) Invalidate();
    }
    protected override void OnClick(EventArgs e) { base.OnClick(e); tips.Show(explanation,this,0,Height,10000); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if(e.KeyCode is Keys.Space or Keys.Enter){tips.Show(explanation,this,0,Height,10000);e.Handled=true;} }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { tips.Hide(this); base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; float scale=Math.Min(Width/100f,Height/34f); g.ScaleTransform(scale,scale); g.SmoothingMode=SmoothingMode.AntiAlias;
        Color color=health?HealthColor(grade):ChargeColor(value);
        using var pen=new Pen(color,1.5f); using var icon=new SolidBrush(color);
        if(health)
        {
            using var heart=new GraphicsPath(); heart.AddBezier(14,24,10,20,2,15,5,10); heart.AddBezier(5,10,8,5,12,8,14,11); heart.AddBezier(14,11,17,6,23,6,24,12); heart.AddBezier(24,12,25,17,18,21,14,24); g.FillPath(icon,heart);
        }
        else { g.DrawRectangle(pen,4,11,20,12); g.FillRectangle(icon,25,14,3,6); if(value.HasValue)g.FillRectangle(icon,7,14,14*(float)Math.Clamp(value.Value/100,0,1),6); }
        int active=health?HealthDotCount(grade):ChargeDotCount(value);
        int dotCount=health?3:4;
        float dotStart=health?43:36;
        for(int i=0;i<dotCount;i++)
        {
            var circle=new RectangleF(dotStart+i*14,12,9,9);
            if(value.HasValue&&i<active)g.FillEllipse(icon,circle);else g.DrawEllipse(pen,circle);
        }
        if(Focused){using var focus=new Pen(Theme.Muted){DashStyle=DashStyle.Dot};g.DrawRectangle(focus,1,2,95,28);}
    }
    protected override void Dispose(bool disposing) { if(disposing)tips.Dispose();base.Dispose(disposing); }
}
