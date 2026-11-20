using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class PetArtwork {
    // Coordinates for both the visible silhouette and the optional window hit region.
    static readonly RectangleF Body = new RectangleF(1,2,95,96);
    static readonly RectangleF Badge = new RectangleF(70,0.5f,29,29);
    static readonly Color Eye = Color.FromArgb(216,252,216);
    static readonly Color Rim = Color.FromArgb(232,255,236);
    const int CacheLimit = 12;
    struct FrameKey {
        public int Pixels, Color, EyeHeight;
        public bool Waiting, Hover, Closed, Busy;
    }
    static readonly Dictionary<FrameKey,Bitmap> frames = new Dictionary<FrameKey,Bitmap>();

    static Rectangle Fit(Rectangle client) {
        int size = Math.Min(client.Width,client.Height);
        return new Rectangle(client.Left + (client.Width-size)/2,client.Top + (client.Height-size)/2,size,size);
    }

    public static Region Shape(Rectangle client,bool waiting) {
        var square = Fit(client);
        using (var p = new GraphicsPath(FillMode.Winding)) {
            p.AddEllipse(Body);
            if (waiting) p.AddEllipse(Badge);
            using (var m = new Matrix(square.Width/100f,0,0,square.Height/100f,square.X,square.Y)) p.Transform(m);
            return new Region(p);
        }
    }

    public static void Draw(Graphics g,Rectangle client,Color coat,bool waiting,bool hovering,bool eyesClosed,bool busy,int eyeHeight) {
        var square = Fit(client);
        if (square.Width < 1) return;
        lock (frames) {
            var key = new FrameKey { Pixels=square.Width, Color=coat.ToArgb(), Waiting=waiting,
                Hover=hovering, Closed=eyesClosed, Busy=busy, EyeHeight=eyeHeight };
            Bitmap bitmap;
            if (!frames.TryGetValue(key,out bitmap)) {
                bitmap = Rasterize(key);
                if (frames.Count == CacheLimit) {
                    foreach (var frame in frames.Values) frame.Dispose();
                    frames.Clear();
                }
                frames.Add(key,bitmap);
            }
            g.DrawImageUnscaled(bitmap,square.Location);
        }
    }

    public static Bitmap Render(int size,Color coat,bool waiting,bool hovering,bool eyesClosed,bool busy,int eyeHeight) {
        if (size < 1) throw new ArgumentOutOfRangeException("size");
        var result = new Bitmap(size,size,PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
            Draw(g,new Rectangle(0,0,size,size),coat,waiting,hovering,eyesClosed,busy,eyeHeight);
        return result;
    }

    static Bitmap Rasterize(FrameKey key) {
        int sampling = key.Pixels < 160 ? 4 : key.Pixels < 512 ? 2 : 1;
        int side = checked(key.Pixels * sampling);
        using (var layers = new Bitmap(side,side,PixelFormat.Format32bppPArgb)) {
            using (var g = Graphics.FromImage(layers)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(side/100f,side/100f);
                Paint(g,key);
            }
            var final = new Bitmap(key.Pixels,key.Pixels,PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(final))
            using (var edges = new ImageAttributes()) {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                edges.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(layers,new Rectangle(0,0,final.Width,final.Height),0,0,side,side,GraphicsUnit.Pixel,edges);
            }
            return final;
        }
    }

    static Color Mix(Color a,Color b,float amount) {
        return Color.FromArgb((int)(a.R + (b.R-a.R)*amount),(int)(a.G + (b.G-a.G)*amount),(int)(a.B + (b.B-a.B)*amount));
    }

    static void Paint(Graphics g,FrameKey key) {
        Color coat = Color.FromArgb(key.Color);
        Color shade = Mix(coat,Color.FromArgb(7,23,27),0.65f);
        Color light = Mix(coat,Color.FromArgb(186,244,201),key.Hover ? .56f : .44f);
        using (var outer = new SolidBrush(shade)) g.FillEllipse(outer,Body);
        using (var shell = new GraphicsPath()) {
            shell.AddEllipse(2.8f,3.9f,91.4f,91.8f);
            using (var glaze = new PathGradientBrush(shell)) {
                glaze.CenterPoint = new PointF(32,23);
                glaze.CenterColor = light;
                glaze.SurroundColors = new[]{shade};
                glaze.InterpolationColors = new ColorBlend(4) {
                    Positions = new[]{0f,.19f,.61f,1f},
                    Colors = new[]{shade,Mix(coat,shade,.25f),Mix(coat,light,.31f),light}
                };
                g.FillPath(glaze,shell);
            }
        }
        // A soft reflection near the upper-left edge adds shape at larger sizes.
        if (key.Pixels >= 32) {
            using (var reflection = new Pen(Color.FromArgb(73,Rim),2.1f)) {
                reflection.StartCap = reflection.EndCap = LineCap.Round;
                g.DrawBezier(reflection,13,35,16,25,22,19,30,15.8f);
            }
        }
        float h = key.Closed ? 3.8f : key.Busy ? 13f : Math.Max(10,key.EyeHeight*1.85f);
        float y = 49-h/2-(key.Hover ? 2.5f : 0);
        float x = key.Hover ? 1.6f : 0;
        if (key.Waiting && !key.Closed) {
            EyeShape(g,28+x,y+1,12.5f,h*.90f);
            EyeShape(g,57+x,y-3,12.5f,h);
            using (var mouth = new SolidBrush(Color.FromArgb(220,Eye))) g.FillEllipse(mouth,45,65,6.5f,5.7f);
        } else {
            EyeShape(g,28+x,y,12.5f,h);
            EyeShape(g,57+x,y,12.5f,h);
        }
        if (key.Waiting) {
            using (var edge = new SolidBrush(Color.FromArgb(251,249,241))) g.FillEllipse(edge,Badge);
            using (var fill = new LinearGradientBrush(Badge,Color.FromArgb(255,81,92),Color.FromArgb(211,30,56),65f))
                g.FillEllipse(fill,72.2f,2.7f,24.6f,24.6f);
        }
    }

    static void EyeShape(Graphics g,float x,float y,float w,float h) {
        using (var shadow = new SolidBrush(Color.FromArgb(80,6,28,27)))
            g.FillEllipse(shadow,x+.4f,y+1.9f,w,h);
        using (var enamel = new LinearGradientBrush(new RectangleF(x,y,w,h),Color.FromArgb(245,255,231),Eye,90f))
            g.FillEllipse(enamel,x,y,w,h);
    }

    public static Icon TrayIcon(Color coat,bool waiting) {
        return TrayIcon(coat,waiting,SystemInformation.SmallIconSize.Width);
    }
    public static Icon TrayIcon(Color coat,bool waiting,int pixels) {
        using (var frame = Render(pixels,coat,waiting,false,false,false,13)) {
            var handle = frame.GetHicon();
            try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
            finally { DestroyIcon(handle); }
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
