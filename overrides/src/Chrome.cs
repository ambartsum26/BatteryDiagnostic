using System.Runtime.InteropServices;
namespace BatteryDiagnostic;

internal sealed partial class MainForm
{
    readonly CaptionButton minimize=new(false), close=new(true);
    ToolTip? chromeTips;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    void SetupChrome()
    {
        var bar=new Panel {Dock=DockStyle.Top,Height=44,BackColor=Theme.Header};
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Right,Width=96,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=Padding.Empty};
        buttons.Controls.AddRange([minimize,close]);bar.Controls.Add(buttons);
        var actions=new FlowLayoutPanel {Dock=DockStyle.Right,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(0,3,8,0)};
        actions.Controls.AddRange([battery,languageButton]);bar.Controls.Add(actions);actions.BringToFront();
        minimize.Click+=(_,_)=>WindowState=FormWindowState.Minimized;
        close.Click+=(_,_)=>Close();
        // Existing title bar receives the selected Pulse Chip branding.
        var brand=new Panel {Dock=DockStyle.Left,Width=216,BackColor=Theme.Header};
        var appMark=new PictureBox {Dock=DockStyle.Left,Width=46,Padding=new Padding(8,6,3,6),BackColor=Theme.Header,SizeMode=PictureBoxSizeMode.Zoom,Image=Icon.ToBitmap()};
        var appTitle=new Label {Dock=DockStyle.Fill,Text="DevicePulse",Font=new Font("Segoe UI Semibold",13,FontStyle.Bold),ForeColor=Theme.Text,TextAlign=ContentAlignment.MiddleLeft,BackColor=Theme.Header};
        brand.Controls.Add(appTitle);brand.Controls.Add(appMark);bar.Controls.Add(brand);
        MouseEventHandler move=(_,e)=>{if(e.Button!=MouseButtons.Left||e.Clicks>1)return;ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);};
        brand.MouseDown+=move;appMark.MouseDown+=move;appTitle.MouseDown+=move;
        bar.MouseDown+=(_,e)=>
        {
            if(e.Button!=MouseButtons.Left || e.Clicks>1)return;
            ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);
        };
        chromeTips=new ToolTip(components);
        Controls.Add(bar);
    }
    void LocalizeChrome()
    {
        minimize.AccessibleName=T("minimize");close.AccessibleName=T("close");
        chromeTips?.SetToolTip(minimize,T("minimize"));chromeTips?.SetToolTip(close,T("close"));
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var p=base.CreateParams;
            // Retain taskbar/system-menu minimize support, without a resize or maximize frame.
            p.Style=(p.Style|0x20000|0x80000)&~0x10000&~0x40000;
            return p;
        }
    }
    protected override void WndProc(ref Message message)
    {
        // Also reject maximize via the system menu, shortcuts or caption double-click.
        if(message.Msg==0x0112 && (message.WParam.ToInt64()&0xFFF0)==0xF030)return;
        if(message.Msg==0x00A3)return;
        base.WndProc(ref message);
    }
}

internal sealed class CaptionButton : Button
{
    readonly bool isClose;
    bool hover;
    internal CaptionButton(bool close)
    {
        isClose=close;Size=new Size(48,34);Margin=Padding.Empty;FlatStyle=FlatStyle.Flat;
        FlatAppearance.BorderSize=0;BackColor=Theme.Header;ForeColor=Theme.Text;
        AccessibleRole=AccessibleRole.PushButton;TabStop=true;UseVisualStyleBackColor=false;
    }
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(hover?(isClose?Color.FromArgb(184,43,51):Theme.Border):Theme.Header);
        float scale=DeviceDpi/96f,x=Width/2f,y=Height/2f,r=5*scale;
        using var pen=new Pen(Color.White,1.8f*scale);
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(isClose){e.Graphics.DrawLine(pen,x-r,y-r,x+r,y+r);e.Graphics.DrawLine(pen,x+r,y-r,x-r,y+r);}
        else e.Graphics.DrawLine(pen,x-r,y,x+r,y);
        if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10),Color.White,Theme.Header);
    }
}
