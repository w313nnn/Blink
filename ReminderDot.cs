using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

sealed class ConversationPopover : Form {
    public event Action MonitorScaleChanged;
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    public int MonitorDpi(int fallback) {
        if (IsHandleCreated) try { var dpi = GetDpiForWindow(Handle); if (dpi>0) return (int)dpi; }
        catch (EntryPointNotFoundException) { }
        return fallback;
    }
    public ConversationPopover() {
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor);
        base.OnPaint(e);
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        int round = 2;
        DwmSetWindowAttribute(Handle,33,ref round,sizeof(int));
    }
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        if (m.Msg == 0x02e0 && MonitorScaleChanged != null && !IsDisposed && !Disposing)
            BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing && MonitorScaleChanged != null) MonitorScaleChanged(); }));
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
}

class ReminderDot : Form {
    static readonly float panelScale = ReadPanelScale();
    static readonly Dictionary<string,Font> fonts = new Dictionary<string,Font>();
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    static float ReadPanelScale() {
        try { uint dpi = GetDpiForSystem(); if (dpi != 0) return dpi / 96F; }
        catch(EntryPointNotFoundException) { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96F;
    }
    static int Px(int logical) { return (int)Math.Round(logical*panelScale); }
    static Size PanelSize(int w,int h) { return new Size(Px(w),Px(h)); }
    static void BoundsInUnits(Control c,int x,int y,int w,int h) {
        c.SetBounds(Px(x),Px(y),Px(x+w)-Px(x),Px(y+h)-Px(y));
    }
    static Font UiFont(float size,FontStyle style = FontStyle.Regular,int dpi = 0) {
        string key = size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + style + ":" + dpi;
        Font font;
        if (!fonts.TryGetValue(key,out font)) {
            font = dpi == 0 ? new Font("Segoe UI",size,style) : new Font("Segoe UI",size*dpi/72f,style,GraphicsUnit.Pixel);
            fonts.Add(key,font);
        }
        return font;
    }
    static void WithPanelDpi(Action operation,bool perMonitor = false) {
        IntPtr old = IntPtr.Zero;
        try { old = SetThreadDpiAwarenessContext(new IntPtr(perMonitor ? -4 : -2)); }
        catch (EntryPointNotFoundException) { }
        try { operation(); }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
    PetTheme theme;
    readonly TaskStore store;
    readonly string dataDirectory;
    PetProfile profile;
    readonly Form editor = new Form();
    readonly ConversationPopover notice = new ConversationPopover();
    readonly Button avatar = new Button();
    readonly Panel composer = new Panel();
    readonly Panel chatRule = new Panel();
    readonly RichTextBox conversation = new RichTextBox();
    readonly TextBox input = new TextBox();
    readonly ListBox tasks = new ListBox();
    readonly Label model = new Label();
    Label taskHeading, petName, noticeName;
    sealed class ThreadPanel : Panel { public ThreadPanel() { DoubleBuffered=true; } }
    readonly Panel messageThread = new ThreadPanel();
    ConversationMotion editorMotion, chatMotion;
    readonly TextBox replyBox = new TextBox();
    readonly Dictionary<string,string> drafts = new Dictionary<string,string>();
    string composingFor;
    readonly Label messageStatus = new Label();
    readonly Label emptyHistory = new Label();
    Button previousPeriod, historyRange, nextPeriod, olderMessages, newerMessages;
    readonly Label conversationCaption = new Label { Text="Messages", AutoEllipsis=true, UseMnemonic=false, TextAlign=ContentAlignment.MiddleLeft };
    readonly List<Nudge> outstanding = new List<Nudge>();
    readonly Timer timer = new Timer();
    readonly Timer blink = new Timer();
    readonly System.Diagnostics.Stopwatch petClock = new System.Diagnostics.Stopwatch();
    PetSurface petSurface;
    long nextPetSurfaceAttempt;
    Size petRegionSize;
    bool petRegionWaiting;
    bool holdingPet, draggingPet;
    EyeExpression responseExpression;
    DateTime expressionUntil;
    readonly NotifyIcon tray = new NotifyIcon();
    Icon trayIcon, waitingIcon;
    ToolStripMenuItem banners;
    Button send, finish, sendReply, messagesButton, acknowledge, snoozeReply, closeChat, upcoming, newReminder;
    readonly ToolTip hints = new ToolTip();
    string focusId;
    bool refreshing, busy, quitting, urgent, petHidden, eyesClosed, hovering, rebuilding, replying;
    string notifiedMessageId;
    const string GlobalChat = "__general";
    DateTime nextPoll = DateTime.MinValue;

    static string DataDirectory() {
        var dir = Environment.GetEnvironmentVariable("BLINK_DATA_DIR");
        return String.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReminderDot")
            : Path.GetFullPath(dir);
    }

    [STAThread] public static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try {
            string dir = DataDirectory();
            string instance = @"Local\ReminderDot.v1";
            if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLINK_DATA_DIR"))) {
                using (var hash = SHA256.Create())
                    instance += BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dir.ToUpperInvariant()))).Substring(0, 32);
            }
            bool first;
            using (var mutex = new System.Threading.Mutex(true, instance, out first)) {
                if (!first) { MessageBox.Show("Blink is already running. Click its dot on the desktop.", "Blink"); return; }
                Application.Run(new ReminderDot(dir));
            }
        } catch (Exception e) { MessageBox.Show(e.Message, "Blink could not start"); }
    }

    public ReminderDot(string dir) {
        dataDirectory = dir;
        bool profileUnavailable = false;
        try { profile = PetProfile.Load(dir); }
        catch (Exception) {
            // A cosmetic setting must never prevent the reminder scheduler from starting.
            profile = new PetProfile(); profileUnavailable = true;
        }
        theme = profile.GetTheme();
        store = new TaskStore(Path.Combine(dir, "tasks.json"));
        Text = "Blink dot"; AccessibleName = Text; AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(profile.PetPixels,profile.PetPixels); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 22, area.Bottom - Height - 22);
        BuildDotMenu();
        WithPanelDpi(() => { BuildEditor(); var handle=editor.Handle; });
        WithPanelDpi(() => { BuildNotice(); var handle=notice.Handle; },true);
        notice.MonitorScaleChanged += RenderNotice;
        ApplyProfile(); SyncInbox();
        if (profileUnavailable) Say("I couldn’t load your pet settings, so I’m using the default appearance. You can save new settings in Customize pet.");
        RefreshTasks(null);
        MouseEnter += delegate { hovering = true; PaintPetFrame(); };
        MouseLeave += delegate { hovering = false; PaintPetFrame(); };
        blink.Interval = 30;
        blink.Tick += delegate { PaintPetFrame(); };
        VisibleChanged += delegate {
            if (Visible && !quitting) { petClock.Start(); blink.Start(); RefreshPet(); }
            else { blink.Stop(); petClock.Stop(); ReleasePetSurface(); }
        };
        Point down = Point.Empty, origin = Point.Empty; bool moved = false;
        MouseDown += (s,e) => {
            if (e.Button != MouseButtons.Left) return;
            down = PointToScreen(e.Location); origin = Location; moved = false;
            Capture = true; holdingPet = true; PaintPetFrame();
        };
        MouseCaptureChanged += delegate {
            if (Capture || !holdingPet) return;
            holdingPet = draggingPet = false;
            hovering = ClientRectangle.Contains(PointToClient(Cursor.Position));
            PaintPetFrame();
        };
        MouseMove += (s,e) => {
            if (e.Button != MouseButtons.Left || !holdingPet || !Capture) return;
            Point pointer = PointToScreen(e.Location);
            int dx = pointer.X - down.X, dy = pointer.Y - down.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4) moved = draggingPet = true;
            if (moved) {
                var bounds = Screen.FromPoint(Cursor.Position).WorkingArea;
                Point where = Clamp(new Point(origin.X + dx,origin.Y + dy), bounds, Size);
                if (where != Location) {
                    Location = where;
                    if (editor.Visible) Place(editor);
                    if (notice.Visible) Place(notice);
                }
            }
        };
        MouseUp += (s,e) => {
            if (e.Button != MouseButtons.Left || !holdingPet) return;
            holdingPet = draggingPet = false;
            Capture = false;
            hovering = ClientRectangle.Contains(e.Location);
            PaintPetFrame();
            if (!moved) {
                if (notice.Visible) notice.Hide();
                else OpenInbox();
            }
        };
        Shown += async delegate {
            RefreshPet();
            await CheckModel();
        };
        timer.Interval = 750; timer.Tick += delegate { Poll(); }; timer.Start();
        FormClosed += delegate {
            quitting = true; timer.Dispose(); blink.Dispose(); ReleasePetSurface(); tray.Visible = false; tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose(); if (waitingIcon != null) waitingIcon.Dispose();
            chatMotion.Dispose(); editorMotion.Dispose(); notice.Dispose(); editor.Dispose(); hints.Dispose();
            foreach (var font in fonts.Values) font.Dispose();
            fonts.Clear();
        };
    }

    protected override CreateParams CreateParams {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000; return p; } // WS_EX_LAYERED
    }
    protected override void OnPaint(PaintEventArgs e) {
        if (profile==null) return;
        if (petClock.ElapsedMilliseconds>=nextPetSurfaceAttempt) {
            PetArtwork.Draw(e.Graphics,ClientRectangle,profile.GetColor(),urgent,hovering,eyesClosed,busy,profile.EyeHeight,DesktopExpression());
            return;
        }
        // During graphics-resource pressure, paint with stock brushes and no frame allocation.
        int size=ClientSize.Width;
        e.Graphics.FillEllipse(Brushes.DarkSlateGray,0,0,size-1,size-1);
        e.Graphics.FillEllipse(Brushes.Honeydew,size*.28f,size*.35f,size*.12f,size*.25f);
        e.Graphics.FillEllipse(Brushes.Honeydew,size*.57f,size*.35f,size*.12f,size*.25f);
        if (urgent) e.Graphics.FillEllipse(Brushes.Crimson,size*.70f,0,size*.28f,size*.28f);
    }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] struct DpiRect { public int Left, Top, Right, Bottom; }

    // Only the layered sprite needs monitor-specific physical pixel coordinates.
    protected override void CreateHandle() {
        IntPtr previous=IntPtr.Zero;
        try {
            try { previous=SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch(EntryPointNotFoundException) { }
            base.CreateHandle();
        } finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    int PetSizeForDpi(int dpi) { return Math.Max(1,(profile.PetPixels*dpi+48)/96); }
    int CurrentPetPixels() {
        if (!IsHandleCreated) return profile.PetPixels;
        try {
            uint dpi=GetDpiForWindow(Handle);
            if (dpi>0) return PetSizeForDpi((int)dpi);
        } catch(EntryPointNotFoundException) { }
        using(var g=CreateGraphics()) return PetSizeForDpi((int)Math.Round(g.DpiX));
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg==0x02E0 && profile!=null) { // WM_DPICHANGED
            int size=PetSizeForDpi(m.WParam.ToInt32() & 0xffff);
            var bounds=(DpiRect)Marshal.PtrToStructure(m.LParam,typeof(DpiRect));
            SetBounds(bounds.Left,bounds.Top,size,size);
            RefreshPet();
            if (notice.Visible) Place(notice);
            if (editor.Visible) Place(editor);
            m.Result=IntPtr.Zero; return;
        }
        base.WndProc(ref m);
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        if (profile!=null) {
            int size=CurrentPetPixels();
            ClientSize=new Size(size,size);
        }
    }
    protected override void OnHandleDestroyed(EventArgs e) {
        ReleasePetSurface();
        base.OnHandleDestroyed(e);
    }
    void ReleasePetSurface() {
        if (petSurface==null) return;
        petSurface.Dispose(); petSurface=null;
    }
    void RefreshPet() {
        if (!avatar.IsDisposed && avatar.Visible) avatar.Invalidate();
        PaintPetFrame();
    }
    EyeExpression CurrentExpression() {
        if (responseExpression != EyeExpression.Neutral && DateTime.UtcNow < expressionUntil) return responseExpression;
        if (urgent) return EyeExpression.Confused;
        if (String.Equals(profile.Character,"Sleepy",StringComparison.OrdinalIgnoreCase)) return EyeExpression.Exhausted;
        return EyeExpression.Neutral;
    }
    EyeExpression DesktopExpression() {
        if (holdingPet) return EyeExpression.Surprised;
        if (hovering) return EyeExpression.Happy;
        return CurrentExpression();
    }
    void SetExpression(EyeExpression expression) {
        responseExpression = expression;
        expressionUntil = DateTime.UtcNow.AddSeconds(3);
        RefreshPet();
    }
    void PaintPetFrame() {
        if (!IsHandleCreated || !Visible || profile==null || IsDisposed || ClientSize.Width<1) return;
        long elapsed=petClock.ElapsedMilliseconds;
        if (elapsed<nextPetSurfaceAttempt) return;
        int pixels=ClientSize.Width;
        try {
            if (petSurface==null || petSurface.Pixels!=pixels) {
                ReleasePetSurface(); petSurface=new PetSurface(pixels);
            }
            double phase=(elapsed % (profile.BlinkIntervalMs+240) - profile.BlinkIntervalMs)/240.0;
            int interval = holdingPet || hovering || phase > -.5 ? 15 : 30;
            if (blink.Interval != interval) blink.Interval = interval;
            eyesClosed=phase>.3 && phase<.7;
            if (petSurface.Present(Handle,profile.GetColor(),urgent,hovering,draggingPet,busy,profile.EyeHeight,elapsed/1000.0,phase,DesktopExpression())) return;
        } catch (System.ComponentModel.Win32Exception) {
            // A native bitmap or device context may be unavailable until Windows frees resources.
        } catch (ExternalException) {
            // Windows may temporarily be unable to provide a presentation bitmap.
        } catch (OutOfMemoryException) {
            // GDI+ also reports an exhausted image or DC allocation with this exception.
        }
        ReleasePetSurface(); nextPetSurfaceAttempt=elapsed+5000;
        Invalidate();
    }
    void BuildDotMenu() {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Blink",null,(s,e) => OpenInbox());
        menu.Items.Add("Show pending reminders",null,(s,e) => OpenInbox(outstanding.Count==0 ? null : outstanding[0].MessageId));
        menu.Items.Add("Open checklist file",null,(s,e) => {
            string error;
            if (!TryOpenChecklist(out error)) MessageBox.Show(error,"Blink checklist",MessageBoxButtons.OK,MessageBoxIcon.Information);
        });
        banners = new ToolStripMenuItem("Windows notification banners");
        string bannerFile = Path.Combine(dataDirectory,"notification-banners.disabled");
        banners.Checked = !File.Exists(bannerFile);
        banners.Click += delegate {
            try {
                if (!banners.Checked) File.Delete(bannerFile);
                else { Directory.CreateDirectory(dataDirectory); File.WriteAllText(bannerFile,"disabled"); }
                banners.Checked = !banners.Checked;
            } catch (Exception e) { ShowEditor(); Say("I couldn’t save the notification setting. " + e.Message); }
        };
        menu.Items.Add(banners);
        menu.Items.Add("Customize pet",null,(s,e) => Customize());
        var toggle = new ToolStripMenuItem("Hide pet");
        toggle.Click += delegate { TogglePet(); };
        menu.Items.Add(toggle);
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var startup = new ToolStripMenuItem("Start when I sign in");
        menu.Items.Add(startup);
        menu.Opening += delegate { try { using (var key = Registry.CurrentUser.OpenSubKey(runKey)) startup.Checked = key != null && key.GetValue("ReminderDot") != null; } catch { startup.Enabled = false; } };
        startup.Click += delegate { try { using(var key = Registry.CurrentUser.CreateSubKey(runKey)) {
            if (startup.Checked) key.DeleteValue("ReminderDot",false);
            else key.SetValue("ReminderDot","\"" + Application.ExecutablePath + "\" --background");
        }} catch(Exception e) { MessageBox.Show(e.Message,"Blink"); } };
        menu.Items.Add("Quit Blink",null,(s,e) => { quitting = true; Close(); });
        ContextMenuStrip = menu;
        menu.Opening += delegate { toggle.Text = petHidden ? "Show pet" : "Hide pet"; };
        tray.Text = "Blink · desktop reminders"; tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { OpenInbox(); };
        tray.BalloonTipClicked += delegate { OpenInbox(notifiedMessageId); };
        tray.Visible = true;
    }
    void TogglePet() {
        if (petHidden) { petHidden = false; Show(); RefreshPet(); return; }
        petHidden = true; notice.Hide(); editor.Hide(); Hide();
    }
    void OpenInbox(string messageId = null) {
        if (petHidden) TogglePet();
        editor.Hide();
        openingInbox=true;
        try {
            string preferred=messageId;
            if (messageId!=null) {
                var pending=outstanding.FirstOrDefault(n=>n.MessageId==messageId);
                var entry=historySnapshot.LastOrDefault(h=>h.MessageId==messageId);
                DateTime? sent=pending!=null ? (DateTime?)pending.SentUtc : entry==null ? null : (DateTime?)entry.AtUtc;
                if (sent.HasValue) {
                    var target=profile.HistoryAt(sent.Value.ToLocalTime());
                    browsingDate=target.Start==profile.HistoryAt(DateTime.Now).Start ? (DateTime?)null : target.Start;
                    locateMessage=messageId;
                }
            } else if (!browsingDate.HasValue && historyPage==0 && replyBox.TextLength==0) {
                var window=profile.HistoryAt(DateTime.Now);
                var pending=outstanding.FirstOrDefault(n=>{var t=n.SentUtc.ToLocalTime(); return t>=window.Start && t<window.End;});
                if(pending!=null) preferred=pending.MessageId;
            }
            SyncInbox(preferred);
        }
        finally { openingInbox=false; }
        WithPanelDpi(() => { Place(notice); if (!notice.Visible) { notice.Show(); RepaintConversation(); } },true);
        notice.Activate(); if (replyBox.Enabled) replyBox.Focus();
    }
    void ApplyProfile() {
        theme = profile.GetTheme();
        renderedTheme=null;
        ApplyTheme(editor,theme); ApplyTheme(notice,theme);
        ApplyMenuTheme(ContextMenuStrip,theme);
        ApplyMenuTheme(tasks.ContextMenuStrip,theme);
        int start=conversation.SelectionStart, length=conversation.SelectionLength;
        conversation.SelectAll(); conversation.SelectionColor=theme.Ink;
        conversation.Select(Math.Min(start,conversation.TextLength),Math.Min(length,conversation.TextLength-Math.Min(start,conversation.TextLength)));
        var anchor = new Point(Right,Bottom);
        var area = Screen.FromControl(this).WorkingArea;
        int size = CurrentPetPixels();
        ClientSize = new Size(size,size);
        Location = Clamp(new Point(anchor.X - Width,anchor.Y - Height),area,Size);
        var oldIcon = trayIcon; var oldWaiting = waitingIcon;
        trayIcon = PetArtwork.TrayIcon(profile.GetColor(),false);
        waitingIcon = PetArtwork.TrayIcon(profile.GetColor(),true);
        tray.Icon = urgent ? waitingIcon : trayIcon;
        if (oldIcon != null) oldIcon.Dispose(); if (oldWaiting != null) oldWaiting.Dispose();
        petName.Text = profile.Name; noticeName.Text = profile.Name;
        hints.SetToolTip(petName,profile.Name); hints.SetToolTip(noticeName,profile.Name);
        hints.SetToolTip(this,profile.Name + " · click to talk, drag to move");
        UpdateBadge(true);

        if (editor.Visible) Place(editor);
        if (notice.Visible) Place(notice);
        RefreshPet();
        if (notice.Visible) RenderNotice();
    }

    void ApplyTheme(Control control,PetTheme colors) {
        var button = control as Button;
        if (button != null) {
            bool primary = button.Tag is bool && (bool)button.Tag;
            button.BackColor = primary ? colors.Accent : colors.Soft;
            button.ForeColor = primary ? colors.OnAccent : colors.Ink;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Dark(colors.Accent,0.08F) : colors.Incoming;
            button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(colors.Accent,0.15F) : colors.Outgoing;
            if (button==avatar) button.BackColor=colors.Canvas;
        } else if (!(control is ColorChoice) && !(control is MessageBubble)) {
            control.ForeColor = control==model || control==messageStatus ? colors.Accent : colors.Ink;
            if (control==chatRule) control.BackColor = colors.Border;
            else if (control==composer || control==replyBox) control.BackColor=colors.Soft;
            else if (control is TextBoxBase || control is ListControl || control==conversation.Parent) control.BackColor=colors.Incoming;
            else control.BackColor=colors.Canvas;
        }
        foreach (Control child in control.Controls) ApplyTheme(child,colors);
    }
    sealed class MenuColors : ProfessionalColorTable {
        readonly PetTheme theme;
        public MenuColors(PetTheme colors) { theme=colors; }
        public override Color ToolStripDropDownBackground { get { return theme.Canvas; } }
        public override Color MenuBorder { get { return theme.Border; } }
        public override Color MenuItemBorder { get { return theme.Border; } }
        public override Color MenuItemSelected { get { return theme.Outgoing; } }
        public override Color ImageMarginGradientBegin { get { return theme.Soft; } }
        public override Color ImageMarginGradientMiddle { get { return theme.Soft; } }
        public override Color ImageMarginGradientEnd { get { return theme.Soft; } }
        public override Color CheckBackground { get { return theme.Outgoing; } }
        public override Color CheckSelectedBackground { get { return theme.Outgoing; } }
    }
    static void ApplyMenuTheme(ContextMenuStrip menu,PetTheme colors) {
        if (menu==null) return;
        menu.Renderer=new ToolStripProfessionalRenderer(new MenuColors(colors));
        menu.BackColor=colors.Canvas; menu.ForeColor=colors.Ink;
        foreach (ToolStripItem item in menu.Items) item.ForeColor=colors.Ink;
    }

    static Point Clamp(Point p, Rectangle bounds, Size size) {
        return new Point(Math.Max(bounds.Left, Math.Min(p.X,bounds.Right-size.Width)),
                         Math.Max(bounds.Top, Math.Min(p.Y,bounds.Bottom-size.Height)));
    }
    void Place(Form f) {
        WithPanelDpi(() => PlaceOnPetMonitor(f),f == notice);
    }
    void PlaceOnPetMonitor(Form f) {
        var area = Screen.FromControl(this).WorkingArea;
        int gap = f == notice ? new ChatGrid(notice.MonitorDpi((int)Math.Round(panelScale*96))).Px(8) : Px(8);
        int alignX = Left + Width/2 < area.Left + area.Width/2 ? Left : Right - f.Width;
        var desired = new Point(alignX,Top - gap - f.Height);
        if (desired.Y < area.Top) {
            if (Bottom + gap + f.Height <= area.Bottom) desired.Y = Bottom + gap;
            else if (Right + gap + f.Width <= area.Right) desired = new Point(Right + gap,Top - f.Height/2 + Height/2);
            else if (Left - gap - f.Width >= area.Left) desired = new Point(Left - gap - f.Width,Top - f.Height/2 + Height/2);
        }
        f.Location = Clamp(desired,area,f.Size);
    }
    void Surface(Form f,string title,int width,int height) {
        f.Text = title; f.AccessibleName = title; f.AutoScaleMode = AutoScaleMode.None;
        f.FormBorderStyle = FormBorderStyle.None; f.StartPosition = FormStartPosition.Manual;
        f.ShowInTaskbar = false; f.TopMost = true; f.BackColor = theme.Canvas; f.ForeColor = theme.Ink;
        f.Font = UiFont(9); f.ClientSize = PanelSize(width,height);
        f.Paint += (s,e) => {
            using (var pen = new Pen(theme.Border))
                e.Graphics.DrawRectangle(pen,0,0,f.ClientSize.Width-1,f.ClientSize.Height-1);
        };
    }
    static GraphicsPath BubbleShape(int width,int height) {
        var edge = new GraphicsPath();
        if (width<2 || height<2) return edge;
        int diameter=Math.Min(20,Math.Min(width,height)-1);
        int right=width-1-diameter, bottom=height-1-diameter;
        edge.AddArc(0,0,diameter,diameter,180,90);
        edge.AddArc(right,0,diameter,diameter,270,90);
        edge.AddArc(right,bottom,diameter,diameter,0,90);
        edge.AddArc(0,bottom,diameter,diameter,90,90);
        edge.CloseFigure(); return edge;
    }
    Label Caption(Control owner, string words, int x, int y, int w, int h, float size, bool bold) {
        var label = new Label { Text = words, ForeColor = theme.Ink, Font = UiFont(size,bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft };
        BoundsInUnits(label,x,y,w,h); owner.Controls.Add(label); return label;
    }
    Button ButtonAt(Control owner, string text,int x,int y,int w,int h, Action action,bool primary) {
        var b = new Button { Text = text, AccessibleName = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Tag=primary, BackColor = primary ? theme.Accent : theme.Soft, ForeColor = primary ? theme.OnAccent : theme.Ink, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; BoundsInUnits(b,x,y,w,h); owner.Controls.Add(b);
        b.Click += delegate { action(); }; return b;
    }
    void ShowEditor() {
        if (petHidden) TogglePet();
        notice.Hide(); Place(editor);
        if (!editor.Visible) WithPanelDpi(() => editor.Show());
        editor.Activate(); input.Focus();
    }

    void BuildEditor() {
        Surface(editor,"Blink",410,433);
        petName = Caption(editor,"Blink",16,7,142,36,17,true); petName.AutoEllipsis = true;
        BoundsInUnits(model,164,12,166,27); model.Text = "Checking local AI…"; model.TextAlign = ContentAlignment.MiddleLeft;
        model.ForeColor = theme.Accent; model.AutoEllipsis = true; editor.Controls.Add(model);
        model.Cursor = Cursors.Hand; model.Click += async delegate { await CheckModel(); Say(model.Text); };
        ButtonAt(editor,"Hide",340,12,54,26,() => editor.Hide(),false);
        var chatArea = new Panel { BackColor = theme.Incoming, Padding = new Padding(Px(10),Px(8),Px(10),Px(8)) };
        BoundsInUnits(chatArea,16,54,378,121); editor.Controls.Add(chatArea);
        editorMotion = new ConversationMotion(editor,chatArea);
        conversation.Dock = DockStyle.Fill; conversation.BackColor = theme.Incoming;
        conversation.ForeColor = theme.Ink; conversation.ReadOnly = true; conversation.AccessibleName = "Conversation";
        conversation.BorderStyle = BorderStyle.None; conversation.DetectUrls = false;
        conversation.Font = UiFont(10); chatArea.Controls.Add(conversation);
        Say(profile.Greeting());
        BoundsInUnits(input,16,185,306,30); input.Font = UiFont(11); input.MaxLength = 1200;
        input.AccessibleName = "Message"; editor.Controls.Add(input);
        input.HandleCreated += delegate { SendMessageNative(input.Handle,0x1501,new IntPtr(1),"Message Blink…"); };
        send = ButtonAt(editor,"Send",330,183,64,30,async () => await SendMessage(),true);
        input.KeyDown += async (s,e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendMessage(); } };
        ButtonAt(editor,"Set manually",16,222,113,29,Manual,false);
        ButtonAt(editor,"Test in 10 seconds",137,222,151,29,TestReminder,false);
        ButtonAt(editor,"Hide pet",299,222,95,29,TogglePet,false);
        taskHeading = Caption(editor,"Active tasks",16,263,238,25,11,true);
        messagesButton = ButtonAt(editor,"Messages",150,261,112,29,() => OpenInbox(),false);
        ButtonAt(editor,"Customize pet",271,261,123,29,Customize,false);
        tasks.AccessibleName = "Active tasks"; tasks.DisplayMember = "Title";
        BoundsInUnits(tasks,16,293,378,83); tasks.BackColor = theme.Incoming; tasks.Font = UiFont(10);
        tasks.IntegralHeight = false; tasks.HorizontalScrollbar = true; editor.Controls.Add(tasks);
        var manage = new ContextMenuStrip();
        manage.Items.Add("Remove task",null,(s,e) => RemoveSelected());
        tasks.ContextMenuStrip = manage;
        tasks.MouseDown += (s,e) => {
            if (e.Button == MouseButtons.Right) tasks.SelectedIndex = tasks.IndexFromPoint(e.Location);
        };
        tasks.KeyDown += (s,e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        tasks.SelectedIndexChanged += delegate {
            if (refreshing) return;
            var t = tasks.SelectedItem as TaskItem;
            focusId = t == null ? null : t.Id;
            if (t != null) Say(Describe(t));
        };
        ButtonAt(editor,"Snooze 10 min",16,388,117,29,() => Quick("snooze",ActiveId()),false);
        ButtonAt(editor,"Draft ready",140,388,91,29,() => Quick("draft",ActiveId()),false);
        ButtonAt(editor,"Finish task",238,388,98,29,() => {
            var t = Get(ActiveId()); if (t == null) { Say("Select a task first."); return; }
            if (t.Submission && MessageBox.Show(editor,"Have you submitted “" + t.Title + "”?","Confirm submission", MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) return;
            Quick(t.Submission ? "submitted" : "done",t.Id);
        },true);
        editor.KeyPreview = true; editor.KeyDown += (s,e) => { if (e.KeyCode == Keys.Escape) editor.Hide(); };
        editor.FormClosing += (s,e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; editor.Hide(); } };
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]
    static extern IntPtr SendMessageNative(IntPtr window,int message,IntPtr value,string text);
    async Task CheckModel() {
        try {
            var checking=Language.ModelStatus();
            string state;
            using (checking.IsCompleted ? null : editorMotion.BeginBusy(theme,profile.EyeHeight))
                state=await checking;
            if (editor.IsDisposed) return;
            model.Text = state; hints.SetToolTip(model,state + " · Click to check again.");
        } catch { if (!editor.IsDisposed) model.Text = "Local AI unavailable"; }
    }
    void Line(string who,string message) {
        if (conversation.TextLength > 18000) conversation.Clear();
        if (conversation.TextLength > 0) conversation.AppendText("\n\n");
        conversation.SelectionColor = theme.Accent; conversation.AppendText(who + "   ");
        conversation.SelectionColor = theme.Ink; conversation.AppendText(message);
        conversation.SelectionStart = conversation.TextLength; conversation.ScrollToCaret();
    }
    void Say(string text) { Line(profile.Name,text); }
    TaskItem Get(string id) { return store.FindActive(id); }
    string ActiveId() { return focusId; }
    void RemoveSelected() {
        var t = Get(focusId);
        if (t == null || MessageBox.Show(editor,"Remove “" + t.Title + "” and its reminders?","Remove task",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { store.Delete(t.Id); RefreshTasks(null); SyncInbox(); Say("Removed " + t.Title + "."); }
        catch(Exception e) { SetExpression(EyeExpression.Sad); Say("I couldn’t remove that task. " + e.Message); }
    }
    void RefreshTasks(string id) {
        refreshing = true; tasks.BeginUpdate();
        try {
            tasks.Items.Clear();
            foreach (var t in store.Items.Where(t=>t.Stage!="done").OrderBy(t=>t.DeadlineUtc ?? t.CheckUtc ?? DateTime.MaxValue)) {
                int index = tasks.Items.Add(t);
                if (t.Id == id) tasks.SelectedIndex = index;
            }
            focusId = Get(id) == null ? null : id;
            int count = tasks.Items.Count;
            taskHeading.Text = count == 0 ? "No active tasks" : "Active tasks  ·  " + count;
            foreach (Control control in editor.Controls)
                if (control.Top >= tasks.Top) control.Visible = count > 0;
            editor.ClientSize = PanelSize(410,count == 0 ? 303 : 433);
            if (editor.Visible) Place(editor);
        } finally { tasks.EndUpdate(); refreshing = false; }
    }
    static string Time(DateTime utc) { return utc.ToLocalTime().ToString("ddd d MMM, h:mm tt"); }
    static string Describe(TaskItem t) {
        var parts = new List<string> { t.Title + (t.Stage == "draft ready" ? " (draft ready)" : "") };
        if (!String.IsNullOrWhiteSpace(t.File) && t.Title.IndexOf(t.File,StringComparison.OrdinalIgnoreCase)<0) parts.Add("File: " + t.File);
        if (t.CheckUtc.HasValue) parts.Add("Check-in " + Time(t.CheckUtc.Value));
        if (t.DeadlineUtc.HasValue) parts.Add("Due " + Time(t.DeadlineUtc.Value));
        if (t.WarnUtc.HasValue) parts.Add("Heads-up " + Time(t.WarnUtc.Value));
        return String.Join(" · ",parts);
    }
    bool Persist(TaskItem t) {
        try { store.Save(t); RefreshTasks(t.Id); SetExpression(EyeExpression.Happy); return true; }
        catch(Exception e) { SetExpression(EyeExpression.Sad); Say("I couldn’t save that change. " + e.Message); ShowEditor(); return false; }
    }
    static TaskItem NewItem(string title) {
        return new TaskItem { Id = Guid.NewGuid().ToString("N"), Title = title, File = "", Stage = "working" };
    }
    static void SetDeadline(TaskItem t,DateTime deadline) {
        t.DeadlineUtc = deadline;
        t.WarnUtc = deadline.AddMinutes(-30) > DateTime.UtcNow ? (DateTime?)deadline.AddMinutes(-30) : null;
    }
    void TestReminder() {
        var t = NewItem("Your 10-second test"); t.Id = "test-" + t.Id; t.CheckUtc = DateTime.UtcNow.AddSeconds(10);
        if (Persist(t)) Say("Test set. Hide this bubble and I’ll message you in 10 seconds.");
    }

    sealed class ColorChoice : RadioButton {
        readonly PetTheme palette;
        public ColorChoice(string name,PetTheme colors) {
            Text=name; AccessibleName=name; palette=colors;
            AutoSize=false; Appearance=Appearance.Button; FlatStyle=FlatStyle.Flat;
            Cursor=Cursors.Hand; Font=UiFont(8);
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Parent==null ? BackColor : Parent.BackColor);
            using (var shape=BubbleShape(Width,Height))
            using (var fill=new SolidBrush(Checked ? palette.Outgoing : palette.Soft))
            using (var edge=new Pen(Checked ? palette.Accent : palette.Border,Checked?Px(2):1)) {
                g.FillPath(fill,shape); g.DrawPath(edge,shape);
            }
            int side=Math.Min(Px(31),Height/2);
            PetArtwork.Draw(g,new Rectangle((Width-side)/2,Px(6),side,side),palette.Coat,false,false,false,false,13);
            if (Checked) using (var mark=new Pen(palette.Accent,Math.Max(1,Px(2)))) {
                g.DrawLines(mark,new[]{ new Point(Width-Px(13),Px(9)),new Point(Width-Px(10),Px(12)),new Point(Width-Px(5),Px(6)) });
            }
            var caption=new Rectangle(Px(2),Px(37),Width-Px(4),Height-Px(38));
            TextRenderer.DrawText(g,Text,Font,caption,palette.Ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle(3,3,Width-6,Height-6));
        }
    }
    void Customize() {
        using (var dialog = new Form()) {
            dialog.Text = "Customize pet"; dialog.AccessibleName = dialog.Text;
            Form owner=notice.Visible ? notice : editor.Visible ? editor : Visible ? this : null;
            dialog.AutoScaleMode = AutoScaleMode.None; dialog.Font = editor.Font;
            dialog.StartPosition = owner==null || owner==this ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.BackColor = theme.Canvas;
            dialog.ShowInTaskbar = false; dialog.MaximizeBox = false; dialog.MinimizeBox = false;
            dialog.TopMost = true; dialog.ClientSize = PanelSize(357,487);
            Caption(dialog,"Pet name",16,9,320,20,10,true);
            var name = new TextBox { AccessibleName = "Pet name", Text = profile.Name, MaxLength = 32 };
            BoundsInUnits(name,16,32,325,26); dialog.Controls.Add(name);
            Caption(dialog,"Characteristic",16,66,320,20,10,true);
            var trait = new ComboBox { AccessibleName = "Characteristic", MaxLength = 160 };
            trait.Items.AddRange(new string[] { "Curious","Sleepy","Energetic" });
            trait.Text = profile.Character; BoundsInUnits(trait,16,90,325,27); dialog.Controls.Add(trait);
            Caption(dialog,"Personality",16,127,320,20,10,true);
            var tone = Choose(dialog,"Personality",150,PetProfile.Personalities,profile.Personality);
            Caption(dialog,"Pet and chat color",16,187,320,20,10,true);
            const int paletteWidth=325, swatchGap=5;
            var choices = new Panel { AccessibleName="Appearance" }; BoundsInUnits(choices,16,211,paletteWidth,68); dialog.Controls.Add(choices);
            var preview = new Panel { AccessibleName="Chat color preview" }; BoundsInUnits(preview,16,288,325,52); dialog.Controls.Add(preview);
            var samplePet=Caption(preview,"How’s the report going?",8,6,165,28,9,false);
            var sampleUser=Caption(preview,"Still working",201,18,113,27,9,false);
            samplePet.TextAlign=sampleUser.TextAlign=ContentAlignment.MiddleCenter;
            var previewMotion=new ConversationMotion(dialog,preview);
            var previewTheme=theme;
            Action<PetTheme> showPreview = colors => {
                ApplyTheme(dialog,colors);
                preview.BackColor=colors.Canvas; samplePet.BackColor=colors.Incoming; sampleUser.BackColor=colors.Outgoing;
                samplePet.ForeColor=sampleUser.ForeColor=colors.Ink; choices.Invalidate(true);
                if (dialog.Visible && previewTheme!=colors) previewMotion.Transition(previewTheme,colors,profile.EyeHeight);
                previewTheme=colors;
            };
            string selectedColor=profile.Appearance;
            for (int i=0;i<PetProfile.Appearances.Length;i++) {
                string colorName=PetProfile.Appearances[i];
                var colors=PetTheme.For(colorName);
                var option=new ColorChoice(colorName,colors);
                int left=i*(paletteWidth+swatchGap)/PetProfile.Appearances.Length;
                int right=(i+1)*(paletteWidth+swatchGap)/PetProfile.Appearances.Length-swatchGap;
                BoundsInUnits(option,left,0,right-left,68); choices.Controls.Add(option);
                option.Checked=colorName==selectedColor;
                option.CheckedChanged+=delegate { if (option.Checked) { selectedColor=colorName; showPreview(colors); } };
            }
            Caption(dialog,"Pet size",16,347,320,20,10,true);
            var petSize = Choose(dialog,"Pet size",370,PetProfile.Sizes,profile.SizeChoice);
            var error = Caption(dialog,"",16,401,325,32,9,false);
            var cancel = ButtonAt(dialog,"Cancel",145,437,90,32,() => dialog.Close(),false);
            dialog.CancelButton = cancel;
            var originalTheme=theme;
            bool saved=false;
            dialog.AcceptButton = ButtonAt(dialog,"Save",244,437,97,32,() => {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "Give your pet a name."; name.Focus(); return; }
                var updated = profile.Copy();
                updated.Name = name.Text.Trim(); updated.Character = trait.Text.Trim();
                updated.Personality = (string)tone.SelectedItem; updated.Appearance = selectedColor;
                updated.SizeChoice = (string)petSize.SelectedItem;
                try {
                    updated.Save(dataDirectory); profile = updated; ApplyProfile();
                    saved=true; dialog.Close();
                } catch(Exception e) { SetExpression(EyeExpression.Sad); error.Text = e.Message; }
            },true);
            showPreview(theme);
            WithPanelDpi(() => { if (owner==null) dialog.ShowDialog(); else dialog.ShowDialog(owner); });
            if (owner==notice && notice.Visible && replyBox.Enabled && replyBox.TextLength>0) replyBox.Focus();
            if (owner==editor && editor.Visible && input.Enabled && input.TextLength>0) input.Focus();
            if (saved && originalTheme!=theme) {
                if (owner==this && Visible && !petHidden && !notice.Visible && !editor.Visible) OpenInbox();
                if (notice.Visible) chatMotion.Transition(originalTheme,theme,profile.EyeHeight);
                else if (editor.Visible) editorMotion.Transition(originalTheme,theme,profile.EyeHeight);
            }
        }
    }
    static ComboBox Choose(Form owner,string label,int y,string[] values,string selected) {
        var box = new ComboBox { AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(values); box.SelectedItem = selected; BoundsInUnits(box,16,y,325,27);
        owner.Controls.Add(box); return box;
    }
    void EditHistoryPeriod() {
        using (var dialog=new Form()) {
            dialog.Text="Conversation history"; dialog.AccessibleName=dialog.Text;
            dialog.AutoScaleMode=AutoScaleMode.None; dialog.Font=editor.Font;
            dialog.FormBorderStyle=FormBorderStyle.FixedDialog; dialog.StartPosition=FormStartPosition.CenterParent;
            dialog.ShowInTaskbar=false; dialog.TopMost=true; dialog.MinimizeBox=false; dialog.MaximizeBox=false;
            dialog.ClientSize=PanelSize(347,225);
            Caption(dialog,"History period",16,12,162,20,10,true);
            Caption(dialog,"Start day",190,12,139,20,10,true);
            var period=Choose(dialog,"History period",36,PetProfile.HistoryPeriods,profile.HistoryPeriod);
            BoundsInUnits(period,16,36,151,28);
            var day=new ComboBox { AccessibleName="Starting day of month", DropDownStyle=ComboBoxStyle.DropDownList };
            for (int i=1;i<=31;i++) day.Items.Add(i);
            day.SelectedItem=profile.HistoryStartDay; BoundsInUnits(day,190,36,139,28); dialog.Controls.Add(day);

            Caption(dialog,"Current period",16,81,314,20,10,true);
            var dates=Caption(dialog,"",16,103,314,24,11,false);
            var hint=Caption(dialog,"",16,131,314,42,9,false);
            var draft=profile.Copy();
            Action preview=delegate {
                draft.HistoryPeriod=(string)period.SelectedItem;
                draft.HistoryStartDay=(int)day.SelectedItem;
                var window=draft.HistoryAt(DateTime.Today);
                dates.Text=window.Start.ToString("d MMM yyyy")+" to "+window.End.AddDays(-1).ToString("d MMM yyyy");
                hint.Text=draft.HistoryPeriod=="6 months" ? "Each period starts in January or July." :
                    draft.HistoryPeriod=="Yearly" ? "Each year starts in January." :
                    draft.HistoryStartDay>28 ? "Shorter months use their last day." : "";
            };
            period.SelectedIndexChanged+=delegate { preview(); };
            day.SelectedIndexChanged+=delegate { preview(); };
            preview();
            var cancel=ButtonAt(dialog,"Cancel",136,177,87,32,() => dialog.Close(),false);
            dialog.CancelButton=cancel;
            dialog.AcceptButton=ButtonAt(dialog,"Save",234,177,95,32,() => {
                var updated=profile.Copy();
                updated.HistoryPeriod=draft.HistoryPeriod; updated.HistoryStartDay=draft.HistoryStartDay;
                try { updated.Save(dataDirectory); }
                catch (Exception e) { hint.Text="Couldn’t save your history settings. "+e.Message; return; }
                profile=updated;
                dialog.DialogResult=DialogResult.OK;
            },true);
            ApplyTheme(dialog,theme);
            WithPanelDpi(() => dialog.ShowDialog(notice));
            if (dialog.DialogResult==DialogResult.OK && !notice.IsDisposed) ResetHistoryView();
        }
    }
    void Manual() {
        using(var dialog = new Form()) {
            dialog.Text = "Set a reminder"; dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.AutoScaleMode = AutoScaleMode.None; dialog.Font = editor.Font; dialog.BackColor = theme.Canvas; dialog.ForeColor = theme.Ink;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.TopMost = true;
            dialog.MinimizeBox = false; dialog.MaximizeBox = false; dialog.ClientSize = PanelSize(365,265);
            Caption(dialog,"Task",14,9,318,22,10,true);
            var name = new TextBox { Text = input.Text.Trim(), AccessibleName = "Task", MaxLength = 500 };
            BoundsInUnits(name,14,37,337,25); dialog.Controls.Add(name);
            var check = DateField(dialog,"Check-in",77,DateTime.Now.AddMinutes(10),true);
            var due = DateField(dialog,"Deadline",114,DateTime.Now.AddDays(1),false);
            var upload = new CheckBox { Text = "This requires a submission" }; BoundsInUnits(upload,14,154,330,26); dialog.Controls.Add(upload);
            var error = Caption(dialog,"",14,181,335,34,9,false);
            ButtonAt(dialog,"Save reminder",194,220,156,31,() => {
                var t = NewItem(name.Text.Trim());
                if (t.Title.Length == 0 || (!check.Checked && !due.Checked)) { error.Text = "Give the task a name and select at least one time."; return; }
                if ((check.Checked && check.Value <= DateTime.Now) || (due.Checked && due.Value <= DateTime.Now)) { error.Text = "Choose a future time for each selected event."; return; }
                t.Submission = upload.Checked;
                if (check.Checked) t.CheckUtc = check.Value.ToUniversalTime();
                if (due.Checked) SetDeadline(t,due.Value.ToUniversalTime());
                if (Persist(t)) { input.Clear(); Say("Saved. " + Describe(t)); dialog.Close(); }
            },true);
            ApplyTheme(dialog,theme);
            WithPanelDpi(() => dialog.ShowDialog(editor));
        }
    }
    DateTimePicker DateField(Form parent,string name,int y,DateTime time,bool enabled) {
        Caption(parent,name,14,y,78,26,9,false);
        var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd dd MMM yyyy   HH:mm:ss", Value = time, ShowCheckBox = true, Checked = enabled, AccessibleName = name };
        BoundsInUnits(picker,96,y,255,28); parent.Controls.Add(picker); return picker;
    }
    bool TryOpenChecklist(out string error) {
        try {
            store.RefreshChecklist();
            using (var viewer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(store.ChecklistPath) { UseShellExecute=true })) { }
            error=null; return true;
        } catch(Exception e) {
            error="I couldn’t open the checklist at "+store.ChecklistPath+". "+e.Message;
            return false;
        }
    }
    static bool ChecklistOpenRequest(string words) {
        return Regex.IsMatch(words,@"^\s*(?:(?:can|could|would) you\s+)?(?:please\s+)?open\s+(?:(?:my|the|blink)\s+)?check\s*list(?:\s+file)?[.!?]?\s*$",RegexOptions.IgnoreCase);
    }
    ChatPrompt ReadQuestion(string messageId) {
        var saved = store.Question(messageId);
        if (saved != null) return saved;
        // Older builds saved the visible exchange without its canonical question.
        var tail = historySnapshot.AsEnumerable().Reverse().Where(e=>e.MessageId==messageId).Take(2).ToArray();
        if (tail.Length == 2 && tail[0].FromPet && !tail[1].FromPet &&
            Regex.IsMatch(tail[0].Text,@"^(?:Which date do you mean|That time has passed|What future date|What time on |Do you mean |Which task do you mean\?|Did you submit )",RegexOptions.IgnoreCase))
            return new ChatPrompt { Context="User: "+tail[1].Text+"\nBlink: "+tail[0].Text };
        return null;
    }
    static bool LooksLikeReplyAction(string words) {
        return Regex.IsMatch(words,@"\b(snooze|remind|check again|check in|later|finished|finish|submitted|submit|uploaded|upload|done|completed|draft|exported|working|reschedule|postpone|move|mark|cancel|rename)\b",RegexOptions.IgnoreCase);
    }
    async Task<bool> SendMessage(string typed = null,ConversationChoice chat = null) {
        string words = (typed ?? input.Text).Trim();
        if (busy || words.Length == 0) return false;
        var target = chat ?? new ConversationChoice { MessageId=GlobalChat,TaskId=GlobalChat };
        string messageId = target.MessageId;
        bool newRequest = Language.IsNewRequest(words);
        var saved = newRequest ? null : ReadQuestion(messageId);
        string thread = saved == null ? "" : saved.Context;
        string selection = saved != null && saved.SelectedTaskId != null ? saved.SelectedTaskId :
            target.TaskId == GlobalChat ? focusId : target.TaskId;
        var current = Get(selection);
        busy = true; send.Enabled = false; RefreshPet();
        try {
            if (ChecklistOpenRequest(words)) {
                string error;
                string result=TryOpenChecklist(out error)?"Opened your checklist at "+store.ChecklistPath+".":error;
                store.Chat(messageId,target.TaskId,words,result); Line("You",words); Say(result); SyncAfterChat(messageId);
                if (chat==null) input.Clear(); return true;
            }
            Intent command = Language.ReminderListQuery(words,DateTime.Now,store.Items);
            if (command == null && target.Pending && saved == null && !newRequest && !LooksLikeReplyAction(words)) {
                var pet = historySnapshot.LastOrDefault(e=>e.MessageId==messageId && e.FromPet);
                bool submitted = current != null && current.Submission && pet != null &&
                    Regex.IsMatch(words,@"^(?:yes|yep|yeah)[.!]?$",RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(pet.Text,@"(?:have you submitted|did you (?:manage to )?submit|did (?:the|your) upload go through)",RegexOptions.IgnoreCase);
                command = new Intent {Action=submitted?"submitted":"ack",Id=target.TaskId};
            }
            if (command == null) {
                string request = words;
                if (saved!=null && Regex.IsMatch(thread,@"^User: (?:snooze|check (?:in )?again|remind me again)",RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(words,@"^(?:in )?(?:an?|one|\d+) (?:min(?:ute)?s?|hours?|days?)\.?$",RegexOptions.IgnoreCase))
                    request = "Snooze " + words;
                var reading = Language.Read(request,DateTime.Now,store.Items.Where(t=>t.Stage!="done").ToList(),current,thread,profile);
                if (reading.IsCompleted) command = await reading;
                else using (chatMotion.BeginBusy(theme,profile.EyeHeight))
                    using (editorMotion.BeginBusy(theme,profile.EyeHeight)) command = await reading;
            }
            if (editor.IsDisposed || notice.IsDisposed) return false;
            if (command == null) command = new Intent { Action="ask",Reply="What task and time should I use?" };
            bool applied = CommitCommand(target,words,command,selection,thread);
            if (applied && chat == null) input.Clear();
            return applied;
        } catch (Exception) {
            if (!editor.IsDisposed) {
                SetExpression(EyeExpression.Sad);
                messageStatus.Text="I couldn’t save that change. Your message is still here.";
                if (chat==null) Say(messageStatus.Text);
            }
            return false;
        } finally { if (!editor.IsDisposed) { busy=false; send.Enabled=true; RefreshPet(); } }
    }
    bool CommitCommand(ConversationChoice conversation,string words,Intent command,string selection,string previous) {
        string id = conversation.MessageId;
        string reply = command.Reply;
        var task = command.Action=="create" ? NewItem(command.Title) : Get(command.Id ?? selection);
        bool answer = conversation.Pending && task!=null && task.Id==conversation.TaskId;
        TaskItem change = null;
        ChatPrompt question = null;
        if (command.Action=="triage") {
            task = task ?? store.Items.Where(x=>x.Stage!="done").OrderBy(x=>x.DeadlineUtc ?? x.CheckUtc ?? DateTime.MaxValue).FirstOrDefault();
            reply = task==null ? "You have no active tasks." : "Start with "+task.Title+". "+
                (task.DeadlineUtc.HasValue ? "It’s due "+Time(task.DeadlineUtc.Value)+"." : task.CheckUtc.HasValue ? "Your check-in is "+Time(task.CheckUtc.Value)+"." : "It’s an open task.");
            answer=false;
        } else if (command.Action=="list") {
            answer=false;
        } else if (command.Action!="ask") {
            if (task==null) { command.Action="ask"; reply="Which active task do you mean?"; }
            else if (command.Action=="done" && task.Submission) {
                command.Action="ask"; reply="Did you submit "+task.Title+"? Reply “I submitted it” once the upload is complete.";
            } else if (command.CheckUtc<=DateTime.UtcNow || command.DeadlineUtc<=DateTime.UtcNow) {
                command.Action="ask"; reply="That time has passed. What future date and time should I use?";
            } else {
                switch(command.Action) {
                    case "working":
                    case "ack":
                        reply=command.Action=="working" ? "You’re still working on "+task.Title+"." : "Got it.";
                        if (answer && task.Id.StartsWith("test-")) { task.Stage="done"; change=task; }
                        break;
                    case "draft":
                        task.Stage="draft ready"; task.CheckUtc=null; change=task;
                        reply=task.Submission?"Draft ready. I’ll keep the upload deadline on your list.":"I’ve marked the draft ready."; break;
                    case "submitted":
                    case "done":
                        task.Stage="done"; task.CheckUtc=null; task.WarnUtc=null; change=task;
                        reply=(task.Submission?"Marked submitted: ":"Completed: ")+task.Title+"."; break;
                    case "cancel":
                        task.Stage="done"; task.CheckUtc=null; task.WarnUtc=null; task.DeadlineUtc=null; change=task;
                        reply="Canceled “"+task.Title+"” and its pending reminders."; break;
                    case "rename":
                        if (String.IsNullOrWhiteSpace(command.Title)) { command.Action="ask"; reply="What new title should I use?"; break; }
                        task.Title=command.Title.Trim(); change=task; reply="Renamed the task to “"+task.Title+"”."; break;
                    case "create":
                    case "edit":
                    case "snooze":
                        if (command.Action=="snooze" && !command.CheckUtc.HasValue) { command.Action="ask"; reply="When should I check in again?"; break; }
                        if (command.DeadlineUtc.HasValue && command.Action!="snooze") SetDeadline(task,command.DeadlineUtc.Value);
                        if (command.CheckUtc.HasValue) task.CheckUtc=command.CheckUtc;
                        if (command.Action!="snooze") {
                            if (!String.IsNullOrWhiteSpace(command.Title)) task.Title=command.Title;
                            if (!String.IsNullOrWhiteSpace(command.File)) task.File=command.File;
                            task.Submission |= command.Submission;
                        }
                        if (String.IsNullOrWhiteSpace(task.Title)) { command.Action="ask"; reply="What should I call the task?"; break; }
                        change=task;
                        reply=command.Action=="snooze" ? "I’ll message you again at "+Time(task.CheckUtc.Value)+"." :
                            !task.CheckUtc.HasValue && !task.DeadlineUtc.HasValue ? "Added “"+task.Title+"” to your task list." : "Saved. "+Describe(task);
                        break;
                    default:
                        command.Action="ask"; reply="Which task should I add or change?"; break;
                }
            }
        }
        if (command.Action=="ask") {
            answer=false;
            if (String.IsNullOrWhiteSpace(reply)) reply="What should I use for this reminder?";
            question = new ChatPrompt {
                Context=(String.IsNullOrWhiteSpace(previous)?"":previous+"\n")+"User: "+words+"\nBlink: "+(command.ContextReply ?? reply),
                SelectedTaskId=command.Id ?? selection
            };
        }
        if (answer) {
            if (!store.Answer(id,words,change,reply)) { SyncInbox(); return false; }
        } else store.CommitChat(id,conversation.TaskId,words,reply,change,question);
        if (question!=null) SetExpression(EyeExpression.Confused);
        else if (command.Action=="done" || command.Action=="submitted" || command.Action=="create") SetExpression(EyeExpression.Happy);
        Line("You",words); Say(reply);
        if (change!=null || selection!=focusId) RefreshTasks(change==null ? selection : change.Id);
        SyncAfterChat(id); return true;
    }
    void Quick(string action,string id,Action<string> respond = null) {
        respond = respond ?? Say;
        var t = Get(id);
        if (t == null) { ShowEditor(); Say("Select an active task first."); return; }
        string reply;
        switch(action) {
            case "snooze":
                t.CheckUtc = DateTime.UtcNow.AddMinutes(10);
                reply = "I’ll check in on " + t.Title + " at " + Time(t.CheckUtc.Value) + ".";
                break;
            case "done":
            case "draft":
            case "submitted":
                if ((t.Submission && action != "submitted") || action == "draft") {
                    t.Stage = "draft ready"; t.CheckUtc = null;
                    reply = "Draft ready. " + (t.Submission ? "I’ll keep the submission open until you confirm the upload." : "I’ve cleared the writing check-in.");
                } else {
                    t.Stage = "done"; t.CheckUtc = null; t.WarnUtc = null;
                    reply = (t.Submission ? "Marked submitted: " : "Completed: ") + t.Title + ".";
                }
                break;
            default: return;
        }
        if (Persist(t)) { SyncInbox(); respond(reply); }
    }
    sealed class ConversationChoice {
        public string MessageId, TaskId;
        public bool Pending;

    }
    ConversationChoice SelectedConversation;

    void BuildNotice() {
        Surface(notice,"Blink messages",388,418);
        notice.BackColor = theme.Canvas;
        BoundsInUnits(avatar,14,12,38,38); avatar.BackColor = notice.BackColor; notice.Controls.Add(avatar);
        avatar.FlatStyle=FlatStyle.Flat; avatar.FlatAppearance.BorderSize=0; avatar.UseVisualStyleBackColor=false;
        avatar.AccessibleName="Customize pet"; avatar.Cursor=Cursors.Hand;
        hints.SetToolTip(avatar,"Customize pet"); avatar.Click+=(s,e)=>Customize();
        avatar.Paint += (s,e) => PetArtwork.Draw(e.Graphics,avatar.ClientRectangle,profile.GetColor(),urgent,false,false,false,profile.EyeHeight,CurrentExpression());
        noticeName = Caption(notice,profile.Name,61,8,255,28,14,true);
        noticeName.AutoEllipsis = true;
        closeChat = ButtonAt(notice,"×",344,13,30,30,() => notice.Hide(),false);
        closeChat.AccessibleName = "Close conversation"; closeChat.Font = UiFont(15);
        closeChat.BackColor = theme.Soft;
        hints.SetToolTip(closeChat,"Close conversation (Esc)");
        BoundsInUnits(messageStatus,62,35,267,19); messageStatus.ForeColor = theme.Accent;
        messageStatus.Font = UiFont(8.5F);
        messageStatus.AutoEllipsis = true; notice.Controls.Add(messageStatus);
        chatRule.BackColor = theme.Border;
        BoundsInUnits(chatRule,1,63,386,1); notice.Controls.Add(chatRule);
        upcoming = ButtonAt(notice,"Upcoming",181,74,81,26,ShowUpcoming,false);
        newReminder = ButtonAt(notice,"New reminder",269,74,104,26,ShowEditor,false);
        notice.Controls.Add(conversationCaption);
        previousPeriod=ButtonAt(notice,"‹",14,105,26,24,()=>MoveHistoryPeriod(-1),false);
        historyRange=ButtonAt(notice,"",44,105,194,24,EditHistoryPeriod,false);
        nextPeriod=ButtonAt(notice,"›",242,105,26,24,()=>MoveHistoryPeriod(1),false);
        olderMessages=ButtonAt(notice,"Older",276,105,46,24,()=>MoveHistoryPage(1),false);
        newerMessages=ButtonAt(notice,"Newer",326,105,46,24,()=>MoveHistoryPage(-1),false);
        previousPeriod.AccessibleName="Previous period"; nextPeriod.AccessibleName="Next period";
        historyRange.AccessibleName="History settings";
        olderMessages.AccessibleName="Older messages"; newerMessages.AccessibleName="Newer messages";
        foreach (var nav in new[]{previousPeriod,nextPeriod,olderMessages,newerMessages}) hints.SetToolTip(nav,nav.AccessibleName);
        hints.SetToolTip(historyRange,"Change conversation history period");
        BoundsInUnits(messageThread,10,110,368,220); messageThread.AutoScroll = true;
        messageThread.AccessibleName = "Message thread";
        messageThread.BackColor = notice.BackColor; notice.Controls.Add(messageThread);
        emptyHistory.AccessibleName="Conversation history status"; emptyHistory.TextAlign=ContentAlignment.MiddleCenter;
        emptyHistory.Visible=false; notice.Controls.Add(emptyHistory); emptyHistory.BringToFront();
        chatMotion = new ConversationMotion(notice,messageThread);

        acknowledge = ButtonAt(notice,"Still working",16,336,106,29,() => AnswerQuick("working","Still working"),false);
        snoozeReply = ButtonAt(notice,"Snooze 10 min",129,336,118,29,() => AnswerQuick("snooze","Snooze 10 min"),false);
        finish = ButtonAt(notice,"Done",254,336,140,29,() => {
            var current = SelectedConversation;
            var t = current == null ? null : Get(current.TaskId);
            if (t != null) AnswerQuick(t.Submission ? "submitted" : "done",t.Submission ? "I submitted it" : "Done");
        },false);
        BoundsInUnits(composer,12,362,364,44);
        composer.BackColor = theme.Soft; notice.Controls.Add(composer);
        replyBox.Font = UiFont(10.5F); replyBox.BorderStyle = BorderStyle.None;
        replyBox.BackColor = composer.BackColor; replyBox.ForeColor = theme.Ink;
        BoundsInUnits(replyBox,12,12,285,24);
        replyBox.MaxLength = 1200; replyBox.AccessibleName = "Reply"; composer.Controls.Add(replyBox);
        replyBox.HandleCreated += delegate { SendMessageNative(replyBox.Handle,0x1501,new IntPtr(1),"Write a reply…"); };
        sendReply = ButtonAt(composer,"↑",325,6,32,32,async () => await SendThreadReply(),true);
        sendReply.AccessibleName = "Send reply"; sendReply.Font = UiFont(13,FontStyle.Bold); hints.SetToolTip(sendReply,"Send reply (Enter)");
        replyBox.KeyDown += async (s,e) => {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendThreadReply(); }
        };
        notice.KeyPreview = true;
        notice.KeyDown += (s,e) => { if (e.KeyCode == Keys.Escape) notice.Hide(); };
        notice.FormClosing += (s,e) => {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; notice.Hide(); }
        };
    }
    struct ChatGrid {
        public readonly int Dpi;
        public ChatGrid(int dpi) { Dpi = dpi; }
        public int Px(int units) { return (int)Math.Round(units*Dpi/96.0); }
        public int Units(int pixels) { return (int)Math.Round(pixels*96.0/Dpi); }
        public void Place(Control target,int x,int y,int width,int height) {
            target.SetBounds(Px(x),Px(y),Px(x+width)-Px(x),Px(y+height)-Px(y));
        }
    }
    int lastChatDpi;
    bool lastActions, lastHasPages;
    bool SizeConversation(int messages,bool actions,ChatGrid grid,bool hasPages) {
        bool dpiChanged=lastChatDpi!=grid.Dpi;
        if (dpiChanged) {
            lastChatDpi = grid.Dpi;
            notice.Font = UiFont(9,FontStyle.Regular,grid.Dpi);
            noticeName.Font = UiFont(14,FontStyle.Bold,grid.Dpi);
            messageStatus.Font = UiFont(8.5f,FontStyle.Regular,grid.Dpi);
            closeChat.Font = UiFont(12,FontStyle.Regular,grid.Dpi);
            replyBox.Font = UiFont(10.5f,FontStyle.Regular,grid.Dpi);
            sendReply.Font = UiFont(13,FontStyle.Bold,grid.Dpi);
            foreach (var nav in new[]{historyRange,olderMessages,newerMessages}) nav.Font=UiFont(8.5f,FontStyle.Regular,grid.Dpi);
        }
        var area = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(388,Math.Max(318,grid.Units(area.Width)-12));
        int height = Math.Min(Math.Max(260,grid.Units(area.Height)-12),messages==0 ? 298 : messages==1 ? 351 : 448);
        if (actions && messages<=1) height = Math.Min(grid.Units(area.Height)-12,height+37);
        var targetSize = new Size(grid.Px(width),grid.Px(height));
        bool resized = notice.ClientSize != targetSize;
        if (!resized && !dpiChanged && actions==lastActions && hasPages==lastHasPages) return false;
        lastActions=actions; lastHasPages=hasPages;
        if (resized) notice.ClientSize = targetSize;
        grid.Place(avatar,14,12,38,38);
        grid.Place(noticeName,61,8,width-120,28);
        grid.Place(messageStatus,62,35,width-110,19);
        grid.Place(closeChat,width-44,13,30,30);
        grid.Place(chatRule,1,63,width-2,1);
        grid.Place(newReminder,width-119,74,104,26);
        grid.Place(upcoming,width-207,74,81,26);
        grid.Place(conversationCaption,14,75,width-230,26);
        int navEnd=hasPages ? width-116 : width-14;
        grid.Place(previousPeriod,14,106,26,24); grid.Place(historyRange,44,106,navEnd-74,24);
        grid.Place(nextPeriod,navEnd-26,106,26,24);
        grid.Place(olderMessages,width-111,106,46,24); grid.Place(newerMessages,width-61,106,47,24);
        grid.Place(composer,12,height-56,width-24,44);
        grid.Place(replyBox,12,12,width-88,24);
        grid.Place(sendReply,width-62,6,32,32);
        int y = height-93;
        grid.Place(acknowledge,13,y,90,26); grid.Place(snoozeReply,109,y,104,26);
        grid.Place(finish,219,y,Math.Min(width-232,125),26);
        grid.Place(messageThread,10,140,width-20,(actions ? y : height-56)-147);
        grid.Place(emptyHistory,20,143,width-40,(actions ? y : height-56)-153);
        if (resized && notice.Visible) Place(notice);
        return true;
    }
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd,IntPtr rect,IntPtr region,uint flags);
    void RepaintConversation() {
        if (notice.IsHandleCreated && notice.Visible && !notice.IsDisposed && !notice.Disposing)
            RedrawWindow(notice.Handle,IntPtr.Zero,IntPtr.Zero,0x0001|0x0004|0x0080|0x0100|0x0400);
    }
    int badgeCount=-1;
    void UpdateBadge(bool force=false) {
        if (!force && badgeCount==outstanding.Count) return;
        badgeCount=outstanding.Count;
        urgent = outstanding.Count > 0;
        string caption = profile.Name + (urgent ? " · " + outstanding.Count + " awaiting reply" : " · desktop reminders");
        tray.Text = caption.Substring(0,Math.Min(63,caption.Length));
        AccessibleDescription = urgent ? "Waiting for your reply" : "Desktop reminder pet";
        hints.SetToolTip(this,caption);
        tray.Icon = urgent ? waitingIcon : trayIcon ?? SystemIcons.Information;
        if (messagesButton != null) messagesButton.Text = urgent ? "Messages (" + outstanding.Count + ")" : "Messages";
        UpdatePetRegion();
        RefreshPet();
    }
    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        UpdatePetRegion();
    }
    void UpdatePetRegion() {
        if (profile==null || ClientSize.Width<1 || ClientSize.Height<1 ||
            (petRegionSize==ClientSize && petRegionWaiting==urgent)) return;
        var oldShape = Region; Region = PetArtwork.Shape(ClientRectangle,urgent);
        if (oldShape != null) oldShape.Dispose();
        petRegionSize=ClientSize; petRegionWaiting=urgent;
    }


    long inboxRevision=-1;
    List<ChatEntry> historySnapshot=new List<ChatEntry>();
    List<ChatEntry> periodEntries=new List<ChatEntry>();
    DateTime periodStart, periodEnd, lastHistoryDate;
    DateTime? browsingDate;
    int historyPage;
    string locateMessage;
    const int HistoryPageSize=40;
    bool openingInbox;
    void ResetHistoryView() {
        browsingDate=null; historyPage=0; locateMessage=null;
        SyncInbox();
    }
    void MoveHistoryPeriod(int direction) {
        if (replying) return;
        var live=profile.HistoryAt(DateTime.Now);
        var target=profile.HistoryAt(browsingDate ?? DateTime.Now).Shift(direction);
        if (target.Start>live.Start || target.Start==periodStart) return;
        browsingDate=target.Start==live.Start ? (DateTime?)null : target.Start;
        historyPage=0; locateMessage=null;
        SyncInbox();
    }
    void MoveHistoryPage(int direction) {
        historyPage=Math.Max(0,historyPage+direction);
        RenderNotice();
        if (direction<0) messageThread.AutoScrollPosition=Point.Empty;
    }
    void SyncAfterChat(string messageId) {
        browsingDate=null; historyPage=0; locateMessage=null;
        SyncInbox(messageId);
    }
    bool InHistoryPeriod(DateTime utc) {
        DateTime local=utc.ToLocalTime();
        return local>=periodStart && local<periodEnd;
    }
    void SelectDraft(ConversationChoice selected) {
        string id=selected==null ? null : selected.MessageId;
        if (id==composingFor) return;
        if (composingFor!=null) {
            if (replyBox.TextLength==0) drafts.Remove(composingFor);
            else drafts[composingFor]=replyBox.Text;
        }
        composingFor=id;
        string text;
        replyBox.Text=id!=null && drafts.TryGetValue(id,out text) ? text : "";
        replyBox.SelectionStart=replyBox.TextLength;
    }
    void SyncInbox(string preferred = null) {
        var previous = SelectedConversation;
        string oldId = previous == null ? null : previous.MessageId;
        long version=store.Revision;
        DateTime now=DateTime.Now; lastHistoryDate=now.Date;
        var window=profile.HistoryAt(browsingDate ?? now);
        bool periodChanged=periodStart!=window.Start || periodEnd!=window.End;
        if (periodChanged || version!=inboxRevision) {
            if (version!=inboxRevision) {
                historySnapshot=store.History;
                outstanding.Clear(); outstanding.AddRange(store.Inbox);
            }
            periodStart=window.Start; periodEnd=window.End;
            if (periodChanged) historyPage=0;
            periodEntries=historySnapshot.Where(entry=>InHistoryPeriod(entry.AtUtc)).OrderBy(entry=>entry.AtUtc).ToList();
            inboxRevision=version;
        }
        rebuilding=true;
        try {
            var requested=preferred ?? oldId;
            var pending=outstanding.FirstOrDefault(n=>n.MessageId==requested &&
                (InHistoryPeriod(n.SentUtc) || periodEntries.Any(e=>e.MessageId==requested)));
            var target=pending==null
                ? new ConversationChoice { MessageId=GlobalChat, TaskId=GlobalChat }
                : new ConversationChoice { MessageId=pending.MessageId, TaskId=pending.Id, Pending=true };
            SelectedConversation=target;
            conversationCaption.Text=pending==null ? "Messages" : "Reply to " + pending.Title;
            SelectDraft(target);
        } finally { rebuilding=false; }
        UpdateBadge(); RenderNotice();
    }
    void RenderNotice() {
        if (rebuilding || finish==null || notice.IsDisposed || notice.Disposing || (!notice.Visible && !openingInbox)) return;
        WithPanelDpi(RenderConversation,true);
    }
    sealed class MessageRow {
        public readonly Label Stamp = new Label { UseMnemonic=false };
        public readonly MessageBubble Bubble = new MessageBubble { UseMnemonic=false };
        public ChatEntry Entry;
        public int Top, Bottom, Width, Height;
    }
    List<MessageRow> renderedRows=new List<MessageRow>();
    List<ChatEntry> visibleEntries=new List<ChatEntry>();
    long renderedRevision=-1;
    DateTime renderedStart, renderedEnd;
    int renderedPage=-1, pageCount;
    string renderedId;
    PetTheme renderedTheme;
    static bool SameEntry(ChatEntry a,ChatEntry b) {
        return a!=null && b!=null && a.AtUtc==b.AtUtc && a.FromPet==b.FromPet &&
            a.MessageId==b.MessageId && a.TaskId==b.TaskId && a.Text==b.Text;
    }
    List<ChatEntry> HistoryPage(ConversationChoice current) {
        var entries=periodEntries;
        pageCount=Math.Max(1,(entries.Count+HistoryPageSize-1)/HistoryPageSize);
        if (locateMessage!=null) {
            int at=entries.FindLastIndex(e=>e.MessageId==locateMessage);
            if (at>=0) historyPage=(entries.Count-1-at)/HistoryPageSize;
            locateMessage=null;
        }
        historyPage=Math.Min(historyPage,pageCount-1);
        int end=entries.Count-historyPage*HistoryPageSize;
        int start=Math.Max(0,end-HistoryPageSize);
        return entries.GetRange(start,end-start);
    }
    void RenderConversation() {
        var grid = new ChatGrid(notice.MonitorDpi((int)Math.Round(panelScale*96)));
        var current = SelectedConversation;
        string id=current==null ? null : current.MessageId;
        bool periodChanged=periodStart!=renderedStart || periodEnd!=renderedEnd;
        bool switched=renderedId!=id || periodChanged;
        if (renderedId!=id) historyPage=0;
        bool contentChanged=switched || renderedPage!=historyPage || renderedRevision!=inboxRevision || locateMessage!=null;
        if (contentChanged) visibleEntries=HistoryPage(current);
        switched=switched || renderedPage!=historyPage;
        int oldScroll=-messageThread.AutoScrollPosition.Y;
        bool readToEnd=oldScroll+messageThread.ClientSize.Height>=messageThread.AutoScrollMinSize.Height-grid.Px(4);
        bool canType = current != null && !replying && !busy;
        bool canReply = canType && current.Pending;
        bool actionsVisible = current != null && current.Pending;
        finish.Visible = acknowledge.Visible = snoozeReply.Visible = actionsVisible;
        bool hasPages=pageCount>1;
        olderMessages.Visible=newerMessages.Visible=hasPages;
        olderMessages.Enabled=historyPage+1<pageCount; newerMessages.Enabled=historyPage>0;
        previousPeriod.Enabled=!replying && periodStart.Year>1;
        nextPeriod.Enabled=!replying && browsingDate.HasValue;
        historyRange.Enabled=!replying;
        if (periodChanged) {
            var last=periodEnd.AddDays(-1);
            historyRange.Text=periodStart.ToString(periodStart.Year==last.Year ? "d MMM" : "d MMM yyyy")+" – "+last.ToString("d MMM yyyy");
        }
        bool geometryChanged=SizeConversation(visibleEntries.Count,actionsVisible,grid,hasPages);
        if (contentChanged) {
            var task = current == null ? null : Get(current.TaskId);
            finish.Text = task != null && task.Submission ? "Mark submitted" : "Done"; finish.AccessibleName = finish.Text;
        }
        finish.Enabled = canReply; acknowledge.Enabled = canReply; snoozeReply.Enabled = canReply;
        replyBox.Enabled = canType; sendReply.Enabled = canType;
        messageStatus.Text = current == null ? "No reminder messages yet" :
            current.Pending ? (replying ? "Reading your reply…" : "Waiting for your reply") :
            current.TaskId == GlobalChat ? "Your desktop companion" : "Replied";
        bool styled=renderedTheme!=theme;
        if (contentChanged || geometryChanged || styled) {
            var displayed=visibleEntries;
            emptyHistory.Visible=displayed.Count==0;
            if (displayed.Count==0) {
                emptyHistory.Text=!browsingDate.HasValue && historySnapshot.Count==0 && current!=null && current.TaskId==GlobalChat
                    ? profile.Greeting() : "No messages in this period.";
            }
            if (geometryChanged || styled || switched || displayed.Count!=renderedRows.Count ||
                displayed.Where((entry,i)=>!SameEntry(renderedRows[i].Entry,entry)).Any()) {
                UpdateRows(displayed,grid,geometryChanged,styled,oldScroll,switched || readToEnd);
            }
            renderedRevision=inboxRevision; renderedId=id; renderedTheme=theme;
            renderedStart=periodStart; renderedEnd=periodEnd; renderedPage=historyPage;
        }
        if (geometryChanged) RepaintConversation();
    }
    void UpdateRows(List<ChatEntry> entries,ChatGrid grid,bool geometryChanged,bool styled,int oldScroll,bool followEnd) {
        var anchor=renderedRows.FirstOrDefault(row=>row.Bottom>oldScroll);
        var anchorEntry=anchor==null ? null : anchor.Entry;
        int offset=anchor==null ? 0 : oldScroll-anchor.Top;
        var pool=new List<MessageRow>(renderedRows);
        var next=new MessageRow[entries.Count];
        // Match stable records first so a rolling 40-message window keeps its existing native controls.
        for (int i=0;i<entries.Count;i++) {
            int index=pool.FindIndex(row=>SameEntry(row.Entry,entries[i]));
            if (index>=0) { next[i]=pool[index]; pool.RemoveAt(index); }
        }
        int y=grid.Px(8);
        // WinForms scrolls each child HWND separately. Present a batch after all rows have moved.
        bool batch=messageThread.Visible && messageThread.IsHandleCreated && entries.Count>1;
        if (batch) SendMessageNative(messageThread.Handle,0x000B,IntPtr.Zero,null); // WM_SETREDRAW
        messageThread.SuspendLayout();
        try {
            for (int i=0;i<entries.Count;i++) {
                var row=next[i];
                if (row==null) {
                    if (pool.Count>0) { row=pool[pool.Count-1]; pool.RemoveAt(pool.Count-1); }
                    else {
                        row=new MessageRow();
                        messageThread.Controls.Add(row.Stamp); messageThread.Controls.Add(row.Bubble);
                    }
                    next[i]=row;
                }
                ArrangeRow(row,entries[i],y,grid,geometryChanged,styled);
                row.Stamp.TabIndex=i*2; row.Bubble.TabIndex=i*2+1;
                y=row.Bottom;
            }
            foreach (var row in pool) { row.Stamp.Dispose(); row.Bubble.Dispose(); }
            renderedRows=next.ToList();
            messageThread.AutoScrollMinSize=new Size(0,y);
        } finally {
            try {
                messageThread.ResumeLayout(true);
                var kept=followEnd ? null : renderedRows.FirstOrDefault(row=>SameEntry(row.Entry,anchorEntry));
                int scroll=followEnd ? y : kept==null ? 0 : kept.Top+offset;
                messageThread.AutoScrollPosition=new Point(0,Math.Max(0,scroll));
            } finally {
                if (batch) {
                    SendMessageNative(messageThread.Handle,0x000B,new IntPtr(1),null);
                    RedrawWindow(messageThread.Handle,IntPtr.Zero,IntPtr.Zero,0x0001|0x0004|0x0080|0x0400);
                }
            }
        }
    }
    sealed class MessageBubble : Label {
        public const TextFormatFlags LayoutFlags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|
            TextFormatFlags.TextBoxControl|TextFormatFlags.NoPadding;
        public MessageBubble() { SetStyle(ControlStyles.Opaque|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Parent==null ? SystemColors.Window : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = BubbleShape(Width,Height))
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill,shape);
            TextRenderer.DrawText(e.Graphics,Text,Font,
                new Rectangle(Padding.Left,Padding.Top,Width-Padding.Horizontal,Height-Padding.Vertical),
                ForeColor,LayoutFlags);
        }
    }
    void ArrangeRow(MessageRow row,ChatEntry entry,int y,ChatGrid grid,bool geometryChanged,bool styled) {
        bool changed=!SameEntry(row.Entry,entry);
        bool pet = entry.FromPet;
        int available = messageThread.Width-grid.Px(25);
        if (changed || geometryChanged) {
            row.Bubble.Padding=new Padding(grid.Px(9),grid.Px(5),grid.Px(9),grid.Px(5));
            int max=Math.Max(1,Math.Min(grid.Px(264),available-grid.Px(42)));
            var measured=TextRenderer.MeasureText(entry.Text,row.Bubble.Font,new Size(max,Int32.MaxValue),MessageBubble.LayoutFlags);
            row.Width=Math.Max(1,Math.Min(max,measured.Width))+row.Bubble.Padding.Horizontal;
            row.Height=Math.Max(grid.Px(28),measured.Height+grid.Px(12));
            row.Stamp.Font=UiFont(8,FontStyle.Regular,grid.Dpi);
        }
        if (changed || styled) {
            row.Bubble.Text=entry.Text; row.Bubble.AccessibleName=entry.Text;
            row.Bubble.BackColor=pet ? theme.Incoming : theme.Outgoing; row.Bubble.ForeColor=theme.Ink;
            row.Stamp.Text=(pet ? profile.Name : "You")+"   "+entry.AtUtc.ToLocalTime().ToString("MMM d, h:mm tt");
            row.Stamp.TextAlign=pet ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
            row.Stamp.BackColor=theme.Canvas; row.Stamp.ForeColor=theme.Accent;
        }
        int x=pet ? grid.Px(8) : available-row.Width;
        int screenY=y+messageThread.AutoScrollPosition.Y;
        row.Stamp.SetBounds(pet?x:1,screenY,pet?available:available-1,grid.Px(18));
        row.Bubble.SetBounds(x,screenY+grid.Px(19),row.Width,row.Height);
        row.Top=y; row.Bottom=y+row.Height+grid.Px(29); row.Entry=entry;
    }
    string NudgeMessage(Nudge nudge, TaskItem t) {
        return profile.Voice(nudge.Id.StartsWith("test-") ? "test" : nudge.Kind,nudge.Title,
            t != null && t.DeadlineUtc.HasValue ? Time(t.DeadlineUtc.Value) : "",t != null && t.Submission);
    }

    void ShowUpcoming() {
        var current = SelectedConversation;
        if (current != null && !replying)
            AddChatPair(current,"Show my upcoming reminders",Language.ListReminders(store.Items,DateTime.Now));
    }
    void AddChatPair(ConversationChoice current,string words,string answer) {
        try {
            store.Chat(current.MessageId,current.TaskId,words,answer);
            SyncAfterChat(current.MessageId);
        } catch(Exception e) { SetExpression(EyeExpression.Sad); messageStatus.Text = "I couldn’t save that message. " + e.Message; }
    }
    void AnswerQuick(string action,string words) {
        var selected = SelectedConversation;
        if (!replying && selected != null && selected.Pending)
            CompleteReply(selected,words,new Intent { Action=action,Id=selected.TaskId });
    }
    void CompleteReply(ConversationChoice selected,string words,Intent command) {
        try {
            if (!store.Inbox.Any(n=>n.MessageId==selected.MessageId)) { SyncInbox(); return; }
            if (command.Action=="snooze") command.CheckUtc=DateTime.UtcNow.AddMinutes(10);
            CommitCommand(selected,words,command,selected.TaskId,"");
        } catch(Exception e) { SetExpression(EyeExpression.Sad); messageStatus.Text="Reply could not be saved. "+e.Message; }
    }
    async Task SendThreadReply() {
        var selected = SelectedConversation;
        string words = replyBox.Text.Trim();
        if (replying || busy || selected==null || words.Length==0) return;
        replying=true;
        try {
            var sending=SendMessage(words,selected);
            if (!sending.IsCompleted) RenderNotice();
            bool saved=await sending;
            if (saved && !notice.IsDisposed) {
                drafts.Remove(selected.MessageId);
                if (composingFor==selected.MessageId) replyBox.Clear();
            }
        } finally {
            replying=false;
            if (!notice.IsDisposed) {
                RenderNotice();
                if (notice.Visible && notice.Enabled && replyBox.Enabled && Form.ActiveForm==notice) replyBox.Focus();
            }
        }
    }
    void Poll() {
        if (lastHistoryDate!=DateTime.Today) SyncInbox();
        if (responseExpression != EyeExpression.Neutral && DateTime.UtcNow >= expressionUntil) {
            responseExpression = EyeExpression.Neutral;
            RefreshPet();
        }
        if (DateTime.UtcNow < nextPoll) return;
        try {
            var due = store.TakeDue(DateTime.UtcNow,n=>NudgeMessage(n,Get(n.Id)));
            if (due.Count == 0) return;
            var selected = SelectedConversation;
            bool showIncoming = notice.Visible && !browsingDate.HasValue && historyPage==0 && !replying && replyBox.TextLength == 0 &&
                (selected == null || !selected.Pending);
            RefreshTasks(focusId); SyncInbox(showIncoming ? due[0].MessageId : null);
            if (banners.Checked) {
                var newest = due[0]; notifiedMessageId = newest.MessageId;
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.BalloonTipTitle = profile.Name;
                string text = newest.Text + (due.Count > 1 ? " You have " + due.Count + " new messages." : "");
                tray.BalloonTipText = text.Length > 245 ? text.Substring(0,244) + "…" : text;
                tray.ShowBalloonTip(10000);
            }
        } catch(Exception e) {
            nextPoll = DateTime.UtcNow.AddMinutes(1);
            Say("I couldn’t check reminders. I’ll retry in a minute. " + e.Message);
        }
    }
}
