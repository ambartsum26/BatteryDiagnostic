using System.Runtime.InteropServices;
namespace BatteryDiagnostic;

internal sealed partial class MainForm
{
    readonly CaptionButton minimize=new(false), maximize=new(false) {Tag="maximizeRestore",IsMaximize=true}, close=new(true);
    ToolTip? chromeTips;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    void SetupChrome()
    {
        var bar=new Panel {Dock=DockStyle.Top,Height=48,BackColor=Theme.Header};
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Right,Width=138,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=Padding.Empty,Padding=Padding.Empty};
        buttons.Controls.AddRange([minimize,maximize,close]);bar.Controls.Add(buttons);
        var actions=new FlowLayoutPanel {Dock=DockStyle.Right,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(0,5,12,0)};
        actions.Controls.AddRange([battery,languageButton]);bar.Controls.Add(actions);actions.BringToFront();
        minimize.Click+=(_,_)=>WindowState=FormWindowState.Minimized;
        maximize.Click+=(_,_)=>ToggleWindowSize();
        close.Click+=(_,_)=>Close();
        var brand=new Panel {Dock=DockStyle.Left,Width=216,BackColor=Theme.Header};
        var appMark=new PictureBox {Dock=DockStyle.Left,Width=48,Padding=new Padding(12,8,4,8),BackColor=Theme.Header,SizeMode=PictureBoxSizeMode.Zoom,Image=Icon?.ToBitmap()};
        var appTitle=new Label {Dock=DockStyle.Fill,Text="DevicePulse",Font=new Font("Segoe UI Semibold",12.5f),ForeColor=Theme.Text,TextAlign=ContentAlignment.MiddleLeft,BackColor=Theme.Header};
        brand.Controls.Add(appTitle);brand.Controls.Add(appMark);bar.Controls.Add(brand);
        MouseEventHandler move=(_,e)=>
        {
            if(e.Button!=MouseButtons.Left||e.Clicks>1)return;
            ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);
        };
        MouseEventHandler toggle=(_,e)=>{if(e.Button==MouseButtons.Left)ToggleWindowSize();};
        foreach(Control surface in new Control[]{bar,brand,appMark,appTitle})
        {surface.MouseDown+=move;surface.MouseDoubleClick+=toggle;}
        chromeTips=new ToolTip(components);
        Controls.Add(bar);UpdateWindowCaption();
    }
    void ToggleWindowSize()
    {
        if(WindowState!=FormWindowState.Maximized)
            MaximizedBounds=Screen.FromControl(this).WorkingArea;
        WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
        UpdateWindowCaption();
    }
    void UpdateWindowCaption()
    {
        if(maximize==null)return;
        maximize.IsRestored=WindowState==FormWindowState.Maximized;
        maximize.AccessibleName=T(maximize.IsRestored?"restore":"maximize");
        chromeTips?.SetToolTip(maximize,maximize.AccessibleName);maximize.Invalidate();
    }
    protected override void OnResize(EventArgs e)
    {base.OnResize(e);UpdateWindowCaption();}
    void LocalizeChrome()
    {
        minimize.AccessibleName=T("minimize");close.AccessibleName=T("close");
        chromeTips?.SetToolTip(minimize,T("minimize"));chromeTips?.SetToolTip(close,T("close"));
        UpdateWindowCaption();
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var p=base.CreateParams;
            p.Style=(p.Style|0x20000|0x10000|0x80000)&~0x40000;
            return p;
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowMinMax
    {
        public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if(message.Msg==0x0024&&message.LParam!=IntPtr.Zero)
        {
            var screen=Screen.FromHandle(Handle);
            var info=Marshal.PtrToStructure<WindowMinMax>(message.LParam);
            info.MaxPosition=new Point(screen.WorkingArea.Left-screen.Bounds.Left,screen.WorkingArea.Top-screen.Bounds.Top);
            info.MaxSize=new Point(screen.WorkingArea.Width,screen.WorkingArea.Height);
            Marshal.StructureToPtr(info,message.LParam,false);
        }
    }
}

internal sealed class CaptionButton : Button
{
    readonly bool isClose;
    bool hover;
    internal bool IsMaximize {get;init;}
    internal bool IsRestored {get;set;}
    internal CaptionButton(bool close)
    {
        isClose=close;Size=new Size(46,48);Margin=Padding.Empty;FlatStyle=FlatStyle.Flat;
        FlatAppearance.BorderSize=0;BackColor=Theme.Header;ForeColor=Theme.Text;
        AccessibleRole=AccessibleRole.PushButton;TabStop=true;UseVisualStyleBackColor=false;
    }
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        var background=hover?(isClose?Color.FromArgb(184,43,51):Theme.Border):Theme.Header;
        e.Graphics.Clear(background);
        float scale=DeviceDpi/96f,x=Width/2f,y=Height/2f,r=5*scale;
        using var pen=new Pen(Theme.Text,1.5f*scale);
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(isClose){e.Graphics.DrawLine(pen,x-r,y-r,x+r,y+r);e.Graphics.DrawLine(pen,x+r,y-r,x-r,y+r);}
        else if(IsMaximize)
        {
            if(IsRestored)
            {
                e.Graphics.DrawRectangle(pen,x-r+3*scale,y-r-2*scale,8*scale,8*scale);
                using var brush=new SolidBrush(background);e.Graphics.FillRectangle(brush,x-r,y-r+scale,8*scale,8*scale);
                e.Graphics.DrawRectangle(pen,x-r,y-r+scale,8*scale,8*scale);
            }
            else e.Graphics.DrawRectangle(pen,x-r,y-r,10*scale,10*scale);
        }
        else e.Graphics.DrawLine(pen,x-r,y,x+r,y);
        if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(6,6,Width-12,Height-12),Theme.Text,background);
    }
}
