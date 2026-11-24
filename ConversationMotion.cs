using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// A small, click-through sibling of the message surface, independent of its scrollable contents.
internal sealed class ConversationMotion : Control {
    const double TransitionSeconds = .70;
    const double SpinSeconds = 1.65;
    readonly Control surface, anchor;
    readonly Timer timer = new Timer();
    readonly Func<bool> animationsEnabled;
    readonly PetArtwork.CoatBlend fade = new PetArtwork.CoatBlend();
    int busyCount, eyeHeight;
    double spinStart, spinOrigin, transitionStart, settleStart, settleOrigin;
    bool transitioning, allowMotion, released;
    Color startCoat, endCoat, startCanvas, endCanvas;

    public ConversationMotion(Control surface, Control anchor) : this(surface, anchor, WindowsAnimationsEnabled) { }

    internal ConversationMotion(Control surface, Control anchor, Func<bool> animationsEnabled) {
        this.surface = surface;
        this.anchor = anchor;
        this.animationsEnabled = animationsEnabled;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Visible = false;
        TabStop = false;
        AccessibleRole = AccessibleRole.Animation;
        AccessibleName = "Pet activity";
        surface.Controls.Add(this);
        anchor.LocationChanged += LayoutChanged;
        anchor.SizeChanged += LayoutChanged;
        anchor.VisibleChanged += HostVisibilityChanged;
        if (surface != anchor) surface.VisibleChanged += HostVisibilityChanged;
        anchor.Disposed += AnchorDisposed;
        timer.Tick += Advance;
    }

    static double Now { get { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; } }

    public IDisposable BeginBusy(PetTheme theme, int eyes) {
        if (!released) {
            double now = Now;
            if (busyCount == 0) {
                spinOrigin = Angle(now);
                spinStart = now;
            }
            busyCount++;
            if (!transitioning) { endCoat = theme.Coat; endCanvas = theme.Canvas; }
            eyeHeight = eyes;
            UpdatePlayback();
        }
        return new BusyLease(this);
    }

    sealed class BusyLease : IDisposable {
        ConversationMotion owner;
        public BusyLease(ConversationMotion owner) { this.owner = owner; }
        public void Dispose() {
            var current = System.Threading.Interlocked.Exchange(ref owner, null);
            if (current != null) current.EndBusy();
        }
    }

    void EndBusy() {
        if (released || busyCount == 0) return;
        if (InvokeRequired) {
            try { BeginInvoke(new Action(EndBusy)); } catch (InvalidOperationException) { }
            return;
        }
        if (busyCount == 1 && transitioning) {
            settleStart = Now;
            settleOrigin = Angle(settleStart);
        }
        busyCount--;
        UpdatePlayback();
    }

    public void Transition(PetTheme before, PetTheme after, int eyes) {
        if (released) return;
        double now = Now;
        double progress = Progress(now);
        startCoat = transitioning ? Blend(startCoat, endCoat, progress) : before.Coat;
        startCanvas = transitioning ? Blend(startCanvas, endCanvas, progress) : before.Canvas;
        settleOrigin = Angle(now);
        endCoat = after.Coat;
        endCanvas = after.Canvas;
        transitionStart = settleStart = now;
        transitioning = true;
        eyeHeight = eyes;
        UpdatePlayback();
    }

    static double Unit(double t) { return Math.Max(0, Math.Min(1, t)); }
    double Progress(double now) {
        double t = Unit((now - transitionStart) / TransitionSeconds);
        return t * t * (3 - 2 * t);
    }
    static Color Blend(Color a, Color b, double t) {
        return Color.FromArgb((int)Math.Round(a.R + (b.R-a.R)*t),
            (int)Math.Round(a.G + (b.G-a.G)*t), (int)Math.Round(a.B + (b.B-a.B)*t));
    }
    double Angle(double now) {
        if (busyCount > 0) return (spinOrigin + 360 * (now-spinStart) / SpinSeconds) % 360;
        if (!transitioning) return 0;
        double duration = Math.Max(.001, transitionStart + TransitionSeconds - settleStart);
        double t = Unit((now-settleStart) / (duration*.90));
        double eased = t*t*(3-2*t);
        return (settleOrigin + (360-settleOrigin)*eased) % 360;
    }

    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")]
    static extern bool SystemParametersInfo(uint action, uint parameter, out int enabled, uint flags);
    static bool WindowsAnimationsEnabled() {
        int enabled;
        return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0;
    }
    int RemainingPreviewMilliseconds() {
        return Math.Max(1,(int)Math.Ceiling((transitionStart + TransitionSeconds - Now)*1000));
    }

