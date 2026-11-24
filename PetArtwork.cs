using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public enum EyeExpression { Neutral, Happy, Sad, Confused, Exhausted, Surprised }

public static class PetArtwork {
    // Coordinates for both the visible silhouette and the optional window hit region.
    static readonly RectangleF Body = new RectangleF(1,2,95,96);
    static readonly RectangleF Badge = new RectangleF(70,0.5f,29,29);
    const int CacheLimit = 40;
    const long MaxFrameBytes = 12L * 1024 * 1024;
    struct FrameKey : IEquatable<FrameKey> {
        public int Pixels, Color, EyeHeight, Lid;
        public bool Waiting, Hover, Closed, Busy;
        public EyeExpression Expression;
        public bool Equals(FrameKey other) {
            return Pixels == other.Pixels && Color == other.Color && EyeHeight == other.EyeHeight &&
                Lid == other.Lid && Expression == other.Expression && Waiting == other.Waiting &&
                Hover == other.Hover && Closed == other.Closed && Busy == other.Busy;
        }
        public override bool Equals(object value) { return value is FrameKey && Equals((FrameKey)value); }
        public override int GetHashCode() {
            unchecked {
                int hash = (Color * 397 ^ Pixels) * 397 ^ EyeHeight;
                hash = (hash * 397 ^ Lid) * 397 ^ (int)Expression;
                int flags = (Waiting ? 1 : 0) | (Hover ? 2 : 0) | (Closed ? 4 : 0) | (Busy ? 8 : 0);
                return hash * 397 ^ flags;
            }
        }
    }
    static readonly Dictionary<FrameKey,Bitmap> frames = new Dictionary<FrameKey,Bitmap>();
    static readonly Queue<FrameKey> oldest = new Queue<FrameKey>();
    static long frameBytes;

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

    public static void Draw(Graphics g,Rectangle client,Color coat,bool waiting,bool hovering,bool eyesClosed,bool busy,int eyeHeight,EyeExpression expression = EyeExpression.Neutral) {
        var square = Fit(client);
        if (square.Width < 1) return;
        lock (frames) {
            var key = new FrameKey { Pixels=square.Width, Color=coat.ToArgb(), Waiting=waiting,
                Hover=hovering, Closed=eyesClosed, Busy=busy, EyeHeight=eyeHeight, Expression=expression };
            g.DrawImageUnscaled(Cached(key),square.Location);
        }
    }

    // Each pose is rasterized just once. Subpixel breathing is applied to a high-resolution
    // source, so moving the pet's face never requires recomputing the gradients.
    public static void DrawAnimated(Graphics g,int pixels,Color coat,bool waiting,bool hovering,bool dragging,
        bool busy,int eyeHeight,double seconds,double blinkCycle,EyeExpression expression = EyeExpression.Neutral) {
        DrawBlended(g,pixels,coat,waiting,hovering,dragging,busy,eyeHeight,seconds,blinkCycle,expression,null,coat,0);
    }

    internal static void DrawBlended(Graphics g,int pixels,Color coat,bool waiting,bool hovering,bool dragging,
        bool busy,int eyeHeight,double seconds,double blinkCycle,EyeExpression expression,CoatBlend fade,Color nextCoat,double amount) {
        double breath = (1 - Math.Cos(seconds * 2 * Math.PI / (waiting ? 2.8 : 4.2))) * .5;
        double elapsed = blinkCycle % 1.0;
        int lid = elapsed < 0 ? 0 : (int)Math.Round(8 * Math.Sin(Math.PI * elapsed));
        float height = (float)(pixels * (.963 + .023*breath));
        float width = (float)(pixels * (.985 - .009*breath));
        float y = pixels-height-(float)(pixels*.012*breath);
        if (dragging) { height *= .976f; width = Math.Min(pixels,width*1.012f); }
        var dest = new RectangleF((pixels-width)/2,y,width,height);
        lock (frames) {
            var key = new FrameKey { Pixels=pixels*2, Color=coat.ToArgb(), Waiting=waiting, Hover=hovering,
                Closed=lid==8, Busy=busy, EyeHeight=eyeHeight, Lid=lid, Expression=expression };
            Bitmap frame=Cached(key);
            if (fade!=null && coat!=nextCoat) {
                fade.ReadStart(frame);
                key.Color=nextCoat.ToArgb();
                frame=fade.MixEnd(Cached(key),amount);
            }
            g.DrawImage(frame,dest);
        }
    }

    // The two cached poses have identical geometry and alpha. Blend their premultiplied
    // color channels directly to retain antialiased edges and opaque eyes during a theme change.
    internal sealed class CoatBlend : IDisposable {
        Bitmap output;
        int[] left, right, result;
        Bitmap lastLeft, lastRight;
        GCHandle outputPixels;

