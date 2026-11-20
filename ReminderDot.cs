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
    protected override CreateParams CreateParams {
        get {
            var p = base.CreateParams;
            p.ClassStyle |= 0x00020000; // CS_DROPSHADOW gives the compact panel separation on busy desktops.
            return p;
        }
    }
}

class ReminderDot : Form {
    static readonly Color Ink = Color.FromArgb(31, 45, 42);
    static readonly Color Teal = Color.FromArgb(20, 91, 79);
    static readonly Color Paper = Color.FromArgb(249, 248, 242);
    static readonly Color Soft = Color.FromArgb(230, 237, 229);

    readonly TaskStore store;
    readonly string dataDirectory;
    PetProfile profile;
    readonly Form editor = new Form();
    readonly Form notice = new ConversationPopover();
    readonly Panel avatar = new Panel();
    readonly Panel composer = new Panel();
    readonly RichTextBox conversation = new RichTextBox();
    readonly TextBox input = new TextBox();
    readonly ListBox tasks = new ListBox();
    readonly Label model = new Label();
    Label taskHeading, petName, noticeName;
    readonly Panel messageThread = new Panel();
    readonly TextBox replyBox = new TextBox();
    readonly Label messageStatus = new Label();
    readonly ComboBox eventList = new ComboBox();
    readonly List<Nudge> outstanding = new List<Nudge>();
    readonly Timer timer = new Timer();
    readonly Timer blink = new Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    Icon trayIcon, waitingIcon;
    ToolStripMenuItem banners;
    Button send, finish, sendReply, messagesButton, acknowledge, snoozeReply, closeChat, upcoming, newReminder;
    readonly ToolTip hints = new ToolTip();
    string context = "", focusId;
    bool refreshing, busy, quitting, urgent, petHidden, eyesClosed, hovering, rebuilding, replying;
    string notifiedMessageId;
    const string GlobalChat = "__general";
    readonly Dictionary<string,string> questions = new Dictionary<string,string>();
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
        store = new TaskStore(Path.Combine(dir, "tasks.json"));
        Text = "Blink dot"; AccessibleName = Text; AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(profile.PetPixels,profile.PetPixels); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 22, area.Bottom - Height - 22);
        BuildDotMenu(); BuildEditor(); BuildNotice();
        ApplyProfile(); SyncInbox();
        if (profileUnavailable) Say("I couldn’t load your pet settings, so I’m using the default appearance. You can save new settings in Customize pet.");
        RefreshTasks(null);
        MouseEnter += delegate { hovering = true; RefreshPet(); };
        MouseLeave += delegate { hovering = false; RefreshPet(); };
        blink.Interval = profile.BlinkIntervalMs;
        blink.Tick += delegate {
            eyesClosed = !eyesClosed;
            blink.Interval = eyesClosed ? 150 : profile.BlinkIntervalMs;
            if (Visible) RefreshPet();
        };
        blink.Start();
        Point down = Point.Empty, origin = Point.Empty; bool moved = false;
        MouseDown += (s,e) => { if (e.Button == MouseButtons.Left) { down = Cursor.Position; origin = Location; moved = false; Capture = true; } };
        MouseMove += (s,e) => {
            if (e.Button != MouseButtons.Left || !Capture) return;
            int dx = Cursor.Position.X - down.X, dy = Cursor.Position.Y - down.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4) moved = true;
            if (moved) {
                var bounds = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = Clamp(new Point(origin.X + dx,origin.Y + dy), bounds, Size);
                Place(editor); Place(notice);
            }
        };
        MouseUp += (s,e) => {
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
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
            quitting = true; timer.Dispose(); blink.Dispose(); tray.Visible = false; tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose(); if (waitingIcon != null) waitingIcon.Dispose();
            notice.Dispose(); editor.Dispose(); hints.Dispose();
        };
    }

    protected override CreateParams CreateParams {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000; return p; } // WS_EX_LAYERED
    }
    protected override void OnPaint(PaintEventArgs e) {
        if (profile != null) PetArtwork.Draw(e.Graphics,ClientRectangle,profile.GetColor(),urgent,hovering,eyesClosed,busy,profile.EyeHeight);
    }
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    struct AlphaBlend { public byte Operation, Flags, Opacity, Format; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr screen,IntPtr destination,ref Size dimensions,
        IntPtr source,ref Point origin,uint colorKey,ref AlphaBlend blend,uint flags);

    void RefreshPet() {
        avatar.Invalidate();
        if (!IsHandleCreated || !Visible || profile == null || IsDisposed) return;
        using (var frame = PetArtwork.Render(ClientSize.Width,profile.GetColor(),urgent,hovering,eyesClosed,busy,profile.EyeHeight)) {
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen);
            IntPtr bitmap = frame.GetHbitmap(Color.FromArgb(0)), original = SelectObject(memory,bitmap);
            try {
                var dimensions = ClientSize; var origin = Point.Empty;
                var blend = new AlphaBlend { Opacity=255, Format=1 };
                if (!UpdateLayeredWindow(Handle,screen,IntPtr.Zero,ref dimensions,memory,ref origin,0,ref blend,2))
                    Invalidate();
            } finally {
                SelectObject(memory,original); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero,screen);
            }
        }
    }

    void BuildDotMenu() {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Blink",null,(s,e) => OpenInbox());
        menu.Items.Add("Show pending reminders",null,(s,e) => OpenInbox());
        banners = new ToolStripMenuItem("Windows notification banners");
        string bannerFile = Path.Combine(dataDirectory,"notification-banners.enabled");
        banners.Checked = File.Exists(bannerFile);
        banners.Click += delegate {
            try {
                if (banners.Checked) File.Delete(bannerFile);
                else { Directory.CreateDirectory(dataDirectory); File.WriteAllText(bannerFile,"enabled"); }
                banners.Checked = !banners.Checked;
            } catch (Exception e) { ShowEditor(); Say("I couldn’t save the notification setting. " + e.Message); }
        };
        menu.Items.Add(banners);
        menu.Items.Add("Customize pet",null,(s,e) => { ShowEditor(); Customize(); });
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
        editor.Hide(); SyncInbox(messageId ?? (outstanding.Count > 0 ? outstanding[0].MessageId : null));
        Place(notice); if (!notice.Visible) notice.Show(this);
        notice.Activate(); if (replyBox.Enabled) replyBox.Focus();
    }
    void ApplyProfile() {
        var anchor = new Point(Right,Bottom);
        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(profile.PetPixels,profile.PetPixels);
        Location = Clamp(new Point(anchor.X - Width,anchor.Y - Height),area,Size);
        var oldIcon = trayIcon; var oldWaiting = waitingIcon;
        trayIcon = PetArtwork.TrayIcon(profile.GetColor(),false);
        waitingIcon = PetArtwork.TrayIcon(profile.GetColor(),true);
        tray.Icon = urgent ? waitingIcon : trayIcon;
        if (oldIcon != null) oldIcon.Dispose(); if (oldWaiting != null) oldWaiting.Dispose();
        petName.Text = profile.Name; noticeName.Text = profile.Name;
        hints.SetToolTip(petName,profile.Name); hints.SetToolTip(noticeName,profile.Name);
        hints.SetToolTip(this,profile.Name + " · click to talk, drag to move");
        UpdateBadge();
        blink.Interval = eyesClosed ? 150 : profile.BlinkIntervalMs;
        if (editor.Visible) Place(editor);
        if (notice.Visible) Place(notice);
        RefreshPet();
        if (notice.Visible) RenderNotice();
    }

    static Point Clamp(Point p, Rectangle bounds, Size size) {
        return new Point(Math.Max(bounds.Left, Math.Min(p.X,bounds.Right-size.Width)),
                         Math.Max(bounds.Top, Math.Min(p.Y,bounds.Bottom-size.Height)));
    }
    void Place(Form f) {
        var area = Screen.FromControl(this).WorkingArea;
        const int gap = 8;
        int alignX = Left + Width/2 < area.Left + area.Width/2 ? Left : Right - f.Width;
        var desired = new Point(alignX,Top - gap - f.Height);
        if (desired.Y < area.Top) {
            if (Bottom + gap + f.Height <= area.Bottom) desired.Y = Bottom + gap;
            else if (Right + gap + f.Width <= area.Right) desired = new Point(Right + gap,Top - f.Height/2 + Height/2);
            else if (Left - gap - f.Width >= area.Left) desired = new Point(Left - gap - f.Width,Top - f.Height/2 + Height/2);
        }
        f.Location = Clamp(desired,area,f.Size);
    }
    static void Surface(Form f, string title, int width, int height) {
        f.Text = title; f.AccessibleName = title; f.AutoScaleMode = AutoScaleMode.None;
        f.FormBorderStyle = FormBorderStyle.None; f.StartPosition = FormStartPosition.Manual;
        f.ShowInTaskbar = false; f.TopMost = true; f.BackColor = Paper; f.ForeColor = Ink;
        f.Font = new Font("Segoe UI",9F); f.ClientSize = new Size(width,height);
        ShapeToSize(f);
        f.SizeChanged += delegate { ShapeToSize(f); };
        f.Paint += (s,e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var edge = BubbleShape(f.ClientSize.Width,f.ClientSize.Height))
            using (var pen = new Pen(Color.FromArgb(154,174,164),2)) e.Graphics.DrawPath(pen,edge);
        };
    }
    static void ShapeToSize(Form f) {
        using (var edge = BubbleShape(f.ClientSize.Width,f.ClientSize.Height)) {
            var old = f.Region; f.Region = new Region(edge); if (old != null) old.Dispose();
        }
    }
    static GraphicsPath BubbleShape(int width,int height) {
        var edge = new GraphicsPath();
        edge.AddArc(0,0,20,20,180,90);
        edge.AddArc(width-21,0,20,20,270,90);
        edge.AddArc(width-21,height-21,20,20,0,90);
        edge.AddArc(0,height-21,20,20,90,90);
        edge.CloseFigure(); return edge;
    }
    static Label Caption(Control owner, string words, int x, int y, int w, int h, float size, bool bold) {
        var label = new Label { Text = words, ForeColor = Ink, Font = new Font("Segoe UI",size,bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft };
        label.SetBounds(x,y,w,h); owner.Controls.Add(label); return label;
    }
    static Button ButtonAt(Control owner, string text,int x,int y,int w,int h, Action action,bool primary) {
        var b = new Button { Text = text, AccessibleName = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Teal : Soft, ForeColor = primary ? Color.White : Ink, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; b.SetBounds(x,y,w,h); owner.Controls.Add(b);
        b.Click += delegate { action(); }; return b;
    }
    void ShowEditor() {
        if (petHidden) TogglePet();
        notice.Hide(); Place(editor);
        if (!editor.Visible) editor.Show(this);
        editor.Activate(); input.Focus();
    }

    void BuildEditor() {
        Surface(editor,"Blink",410,433);
        petName = Caption(editor,"Blink",16,7,142,36,17,true); petName.AutoEllipsis = true;
        model.SetBounds(164,12,166,27); model.Text = "Checking local AI…"; model.TextAlign = ContentAlignment.MiddleLeft;
        model.ForeColor = Teal; model.AutoEllipsis = true; editor.Controls.Add(model);
        model.Cursor = Cursors.Hand; model.Click += async delegate { await CheckModel(); Say(model.Text); };
        ButtonAt(editor,"Hide",340,12,54,26,() => editor.Hide(),false);
        var chatArea = new Panel { BackColor = Color.White, Padding = new Padding(10,8,10,8) };
        chatArea.SetBounds(16,54,378,121); editor.Controls.Add(chatArea);
        conversation.Dock = DockStyle.Fill; conversation.BackColor = Color.White;
        conversation.ForeColor = Ink; conversation.ReadOnly = true; conversation.AccessibleName = "Conversation";
        conversation.BorderStyle = BorderStyle.None; conversation.DetectUrls = false;
        conversation.Font = new Font("Segoe UI",10); chatArea.Controls.Add(conversation);
        Say(profile.Greeting());
        input.SetBounds(16,185,306,30); input.Font = new Font("Segoe UI",11); input.MaxLength = 1200;
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
        tasks.SetBounds(16,293,378,83); tasks.BackColor = Color.White; tasks.Font = new Font("Segoe UI",10);
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
            focusId = t == null ? null : t.Id; context = "";
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
            string state = await Language.ModelStatus();
            if (editor.IsDisposed) return;
            model.Text = state; hints.SetToolTip(model,state + " · Click to check again.");
        } catch { if (!editor.IsDisposed) model.Text = "Local AI unavailable"; }
    }
    void Line(string who,string message) {
        if (conversation.TextLength > 18000) conversation.Clear();
        if (conversation.TextLength > 0) conversation.AppendText("\n\n");
        conversation.SelectionColor = Teal; conversation.AppendText(who + "   ");
        conversation.SelectionColor = Ink; conversation.AppendText(message);
        conversation.SelectionStart = conversation.TextLength; conversation.ScrollToCaret();
    }
    void Say(string text) { Line(profile.Name,text); }
    TaskItem Get(string id) { return store.Items.FirstOrDefault(t => t.Id == id && t.Stage != "done"); }
    string ActiveId() { return focusId; }
    void RemoveSelected() {
        var t = Get(focusId);
        if (t == null || MessageBox.Show(editor,"Remove “" + t.Title + "” and its reminders?","Remove task",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { store.Delete(t.Id); RefreshTasks(null); SyncInbox(); context = ""; Say("Removed " + t.Title + "."); }
        catch(Exception e) { Say("I couldn’t remove that task. " + e.Message); }
    }
    void RefreshTasks(string id) {
        refreshing = true;
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
            editor.ClientSize = new Size(410,count == 0 ? 303 : 433);
            if (editor.Visible) Place(editor);
        } finally { refreshing = false; }
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
        try { store.Save(t); RefreshTasks(t.Id); return true; }
        catch(Exception e) { Say("I couldn’t save that change. " + e.Message); ShowEditor(); return false; }
    }
    static TaskItem NewItem(string title) {
        return new TaskItem { Id = Guid.NewGuid().ToString("N"), Title = title, File = "", Stage = "working" };
    }
    static void SetDeadline(TaskItem t,DateTime deadline) {
        t.DeadlineUtc = deadline;
        t.WarnUtc = deadline.AddMinutes(-30) > DateTime.UtcNow ? (DateTime?)deadline.AddMinutes(-30) : null;
    }
    void TestReminder() {
        context = "";
        var t = NewItem("Your 10-second test"); t.Id = "test-" + t.Id; t.CheckUtc = DateTime.UtcNow.AddSeconds(10);
        if (Persist(t)) Say("Test set. Hide this bubble and I’ll message you in 10 seconds.");
    }
    void Customize() {
        using (var dialog = new Form()) {
            dialog.Text = "Customize pet"; dialog.AccessibleName = dialog.Text;
            dialog.Font = editor.Font; dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.BackColor = Paper;
            dialog.ShowInTaskbar = false; dialog.MaximizeBox = false; dialog.MinimizeBox = false;
            dialog.TopMost = true; dialog.ClientSize = new Size(357,389);
            Caption(dialog,"Pet name",16,9,320,20,10,true);
            var name = new TextBox { AccessibleName = "Pet name", Text = profile.Name, MaxLength = 32 };
            name.SetBounds(16,32,325,26); dialog.Controls.Add(name);
            Caption(dialog,"Characteristic",16,66,320,20,10,true);
            var trait = new ComboBox { AccessibleName = "Characteristic", MaxLength = 160 };
            trait.Items.AddRange(new string[] { "Curious","Sleepy","Energetic" });
            trait.Text = profile.Character; trait.SetBounds(16,90,325,27); dialog.Controls.Add(trait);
            Caption(dialog,"Personality",16,127,320,20,10,true);
            var tone = Choose(dialog,"Personality",150,PetProfile.Personalities,profile.Personality);
            Caption(dialog,"Appearance",16,187,320,20,10,true);
            var color = Choose(dialog,"Appearance",210,PetProfile.Appearances,profile.Appearance);
            Caption(dialog,"Pet size",16,246,320,20,10,true);
            var petSize = Choose(dialog,"Pet size",269,PetProfile.Sizes,profile.SizeChoice);
            var error = Caption(dialog,"",16,299,325,33,9,false);
            var cancel = ButtonAt(dialog,"Cancel",145,341,90,32,() => dialog.Close(),false);
            dialog.CancelButton = cancel;
            dialog.AcceptButton = ButtonAt(dialog,"Save",244,341,97,32,() => {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "Give your pet a name."; name.Focus(); return; }
                var updated = profile.Copy();
                updated.Name = name.Text.Trim(); updated.Character = trait.Text.Trim();
                updated.Personality = (string)tone.SelectedItem; updated.Appearance = (string)color.SelectedItem;
                updated.SizeChoice = (string)petSize.SelectedItem;
                try {
                    updated.Save(dataDirectory); profile = updated; ApplyProfile();
                    Say(profile.Greeting()); dialog.Close();
                } catch(Exception e) { error.Text = e.Message; }
            },true);
            dialog.ShowDialog(editor);
        }
    }
    static ComboBox Choose(Form owner,string label,int y,string[] values,string selected) {
        var box = new ComboBox { AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(values); box.SelectedItem = selected; box.SetBounds(16,y,325,27);
        owner.Controls.Add(box); return box;
    }
    void Manual() {
        using(var dialog = new Form()) {
            dialog.Text = "Set a reminder"; dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Font = editor.Font; dialog.BackColor = Paper; dialog.ForeColor = Ink;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.TopMost = true;
            dialog.MinimizeBox = false; dialog.MaximizeBox = false; dialog.ClientSize = new Size(365,265);
            Caption(dialog,"Task",14,9,318,22,10,true);
            var name = new TextBox { Text = input.Text.Trim(), AccessibleName = "Task", MaxLength = 500 };
            name.SetBounds(14,37,337,25); dialog.Controls.Add(name);
            var check = DateField(dialog,"Check-in",77,DateTime.Now.AddMinutes(10),true);
            var due = DateField(dialog,"Deadline",114,DateTime.Now.AddDays(1),false);
            var upload = new CheckBox { Text = "This requires a submission" }; upload.SetBounds(14,154,330,26); dialog.Controls.Add(upload);
            var error = Caption(dialog,"",14,181,335,34,9,false);
            ButtonAt(dialog,"Save reminder",194,220,156,31,() => {
                var t = NewItem(name.Text.Trim());
                if (t.Title.Length == 0 || (!check.Checked && !due.Checked)) { error.Text = "Give the task a name and select at least one time."; return; }
                if ((check.Checked && check.Value <= DateTime.Now) || (due.Checked && due.Value <= DateTime.Now)) { error.Text = "Choose a future time for each selected event."; return; }
                t.Submission = upload.Checked;
                if (check.Checked) t.CheckUtc = check.Value.ToUniversalTime();
                if (due.Checked) SetDeadline(t,due.Value.ToUniversalTime());
                if (Persist(t)) { context = ""; input.Clear(); Say("Saved. " + Describe(t)); dialog.Close(); }
            },true);
            dialog.ShowDialog(editor);
        }
    }
    static DateTimePicker DateField(Form parent,string name,int y,DateTime time,bool enabled) {
        Caption(parent,name,14,y,78,26,9,false);
        var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd dd MMM yyyy   HH:mm:ss", Value = time, ShowCheckBox = true, Checked = enabled, AccessibleName = name };
        picker.SetBounds(96,y,255,28); parent.Controls.Add(picker); return picker;
    }
    async Task SendMessage(string typed = null,ConversationChoice chat = null) {
        string words = typed ?? input.Text.Trim(); if (busy || words.Length == 0) return;
        string selection = chat == null || chat.TaskId == GlobalChat ? focusId : chat.TaskId;
        string thread = context;
        if (chat != null) { questions.TryGetValue(chat.MessageId,out thread); thread = thread ?? ""; }
        Action<string> respond = answer => {
            Say(answer);
            store.Chat(chat == null ? GlobalChat : chat.MessageId,chat == null ? GlobalChat : chat.TaskId,words,answer);
        };
        busy = true; send.Enabled = false; RefreshPet();
        if (chat == null) input.Clear();
        Line("You",words);
        try {
            var command = await Language.Read(words,DateTime.Now,store.Items.Where(t => t.Stage != "done").ToList(),Get(selection),thread,profile);
            if (editor.IsDisposed) return;
            if (command == null) { respond("I couldn’t read that request. You can also choose Set manually."); return; }
            if (command.Action == "ask") {
                string next = (thread.Length > 0 ? thread + "\n" : "") + "User: " + words + "\nBlink: " + (command.ContextReply ?? command.Reply);
                if (chat == null) context = next; else questions[chat.MessageId] = next;
                respond(command.Reply); return;
            }
            if (chat == null) context = ""; else questions.Remove(chat.MessageId);
            if (command.Action == "list") { respond(command.Reply); return; }
            if (command.Action == "triage") {
                var t = Get(command.Id) ?? store.Items.Where(x=>x.Stage!="done").OrderBy(x=>x.DeadlineUtc ?? x.CheckUtc ?? DateTime.MaxValue).FirstOrDefault();
                if (t == null) respond("You have no active tasks.");
                else { RefreshTasks(t.Id); respond("Start with " + t.Title + ". " + (t.DeadlineUtc.HasValue ? "It’s due " + Time(t.DeadlineUtc.Value) + "." : t.CheckUtc.HasValue ? "Your check-in is " + Time(t.CheckUtc.Value) + "." : "It’s an open task.")); }
                return;
            }
            bool creating = command.Action == "create";
            var task = creating ? NewItem(command.Title) : Get(command.Id ?? selection);
            if (task == null) { respond("Which active task do you mean? Select it below and try again."); return; }
            if (command.Action == "working") { RefreshTasks(task.Id); SyncInbox(); respond("You’re still working on " + task.Title + "."); return; }
            if (command.Action == "draft" || command.Action == "done" || command.Action == "submitted") {
                Quick(command.Action,task.Id,respond); return;
            }
            if (!creating && command.Action != "edit" && command.Action != "snooze") { respond(command.Reply ?? "What would you like to change?"); return; }
            if (String.IsNullOrWhiteSpace(task.Title)) { respond("What should I call this task?"); return; }
            if ((command.CheckUtc.HasValue && command.CheckUtc <= DateTime.UtcNow) || (command.DeadlineUtc.HasValue && command.DeadlineUtc <= DateTime.UtcNow)) {
                respond("That time has passed. What future time should I use?"); return;
            }
            if (command.Action == "snooze" && !command.CheckUtc.HasValue) { respond("When should I check in again?"); return; }
            if (command.DeadlineUtc.HasValue && command.Action != "snooze") SetDeadline(task,command.DeadlineUtc.Value);
            if (command.CheckUtc.HasValue) task.CheckUtc = command.CheckUtc;
            if (command.Action != "snooze") {
                if (!String.IsNullOrWhiteSpace(command.Title)) task.Title = command.Title;
                if (!String.IsNullOrWhiteSpace(command.File)) task.File = command.File;
                task.Submission |= command.Submission;
            }
            if (Persist(task)) {
                SyncInbox();
                respond("Saved. " + Describe(task));
            }
        } catch(Exception) { if (!editor.IsDisposed) messageStatus.Text = "I couldn’t process or save that message."; if (chat==null) Say("I couldn’t process that request. Try a simpler time or choose Set manually."); }
        finally { if (!editor.IsDisposed) { busy = false; send.Enabled = true; RefreshPet(); } }
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
        if (Persist(t)) { context = ""; SyncInbox(); respond(reply); }
    }
    sealed class ConversationChoice {
        public string MessageId, TaskId, Label;
        public bool Pending;
        public override string ToString() { return Label; }
    }
    ConversationChoice SelectedConversation { get { return eventList.SelectedItem as ConversationChoice; } }

    void BuildNotice() {
        Surface(notice,"Blink messages",388,418);
        notice.BackColor = Color.FromArgb(251,252,248);
        avatar.SetBounds(14,12,38,38); avatar.BackColor = notice.BackColor; notice.Controls.Add(avatar);
        avatar.Paint += (s,e) => PetArtwork.Draw(e.Graphics,avatar.ClientRectangle,profile.GetColor(),urgent,false,eyesClosed,false,profile.EyeHeight);
        noticeName = Caption(notice,profile.Name,61,8,255,28,14,true);
        noticeName.AutoEllipsis = true;
        closeChat = ButtonAt(notice,"×",344,13,30,30,() => notice.Hide(),false);
        closeChat.AccessibleName = "Close conversation"; closeChat.Font = new Font("Segoe UI",15);
        closeChat.BackColor = Color.FromArgb(238,242,235); RoundControl(closeChat);
        hints.SetToolTip(closeChat,"Close conversation (Esc)");
        messageStatus.SetBounds(62,35,267,19); messageStatus.ForeColor = Teal;
        messageStatus.Font = new Font("Segoe UI",8.5F);
        messageStatus.AutoEllipsis = true; notice.Controls.Add(messageStatus);
        var rule = new Panel { BackColor = Color.FromArgb(224,231,221) };
        rule.SetBounds(1,63,386,1); notice.Controls.Add(rule);
        upcoming = ButtonAt(notice,"Upcoming",181,74,81,26,ShowUpcoming,false);
        newReminder = ButtonAt(notice,"New reminder",269,74,104,26,ShowEditor,false);
        RoundControl(upcoming); RoundControl(newReminder);
        eventList.DropDownStyle = ComboBoxStyle.DropDownList;
        eventList.FlatStyle = FlatStyle.Flat; eventList.BackColor = notice.BackColor;
        eventList.AccessibleName = "Messages"; eventList.SetBounds(14,75,158,26);
        notice.Controls.Add(eventList);
        eventList.SelectedIndexChanged += delegate {
            if (rebuilding) return;
            replyBox.Clear(); RenderNotice();
        };
        messageThread.SetBounds(10,110,368,220); messageThread.AutoScroll = true;
        messageThread.AccessibleName = "Message thread";
        messageThread.BackColor = notice.BackColor; notice.Controls.Add(messageThread);

        acknowledge = ButtonAt(notice,"Still working",16,336,106,29,() => AnswerQuick("working","Still working"),false);
        snoozeReply = ButtonAt(notice,"Snooze 10 min",129,336,118,29,() => AnswerQuick("snooze","Snooze 10 min"),false);
        finish = ButtonAt(notice,"Done",254,336,140,29,() => {
            var current = SelectedConversation;
            var t = current == null ? null : Get(current.TaskId);
            if (t != null) AnswerQuick(t.Submission ? "submitted" : "done",t.Submission ? "I submitted it" : "Done");
        },false);
        RoundControl(acknowledge); RoundControl(snoozeReply); RoundControl(finish);
        composer.SetBounds(12,362,364,44);
        composer.BackColor = Color.FromArgb(238,242,234); RoundControl(composer); notice.Controls.Add(composer);
        replyBox.Font = new Font("Segoe UI",10.5F); replyBox.BorderStyle = BorderStyle.None;
        replyBox.BackColor = composer.BackColor; replyBox.ForeColor = Ink;
        replyBox.SetBounds(12,12,285,24);
        replyBox.MaxLength = 1200; replyBox.AccessibleName = "Reply"; composer.Controls.Add(replyBox);
        replyBox.HandleCreated += delegate { SendMessageNative(replyBox.Handle,0x1501,new IntPtr(1),"Write a reply…"); };
        sendReply = ButtonAt(composer,"↑",325,6,32,32,async () => await SendThreadReply(),true);
        sendReply.AccessibleName = "Send reply"; sendReply.Font = new Font("Segoe UI",13,FontStyle.Bold);
        RoundControl(sendReply); hints.SetToolTip(sendReply,"Send reply (Enter)");
        replyBox.KeyDown += async (s,e) => {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendThreadReply(); }
        };
        notice.KeyPreview = true;
        notice.KeyDown += (s,e) => { if (e.KeyCode == Keys.Escape) notice.Hide(); };
        notice.FormClosing += (s,e) => {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; notice.Hide(); }
        };
    }
    static void RoundControl(Control control) {
        using (var edge = BubbleShape(control.Width,control.Height)) {
            var old = control.Region; control.Region = new Region(edge); if (old != null) old.Dispose();
        }
    }
    void SizeConversation(int messages,bool actions) {
        var area = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(388,Math.Max(318,area.Width-12));
        int height = Math.Min(Math.Max(260,area.Height-12),messages == 0 ? 268 : messages == 1 ? 321 : 418);
        if (actions && messages <= 1) height = Math.Min(area.Height-12,height+37);
        bool resized = notice.ClientSize != new Size(width,height);
        if (resized) notice.ClientSize = new Size(width,height);
        closeChat.Left = width - 44;
        noticeName.Width = width - 120; messageStatus.Width = width - 110;
        newReminder.Left = width - 119; upcoming.Left = newReminder.Left - upcoming.Width - 7;
        eventList.Width = upcoming.Left - 22;
        composer.SetBounds(12,height-56,width-24,44); RoundControl(composer);
        replyBox.Width = composer.Width - 64; sendReply.Left = composer.Width - sendReply.Width - 6;
        int y = composer.Top - 37;
        acknowledge.SetBounds(13,y,90,26); snoozeReply.SetBounds(109,y,104,26);
        finish.SetBounds(219,y,Math.Min(width-232,125),26);
        messageThread.SetBounds(10,110,width-20,(actions ? y : composer.Top) - 117);
        if (resized && notice.Visible) Place(notice);
    }
    void UpdateBadge() {
        urgent = outstanding.Count > 0;
        string caption = profile.Name + (urgent ? " · " + outstanding.Count + " awaiting reply" : " · desktop reminders");
        tray.Text = caption.Substring(0,Math.Min(63,caption.Length));
        AccessibleDescription = urgent ? "Waiting for your reply" : "Desktop reminder pet";
        hints.SetToolTip(this,caption);
        tray.Icon = urgent ? waitingIcon : trayIcon ?? SystemIcons.Information;
        if (messagesButton != null) messagesButton.Text = urgent ? "Messages (" + outstanding.Count + ")" : "Messages";
        var oldShape = Region; Region = PetArtwork.Shape(ClientRectangle,urgent);
        if (oldShape != null) oldShape.Dispose();
        RefreshPet();
    }


    void SyncInbox(string preferred = null) {
        var previous = SelectedConversation;
        string oldId = previous == null ? null : previous.MessageId;
        outstanding.Clear(); outstanding.AddRange(store.Inbox);
        var choices = outstanding.Select(n=>new ConversationChoice {
            MessageId=n.MessageId, TaskId=n.Id, Pending=true,
            Label=n.Title + "   ·   " + n.SentUtc.ToLocalTime().ToString("g") + "   ·   Awaiting reply"
        }).ToList();
        var titleById = store.Items.ToDictionary(x=>x.Id,x=>x.Title);
        choices.Add(new ConversationChoice { MessageId=GlobalChat, TaskId=GlobalChat, Label="All messages" });
        var inboxIds = new HashSet<string>(outstanding.Select(n=>n.MessageId));
        // Keep the last answered conversation for each task available even when its final reply cleared the badge.
        foreach (var group in store.History.AsEnumerable().Reverse().Where(h=>h.TaskId!=GlobalChat && !inboxIds.Contains(h.MessageId)).GroupBy(h=>h.TaskId)) {
            var last = group.First();
            string title; titleById.TryGetValue(last.TaskId,out title);
            choices.Add(new ConversationChoice { MessageId=last.MessageId, TaskId=last.TaskId,
                Label=(title ?? "Earlier reminder") + "   ·   " + last.AtUtc.ToLocalTime().ToString("g") + "   ·   Replied" });
        }
        rebuilding = true;
        try {
            eventList.Items.Clear(); eventList.Items.AddRange(choices.ToArray());
            var target = choices.FirstOrDefault(c=>c.MessageId==(preferred ?? oldId)) ?? choices.FirstOrDefault();
            if (target != null) eventList.SelectedItem = target;
            if (target == null || target.MessageId != oldId) replyBox.Clear();
        } finally { rebuilding = false; }
        UpdateBadge(); RenderNotice();
    }

    void RenderNotice() {
        if (rebuilding || finish == null) return;
        var current = SelectedConversation;
        bool canType = current != null && !replying && !busy;
        bool canReply = canType && current.Pending;
        bool actionsVisible = current != null && current.Pending;
        finish.Visible = acknowledge.Visible = snoozeReply.Visible = actionsVisible;
        var entries = store.History.Where(h=>current != null && (current.TaskId==GlobalChat || h.TaskId==current.TaskId)).ToList();
        SizeConversation(entries.Count,actionsVisible);
        var task = current == null ? null : Get(current.TaskId);
        finish.Text = task != null && task.Submission ? "Mark submitted" : "Done"; finish.AccessibleName = finish.Text;
        finish.Enabled = canReply; acknowledge.Enabled = canReply; snoozeReply.Enabled = canReply;
        replyBox.Enabled = canType; sendReply.Enabled = canType; eventList.Enabled = !replying && current != null;
        messageStatus.Text = current == null ? "No reminder messages yet" :
            current.Pending ? (replying ? "Reading your reply…" : "Waiting for your reply") :
            current.TaskId == GlobalChat ? "Your desktop companion" : "Replied";
        messageThread.SuspendLayout();
        try {
            messageThread.AutoScrollPosition = Point.Empty;
            while (messageThread.Controls.Count > 0) messageThread.Controls[0].Dispose();
            if (current == null) {
                Caption(messageThread,"Your pet’s reminder messages will appear here.",14,14,335,55,10,false);
                return;
            }
            if (entries.Count == 0) {
                AddBubble(new ChatEntry { Text=profile.Greeting(),FromPet=true,AtUtc=DateTime.UtcNow },0);
                messageThread.AutoScrollMinSize = Size.Empty; return;
            }
            int y = 8;
            var displayed = entries.Skip(Math.Max(0,entries.Count-40)).ToList();
            // Keep the addressed reminder in view if this task has a long conversation.
            if (current.TaskId!=GlobalChat && displayed.Count > 0 && !displayed.Any(h=>h.MessageId==current.MessageId)) {
                var original = entries.FirstOrDefault(h=>h.MessageId==current.MessageId && h.FromPet);
                if (original != null) displayed.Insert(0,original);
            }
            foreach (var log in displayed) y = AddBubble(log,y);
            messageThread.AutoScrollMinSize = new Size(0,y);
        } finally { messageThread.ResumeLayout(true); }
        messageThread.AutoScrollPosition = new Point(0,messageThread.DisplayRectangle.Height);
    }
    int AddBubble(ChatEntry entry,int y) {
        bool pet = entry.FromPet;
        int available = messageThread.Width - 25;
        int max = Math.Min(264,available-42);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var measured = TextRenderer.MeasureText(entry.Text,notice.Font,new Size(max,Int32.MaxValue),flags);
        int width = Math.Max(96,Math.Min(max,measured.Width)+18);
        int height = Math.Max(28,measured.Height+12);
        int x = pet ? 8 : available-width;
        string stamp = (pet ? profile.Name : "You") + "   " + entry.AtUtc.ToLocalTime().ToString("MMM d, h:mm tt");
        var label = Caption(messageThread,stamp,pet ? x : 1,y,pet ? available : available-1,18,8,false);
        label.ForeColor = Color.FromArgb(74,95,87); label.TextAlign = pet ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
        var bubble = new Label { Text=entry.Text, AccessibleName=entry.Text, AutoSize=false, Padding=new Padding(9,5,9,5),
            UseMnemonic=false, BackColor=pet ? Color.FromArgb(231,238,227) : Teal, ForeColor=pet ? Ink : Color.White };
        bubble.SetBounds(x,y+19,width,height);
        using (var edge = BubbleShape(width,height)) bubble.Region = new Region(edge);
        messageThread.Controls.Add(bubble);
        return y + height + 29;
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
            replyBox.Clear(); SyncInbox(current.MessageId);
        } catch(Exception e) { messageStatus.Text = "I couldn’t save that message. " + e.Message; }
    }
    void AnswerQuick(string action,string words) {
        var selected = SelectedConversation;
        if (!replying && selected != null && selected.Pending)
            CompleteReply(selected,words,new Intent { Action=action,Id=selected.TaskId });
    }
    void CompleteReply(ConversationChoice selected,string words,Intent command) {
        try {
            var task = Get(selected.TaskId);
            if (task == null || !store.Inbox.Any(x=>x.MessageId==selected.MessageId)) { SyncInbox(); return; }
            if (command.Id != null && command.Id != task.Id) {
                Clarify(selected,words,"This message is about “" + task.Title + "”. What would you like to do with this task?"); return;
            }
            TaskItem changed = null;
            string answer = null;
            switch (command.Action) {
                case "snooze":
                    changed = task;
                    changed.CheckUtc = command.CheckUtc ?? DateTime.UtcNow.AddMinutes(10);
                    if (changed.CheckUtc <= DateTime.UtcNow) {
                        Clarify(selected,words,"When should I message you again?"); return;
                    }
                    answer = "I’ll message you again at " + Time(changed.CheckUtc.Value) + ".";
                    break;
                case "draft":
                    changed = task; changed.Stage = "draft ready"; changed.CheckUtc = null;
                    answer = task.Submission ? "Draft ready. I’ll keep the upload deadline on your list." : "Got it. Your draft is ready.";
                    break;
                case "done":
                case "submitted":
                    if (task.Submission && command.Action != "submitted") {
                        Clarify(selected,words,"Did you submit " + task.Title + "? Reply “I submitted it” once the upload is complete."); return;
                    }
                    changed = task; changed.Stage = "done"; changed.CheckUtc = null; changed.WarnUtc = null;
                    answer = task.Submission ? "Got it. I’ve marked it submitted." : "I’ve marked that task done.";
                    break;
                case "ack":
                case "working":
                    if (task.Id.StartsWith("test-")) { changed = task; changed.Stage = "done"; }
                    break;
                default:
                    Clarify(selected,words,command.Reply ?? "Should I snooze this reminder, mark the task done, or record a progress update?"); return;
            }
            if (store.Answer(selected.MessageId,words,changed,answer)) {
                replyBox.Clear(); RefreshTasks(task.Id);
                if (changed != null && command.Action == "snooze")
                    Say("I’ll check in on " + task.Title + " at " + Time(changed.CheckUtc.Value) + ".");
            }
            SyncInbox(selected.MessageId);
        } catch(Exception e) { messageStatus.Text = "Reply could not be saved. " + e.Message; }
    }
    void Clarify(ConversationChoice selected,string words,string question) {
        if (store.Clarify(selected.MessageId,words,question)) replyBox.Clear();
        SyncInbox(selected.MessageId);
    }
    async Task SendThreadReply() {
        var selected = SelectedConversation;
        string words = replyBox.Text.Trim();
        if (replying || busy || selected == null || words.Length == 0) return;
        var query = Language.ReminderListQuery(words,DateTime.Now,store.Items);
        if (query != null) { AddChatPair(selected,words,query.Reply); return; }
        if (!selected.Pending) {
            replying = true; RenderNotice();
            try { await SendMessage(words,selected); if (!notice.IsDisposed) { replyBox.Clear(); SyncInbox(selected.MessageId); } }
            finally { replying = false; if (!notice.IsDisposed) { RenderNotice(); replyBox.Focus(); } }
            return;
        }
        var current = Get(selected.TaskId);
        if (current == null) { SyncInbox(); return; }
        var logs = store.History.Where(h=>h.MessageId==selected.MessageId).ToList();
        var lastQuestion = logs.LastOrDefault(h=>h.FromPet);
        if (current.Submission && lastQuestion != null &&
            Regex.IsMatch(lastQuestion.Text,@"(?:have you submitted|did you submit|did (?:the|your) upload go through)",RegexOptions.IgnoreCase) &&
            Regex.IsMatch(words,@"^(?:yes|yep|yeah)[.!]?$",RegexOptions.IgnoreCase)) {
            CompleteReply(selected,words,new Intent { Action="submitted",Id=current.Id }); return;
        }
        var exchanges = logs.Skip(1).ToList();
        string prior = String.Join("\n",exchanges.Select(h=>(h.FromPet ? "Blink: " : "User: ") + h.Text));
        bool actionLike = Regex.IsMatch(words,@"\b(snooze|remind|check again|check in|later|finished|finish|submitted|submit|uploaded|upload|done|completed|draft|exported|working|reschedule|postpone|move|mark)\b",RegexOptions.IgnoreCase);
        bool needsClarification = exchanges.Count > 0 && lastQuestion != null && lastQuestion.Text.Contains("?");
        if (!actionLike && (!needsClarification || Regex.IsMatch(words,@"^(?:got it|thanks|thank you|okay|ok)[.!]?$",RegexOptions.IgnoreCase))) {
            CompleteReply(selected,words,new Intent { Action="ack",Id=current.Id }); return;
        }
        replying = true; RenderNotice();
        try {
            string request = words;
            if (exchanges.Count > 0 && Regex.IsMatch(words,@"^(?:in )?(?:an?|one|\d+) (?:min(?:ute)?s?|hours?|days?)\.?$",RegexOptions.IgnoreCase))
                request = "Snooze " + words;
            var command = await Language.Read(request,DateTime.Now,store.Items,current,prior,profile);
            if (notice.IsDisposed) return;
            if (command == null) { Clarify(selected,words,"What would you like me to do with this reminder?"); return; }
            CompleteReply(selected,words,command);
        } catch(Exception e) { if (!notice.IsDisposed) messageStatus.Text = "I couldn’t send that reply. " + e.Message; }
        finally { replying=false; if (!notice.IsDisposed) { RenderNotice(); if (notice.Visible && replyBox.Enabled) replyBox.Focus(); } }
    }
    void Poll() {
        if (DateTime.UtcNow < nextPoll) return;
        try {
            var due = store.TakeDue(DateTime.UtcNow,n=>NudgeMessage(n,Get(n.Id)));
            if (due.Count == 0) return;
            var selected = SelectedConversation;
            bool showIncoming = notice.Visible && !replying && replyBox.TextLength == 0 &&
                (selected == null || !selected.Pending);
            RefreshTasks(focusId); SyncInbox(showIncoming ? due[0].MessageId : null);
            if (banners.Checked) {
                var newest = due[0]; notifiedMessageId = newest.MessageId;
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
