namespace BatteryDiagnostic;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    internal DarkMenuRenderer(){RoundedEdges=false;}
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)=>e.Graphics.Clear(Theme.Card);
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var fill=new SolidBrush(Theme.Card);e.Graphics.FillRectangle(fill,e.AffectedBounds);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        using var fill=new SolidBrush(e.Item.Selected?Theme.Border:Theme.Card);
        e.Graphics.FillRectangle(fill,new Rectangle(Point.Empty,e.Item.Size));
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor=Theme.Text;base.OnRenderItemText(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r=e.ImageRectangle;float scale=(e.ToolStrip?.DeviceDpi??96)/96f;
        using var pen=new Pen(Theme.Accent,2*scale);
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float x=r.Left+r.Width/2f,y=r.Top+r.Height/2f;
        e.Graphics.DrawLines(pen,[new PointF(x-5*scale,y),new PointF(x-1*scale,y+4*scale),new PointF(x+6*scale,y-4*scale)]);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen=new Pen(Theme.Border);e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);
    }
}