        static void ReadPixels(Bitmap source,Rectangle bounds,int[] pixels) {
            var data=source.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(data.Scan0,pixels,0,pixels.Length); }
            finally { source.UnlockBits(data); }
        }
        internal void ReadStart(Bitmap from) {
            if(output==null || output.Size!=from.Size) {
                Dispose();
                left=new int[from.Width*from.Height]; right=new int[left.Length]; result=new int[left.Length];
                outputPixels=GCHandle.Alloc(result,GCHandleType.Pinned);
                try { output=new Bitmap(from.Width,from.Height,from.Width*4,PixelFormat.Format32bppPArgb,outputPixels.AddrOfPinnedObject()); }
                catch { Dispose(); throw; }
            }
            if(lastLeft!=from) {
                ReadPixels(from,new Rectangle(Point.Empty,from.Size),left);
                lastLeft=from;
            }
        }
        internal Bitmap MixEnd(Bitmap to,double amount) {
            var bounds=new Rectangle(Point.Empty,to.Size);
            if(lastRight!=to) {
                ReadPixels(to,bounds,right);
                lastRight=to;
            }
            uint w=(uint)Math.Round(256*amount),v=256-w;
            const uint redBlue=0x00ff00ff;
            for(int i=0;i<left.Length;i++) {
                uint a=unchecked((uint)left[i]),b=unchecked((uint)right[i]);
                uint rb=(((a & redBlue)*v + (b & redBlue)*w)>>8) & redBlue;
                uint green=((((a>>8)&255)*v + ((b>>8)&255)*w) & 0xff00);
                result[i]=unchecked((int)((a & 0xff000000)|rb|green));
            }
            return output;
        }
        public void Dispose() {
            if(output!=null) { output.Dispose(); output=null; }
            if(outputPixels.IsAllocated) outputPixels.Free();
            left=right=result=null;
            lastLeft=lastRight=null;
        }
    }

    static Bitmap Cached(FrameKey key) {
        Bitmap bitmap;
        if (frames.TryGetValue(key,out bitmap)) return bitmap;
        long bytes=4L*key.Pixels*key.Pixels;
        while (oldest.Count>0 && (frames.Count>=CacheLimit || frameBytes+bytes>MaxFrameBytes)) {
            var previous=oldest.Dequeue();
            frames[previous].Dispose(); frames.Remove(previous);
            frameBytes-=4L*previous.Pixels*previous.Pixels;
        }
        bitmap=Rasterize(key);
        frames.Add(key,bitmap); oldest.Enqueue(key); frameBytes+=bytes;
        return bitmap;
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
        Color shade = Mix(coat,Color.FromArgb(40,30,44),.38f);
        Color light = Mix(coat,Color.White,key.Hover ? .55f : .36f);
        Color eye = Mix(coat,Color.FromArgb(20,17,29),.86f);
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
            using (var reflection = new Pen(Color.FromArgb(128,Color.White),2.1f)) {
                reflection.StartCap = reflection.EndCap = LineCap.Round;
                g.DrawBezier(reflection,13,35,16,25,22,19,30,15.8f);
            }
        }
        float rest = key.Busy ? 13f : Math.Max(10,key.EyeHeight*1.85f);
        float h = key.Closed ? 3.8f : rest+(3.8f-rest)*(key.Lid/8f);
        float y = 49-h/2-(key.Hover ? 2.5f : 0);
        float x = key.Hover ? 1.6f : 0;
        var expression = key.Waiting && key.Expression == EyeExpression.Neutral ? EyeExpression.Confused : key.Expression;
        DrawEye(g,28+x,y,h,eye,expression,true);
        DrawEye(g,57+x,y,h,eye,expression,false);
        if (key.Waiting) {
            using (var edge = new SolidBrush(Color.FromArgb(251,249,241))) g.FillEllipse(edge,Badge);
            using (var fill = new LinearGradientBrush(Badge,Color.FromArgb(255,81,92),Color.FromArgb(211,30,56),65f))
                g.FillEllipse(fill,72.2f,2.7f,24.6f,24.6f);
        }
    }

    static void DrawEye(Graphics g,float x,float y,float h,Color eye,EyeExpression expression,bool left) {
        float w = 12.5f;
        float middle = y + h/2;
        if (expression == EyeExpression.Surprised) {
            w = 17;
            x -= 2.25f;
            middle -= 2;
            h = Math.Min(30,h*1.18f);
        }
        if (expression == EyeExpression.Happy) {
            float rise = Math.Min(8, h*.42f);
            using (var pen = new Pen(eye,4.4f)) {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawBezier(pen,x,middle+rise/2,x+w*.25f,middle-rise,x+w*.75f,middle-rise,x+w,middle+rise/2);
            }
            return;
        }
        if (expression == EyeExpression.Exhausted) h = Math.Max(3.5f,h*.43f);
        if (expression == EyeExpression.Confused) {
            middle += left ? 1.8f : -3.2f;
            if (left) h *= .68f;
        }
        var state = g.Save();
        try {
            g.TranslateTransform(x+w/2,middle);
            if (expression == EyeExpression.Sad) g.RotateTransform(left ? 23 : -23);
            if (expression == EyeExpression.Confused && left) g.RotateTransform(-13);
            if (expression == EyeExpression.Surprised && h > 8) {
                using (var rim = new Pen(eye,1.6f)) {
                    g.FillEllipse(Brushes.Ivory,-w/2,-h/2,w,h);
                    g.DrawEllipse(rim,-w/2,-h/2,w,h);
                }
                w *= .54f; h *= .54f;
            }
            EyeShape(g,-w/2,-h/2,w,h,eye);
        } finally { g.Restore(state); }
    }

    static void EyeShape(Graphics g,float x,float y,float w,float h,Color eye) {
        using (var enamel = new LinearGradientBrush(new RectangleF(x,y,w,h),Mix(eye,Color.White,.11f),eye,90f))
            g.FillEllipse(enamel,x,y,w,h);
        if (h > 9) {
            using (var glint = new SolidBrush(Color.FromArgb(225,Color.White)))
                g.FillEllipse(glint,x+w*.22f,y+h*.17f,w*.32f,Math.Min(h*.23f,4.2f));
        }
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

internal sealed class PetSurface : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct BitmapHeader {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XResolution, YResolution;
        public uint ColorsUsed, ImportantColors;
    }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct AlphaBlend { public byte Operation, Flags, Opacity, Format; }
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr reference);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapHeader info,uint usage,out IntPtr pixels,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr screen,IntPtr destination,ref Size dimensions,
        IntPtr source,ref Point origin,uint colorKey,ref AlphaBlend blend,uint flags);

    IntPtr dc, dib, oldBitmap;
    Bitmap bitmap;
    Graphics graphics;
    public int Pixels { get; private set; }

    public PetSurface(int pixels) {
        Pixels = pixels;
        try {
            dc=CreateCompatibleDC(IntPtr.Zero);
            if (dc==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var header = new BitmapHeader { Size=(uint)Marshal.SizeOf(typeof(BitmapHeader)),
                Width=pixels, Height=-pixels, Planes=1, Bits=32 };
            IntPtr bits;
            dib=CreateDIBSection(dc,ref header,0,out bits,IntPtr.Zero,0);
            if (dib==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            oldBitmap=SelectObject(dc,dib);
            if (oldBitmap==IntPtr.Zero || oldBitmap==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
            bitmap=new Bitmap(pixels,pixels,pixels*4,PixelFormat.Format32bppPArgb,bits);
            graphics=Graphics.FromImage(bitmap);
            graphics.CompositingMode=CompositingMode.SourceCopy;
            graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
        } catch { Dispose(); throw; }
    }

    public bool Present(IntPtr window,Color coat,bool waiting,bool hovering,bool dragging,bool busy,int eyeHeight,double seconds,double blinkCycle,EyeExpression expression = EyeExpression.Neutral) {
        graphics.Clear(Color.Transparent);
        PetArtwork.DrawAnimated(graphics,Pixels,coat,waiting,hovering,dragging,busy,eyeHeight,seconds,blinkCycle,expression);
        graphics.Flush(FlushIntention.Sync);
        var size=new Size(Pixels,Pixels); var origin=Point.Empty;
        var blend=new AlphaBlend {Opacity=255,Format=1};
        return UpdateLayeredWindow(window,IntPtr.Zero,IntPtr.Zero,ref size,dc,ref origin,0,ref blend,2);
    }

    public void Dispose() {
        if (graphics!=null) {graphics.Dispose(); graphics=null;}
        if (bitmap!=null) {bitmap.Dispose(); bitmap=null;}
        if (oldBitmap!=IntPtr.Zero && oldBitmap!=new IntPtr(-1)) SelectObject(dc,oldBitmap);
        if (dib!=IntPtr.Zero) {DeleteObject(dib); dib=IntPtr.Zero;}
        if (dc!=IntPtr.Zero) {DeleteDC(dc); dc=IntPtr.Zero;}
        oldBitmap=IntPtr.Zero;
    }
}
