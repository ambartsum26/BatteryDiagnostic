using System.Drawing.Drawing2D;
namespace BatteryDiagnostic;
// Pure presentation of the EXISTING Engine.Evaluate(...).Retained.
internal sealed class BatteryHealthRing: Control
{
    double? value;CapacityGrade? grade;
    internal BatteryHealthRing(){DoubleBuffered=true;ResizeRedraw=true;AccessibleRole=AccessibleRole.ProgressBar;}
    internal void SetValue(double? percent,CapacityGrade? condition)
    {
        value=percent is {} n&&double.IsFinite(n)&&n>=0&&n<=100?percent:null;
        grade=condition;AccessibleName=value.HasValue?value.Value.ToString("0.#")+"%":"—";
        if(!IsDisposed)Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        float diameter=Math.Min(Width-16,Height-16);
        if(diameter<32)return;
        float x=(Width-diameter)/2,y=(Height-diameter)/2;
        var rect=new RectangleF(x+7,y+7,diameter-14,diameter-14);
        using var track=new Pen(Theme.Border,10){StartCap=LineCap.Round,EndCap=LineCap.Round};
        var color=IndicatorWidget.HealthColor(grade);
        using var active=new Pen(color,10){StartCap=LineCap.Round,EndCap=LineCap.Round};
        g.DrawArc(track,rect,0,360);
        if(value.HasValue)g.DrawArc(active,rect,-90,(float)(value.Value*3.6));
        using var brush=new SolidBrush(value.HasValue?color:Theme.Muted);
        using var font=new Font("Segoe UI",Math.Max(18,diameter/4),FontStyle.Bold,GraphicsUnit.Pixel);
        using var sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
        string text=value.HasValue?value.Value.ToString("0.#")+"%":"—";
        g.DrawString(text,font,brush,new RectangleF(x,y,diameter,diameter),sf);
    }
}