    void UpdatePlayback() {
        if (released) return;
        timer.Stop();
        if (transitioning && Now-transitionStart >= TransitionSeconds) transitioning = false;
        if (!transitioning) fade.Dispose();
        if ((!transitioning && busyCount == 0) || !anchor.Visible || !surface.Visible ||
                anchor.ClientSize.Width <= 0 || anchor.ClientSize.Height <= 0) {
            Visible = false;
            fade.Dispose();
            return;
        }
        allowMotion = animationsEnabled();
        Reposition();
        Visible = true;
        BringToFront();
        Invalidate();
        // Reduced motion needs a single expiration tick for the static theme preview.
        if (allowMotion || transitioning) {
            timer.Interval = allowMotion ? 15 : RemainingPreviewMilliseconds();
            timer.Start();
        }
    }
    void Advance(object sender, EventArgs args) {
        if (!Visible) {
            timer.Stop();
            return;
        }
        if (transitioning && Now-transitionStart >= TransitionSeconds) {
            transitioning = false;
            UpdatePlayback();
        } else if (!allowMotion && transitioning) {
            // Windows may dispatch WM_TIMER just before the requested deadline.
            timer.Interval = RemainingPreviewMilliseconds();
        } else Invalidate();
    }

    public void Reposition() {
        if (released || !anchor.Visible || !surface.Visible) return;
        var middle = new Point(anchor.ClientSize.Width/2, anchor.ClientSize.Height/2);
        if (anchor != surface) middle = surface.PointToClient(anchor.PointToScreen(middle));
        float scale;
        using (Graphics g = anchor.CreateGraphics()) scale = g.DpiX / 96f;
        int diameter = Math.Max(1,Math.Min(Math.Min(anchor.ClientSize.Width,anchor.ClientSize.Height),(int)Math.Round(56*scale)));
        SetBounds(middle.X-diameter/2,middle.Y-diameter/2,diameter,diameter);
    }
    void LayoutChanged(object sender, EventArgs args) { if (busyCount>0 || transitioning) UpdatePlayback(); }
    void HostVisibilityChanged(object sender, EventArgs args) { UpdatePlayback(); }
    void AnchorDisposed(object sender, EventArgs args) { Dispose(); }

    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        if (Width==0 || Height==0) return;
        var old = Region;
        using (var outline = new GraphicsPath()) {
            outline.AddEllipse(0,0,Width,Height);
            Region = new Region(outline);
        }
        if (old!=null) old.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e) {
        double now = Now;
        double t = transitioning && allowMotion ? Progress(now) : 1;
        var g = e.Graphics;
        g.Clear(Blend(startCanvas,endCanvas,t));
        // Artwork is already supersampled at twice the destination resolution.
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int side = Math.Max(1,(int)Math.Round(Math.Min(Width,Height)*.89));
        float angle = allowMotion ? (float)Angle(now) : 0f;
        var state = g.Save();
        try {
            g.TranslateTransform(Width/2f,Height/2f);
            g.RotateTransform(angle);
            g.TranslateTransform(-side/2f,-side/2f);
            bool crossfade = t>0 && t<1 && startCoat!=endCoat;
            PetArtwork.DrawBlended(g,side,t>=1 ? endCoat : startCoat,false,false,false,false,
                eyeHeight,allowMotion ? now : 0,angle/360.0,EyeExpression.Neutral,
                crossfade ? fade : null,endCoat,t);
        } finally { g.Restore(state); }
    }

    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; } // WM_NCHITTEST: HTTRANSPARENT.
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }  // WM_MOUSEACTIVATE: MA_NOACTIVATE.
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing) {
        if (disposing && !released) {
            released = true;
            timer.Stop();
            timer.Dispose();
            fade.Dispose();
            anchor.LocationChanged -= LayoutChanged;
            anchor.SizeChanged -= LayoutChanged;
            anchor.VisibleChanged -= HostVisibilityChanged;
            if (surface != anchor) surface.VisibleChanged -= HostVisibilityChanged;
            anchor.Disposed -= AnchorDisposed;
        }
        base.Dispose(disposing);
    }
}
