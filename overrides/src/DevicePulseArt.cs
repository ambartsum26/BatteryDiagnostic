using System.Drawing.Drawing2D;
namespace BatteryDiagnostic;

// The approved Pulse Chip asset family is embedded into the single-file EXE.
// Read-only shared bitmaps: never dispose or mutate them from individual controls.
internal enum HardwareArt { Cpu, Ram, Gpu, Storage, Battery }
internal static class DevicePulseArt
{
    static readonly Dictionary<HardwareArt, Image?> images=new()
    {
        [HardwareArt.Cpu]=Load("Cpu"),
        [HardwareArt.Ram]=Load("Ram"),
        [HardwareArt.Gpu]=Load("Gpu"),
        [HardwareArt.Storage]=Load("Storage"),
        [HardwareArt.Battery]=Load("Battery")
    };
    static Image? Load(string name)
    {
        using var stream=typeof(DevicePulseArt).Assembly.GetManifestResourceStream("DevicePulse.Icons."+name);
        if(stream==null)return null;
        using var source=System.Drawing.Image.FromStream(stream);
        return new Bitmap(source);
    }
    internal static bool Available(HardwareArt kind)=>images[kind]!=null;
    internal static Image? GetImage(HardwareArt kind)=>images[kind];
    internal static PictureBox Picture(HardwareArt kind,int size=30)=>new()
    {
        Image=GetImage(kind),SizeMode=PictureBoxSizeMode.Zoom,Size=new Size(size,size),
        Dock=DockStyle.Fill,BackColor=Theme.Card,Margin=new Padding(0,1,7,1),TabStop=false
    };
    internal static void Draw(Graphics g,HardwareArt kind,RectangleF bounds)
    {
        if(images[kind] is not {} bitmap)return;
        var saved=g.Save();
        try
        {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            g.DrawImage(bitmap,bounds);
        }
        finally{g.Restore(saved);}
    }
}
