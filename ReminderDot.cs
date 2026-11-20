using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

class BlinkBubble : Form {
    internal bool Passive;
    protected override bool ShowWithoutActivation { get { return Passive; } }
    protected override CreateParams CreateParams {
        get {
            var style = base.CreateParams;
            if (Passive) style.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return style;
        }
    }
}

class ReminderDot : Form {
    static readonly Color Ink = Color.FromArgb(31, 45, 42);
    static readonly Color Teal = Color.FromArgb(20, 91, 79);
    static readonly Color Paper = Color.FromArgb(249, 248, 242);
    static readonly Color Soft = Color.FromArgb(230, 237, 229);
    static readonly Color Amber = Color.FromArgb(158, 75, 24);
    readonly TaskStore store;
    readonly string dataDirectory;
    PetProfile profile;
    readonly BlinkBubble editor = new BlinkBubble();
    readonly BlinkBubble notice = new BlinkBubble { Passive = true };
    readonly RichTextBox conversation = new RichTextBox();
    readonly TextBox input = new TextBox();
    readonly ListBox tasks = new ListBox();
    readonly Label model = new Label();
    Label taskHeading, petName, noticeName;
    readonly TextBox noticeText = new TextBox();
    readonly ComboBox eventList = new ComboBox();
    readonly List<Nudge> outstanding = new List<Nudge>();
    readonly Timer timer = new Timer();
    readonly Timer blink = new Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    Icon trayIcon;
    Button send, finish;
    readonly ToolTip hints = new ToolTip();
    string context = "", focusId;
    bool refreshing, busy, quitting, urgent, petHidden, eyesClosed, hovering;
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
        Size = new Size(56,56); FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(56,56); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        using (var path = new GraphicsPath()) { path.AddEllipse(0,0,55,55); Region = new Region(path); }
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 22, area.Bottom - Height - 22);
        BuildDotMenu(); BuildEditor(); BuildNotice();
        ApplyProfile();
        if (profileUnavailable) Say("I couldn’t load your pet settings, so I’m using the default appearance. You can save new settings in Customize pet.");
        RefreshTasks(null);
        MouseEnter += delegate { hovering = true; Invalidate(); };
        MouseLeave += delegate { hovering = false; Invalidate(); };
        blink.Interval = profile.BlinkIntervalMs;
        blink.Tick += delegate {
            eyesClosed = !eyesClosed;
            blink.Interval = eyesClosed ? 150 : profile.BlinkIntervalMs;
            if (Visible) Invalidate();
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
            if (!moved) { if (editor.Visible && !notice.Visible) editor.Hide(); else ShowEditor(); }
        };
        Shown += async delegate {
            if (!Environment.GetCommandLineArgs().Contains("--background")) ShowEditor();
            await CheckModel();
        };
        timer.Interval = 750; timer.Tick += delegate { Poll(); }; timer.Start();
        FormClosed += delegate {
            quitting = true; timer.Dispose(); blink.Dispose(); tray.Visible = false; tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            notice.Dispose(); editor.Dispose(); hints.Dispose();
        };
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Ink);
        Color coat = profile.GetColor();
        using (var body = new SolidBrush(urgent ? Amber : hovering ? ControlPaint.Light(coat,0.20F) : coat)) g.FillEllipse(body,3,3,49,49);
        using (var face = new SolidBrush(urgent ? Color.FromArgb(255,221,155) : Color.FromArgb(198,244,202))) {
            int height = eyesClosed ? 3 : busy ? 7 : profile.EyeHeight;
            int y = 27 - height/2 - (hovering ? 2 : 0);
            g.FillEllipse(face,15,y,8,height); g.FillEllipse(face,32,y,8,height);
        }
    }

    void BuildDotMenu() {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Blink",null,(s,e) => ShowEditor());
        menu.Items.Add("Show pending reminders",null,(s,e) => OpenInbox());
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
        try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch(System.ComponentModel.Win32Exception) { }
        tray.Icon = trayIcon ?? SystemIcons.Information;
        tray.Text = "Blink · desktop reminders"; tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowEditor(); };
        tray.BalloonTipClicked += delegate { OpenInbox(); };
        tray.Visible = true;
    }
    void TogglePet() {
        if (petHidden) { petHidden = false; Show(); return; }
        petHidden = true; notice.Hide(); editor.Hide(); Hide();
    }
    void OpenInbox() {
        if (petHidden) TogglePet();
        if (outstanding.Count > 0) ShowNotice();
        else { ShowEditor(); Say("You have no new reminder messages."); }
    }
    void ApplyProfile() {
        petName.Text = profile.Name; noticeName.Text = profile.Name;
        hints.SetToolTip(petName,profile.Name); hints.SetToolTip(noticeName,profile.Name);
        hints.SetToolTip(this,profile.Name + " · click to talk, drag to move");
        tray.Text = profile.Name + " · desktop reminders";
        blink.Interval = eyesClosed ? 150 : profile.BlinkIntervalMs;
        Invalidate();
        if (outstanding.Count > 0) RenderNotice();
    }

    static Point Clamp(Point p, Rectangle bounds, Size size) {
        return new Point(Math.Max(bounds.Left, Math.Min(p.X,bounds.Right-size.Width)),
                         Math.Max(bounds.Top, Math.Min(p.Y,bounds.Bottom-size.Height)));
    }
    void Place(Form f) {
        var area = Screen.FromControl(this).WorkingArea;
        int y = Top - f.Height - 8;
        if (y < area.Top && Bottom + f.Height + 8 <= area.Bottom) y = Bottom + 8;
        var desired = Clamp(new Point(Right - f.Width,y),area,f.Size);
        if (f == notice && editor.Visible && editor.Bounds.IntersectsWith(new Rectangle(desired,f.Size))) {
            if (editor.Left - 8 - f.Width >= area.Left) desired.X = editor.Left - 8 - f.Width;
            else if (editor.Right + 8 + f.Width <= area.Right) desired.X = editor.Right + 8;
            else if (editor.Top - 8 - f.Height >= area.Top) desired.Y = editor.Top - 8 - f.Height;
        }
        f.Location = desired;
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
        if (notice.Visible && NoticeId() != null) {
            context = ""; RefreshTasks(NoticeId()); Say(noticeText.Text);
        }
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
        try { store.Delete(t.Id); RefreshTasks(null); ClearNotice(t.Id); context = ""; Say("Removed " + t.Title + "."); }
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
            dialog.TopMost = true; dialog.ClientSize = new Size(357,336);
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
            var error = Caption(dialog,"",16,244,325,35,9,false);
            var cancel = ButtonAt(dialog,"Cancel",145,288,90,32,() => dialog.Close(),false);
            dialog.CancelButton = cancel;
            dialog.AcceptButton = ButtonAt(dialog,"Save",244,288,97,32,() => {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "Give your pet a name."; name.Focus(); return; }
                var updated = profile.Copy();
                updated.Name = name.Text.Trim(); updated.Character = trait.Text.Trim();
                updated.Personality = (string)tone.SelectedItem; updated.Appearance = (string)color.SelectedItem;
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
    async Task SendMessage() {
        string words = input.Text.Trim(); if (busy || words.Length == 0) return;
        string selection = focusId, thread = context;
        busy = true; send.Enabled = false; Invalidate();
        input.Clear(); Line("You",words);
        try {
            var command = await Language.Read(words,DateTime.Now,store.Items.Where(t => t.Stage != "done").ToList(),Get(selection),thread,profile);
            if (editor.IsDisposed) return;
            if (command == null) { Say("I couldn’t read that request. You can also choose Set manually."); return; }
            if (command.Action == "ask") {
                context = (thread.Length > 0 ? thread + "\n" : "") + "User: " + words + "\nBlink: " + (command.ContextReply ?? command.Reply);
                Say(command.Reply); return;
            }
            context = "";
            if (command.Action == "triage") {
                var t = Get(command.Id) ?? store.Items.Where(x=>x.Stage!="done").OrderBy(x=>x.DeadlineUtc ?? x.CheckUtc ?? DateTime.MaxValue).FirstOrDefault();
                if (t == null) Say("You have no active tasks.");
                else { RefreshTasks(t.Id); Say("Start with " + t.Title + ". " + (t.DeadlineUtc.HasValue ? "It’s due " + Time(t.DeadlineUtc.Value) + "." : t.CheckUtc.HasValue ? "Your check-in is " + Time(t.CheckUtc.Value) + "." : "It’s an open task.")); }
                return;
            }
            bool creating = command.Action == "create";
            var task = creating ? NewItem(command.Title) : Get(command.Id ?? selection);
            if (task == null) { Say("Which active task do you mean? Select it below and try again."); return; }
            if (command.Action == "working") { RefreshTasks(task.Id); ClearNotice(task.Id); Say("You’re still working on " + task.Title + "."); return; }
            if (command.Action == "draft" || command.Action == "done" || command.Action == "submitted") {
                Quick(command.Action,task.Id); return;
            }
            if (!creating && command.Action != "edit" && command.Action != "snooze") { Say(command.Reply ?? "What would you like to change?"); return; }
            if (String.IsNullOrWhiteSpace(task.Title)) { Say("What should I call this task?"); return; }
            if ((command.CheckUtc.HasValue && command.CheckUtc <= DateTime.UtcNow) || (command.DeadlineUtc.HasValue && command.DeadlineUtc <= DateTime.UtcNow)) {
                Say("That time has passed. What future time should I use?"); return;
            }
            if (command.Action == "snooze" && !command.CheckUtc.HasValue) { Say("When should I check in again?"); return; }
            if (command.DeadlineUtc.HasValue && command.Action != "snooze") SetDeadline(task,command.DeadlineUtc.Value);
            if (command.CheckUtc.HasValue) task.CheckUtc = command.CheckUtc;
            if (command.Action != "snooze") {
                if (!String.IsNullOrWhiteSpace(command.Title)) task.Title = command.Title;
                if (!String.IsNullOrWhiteSpace(command.File)) task.File = command.File;
                task.Submission |= command.Submission;
            }
            if (Persist(task)) {
                ClearNotice(task.Id);
                Say("Saved. " + Describe(task));
            }
        } catch(Exception) { if (!editor.IsDisposed) Say("I couldn’t process that request. Try a simpler time or choose Set manually."); }
        finally { if (!editor.IsDisposed) { busy = false; send.Enabled = true; Invalidate(); } }
    }
    void Quick(string action,string id) {
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
        if (Persist(t)) { context = ""; ClearNotice(id); Say(reply); }
    }
    void BuildNotice() {
        Surface(notice,"Blink reminder",392,193);
        noticeName = Caption(notice,"Blink",15,7,124,33,16,true); noticeName.AutoEllipsis = true;
        eventList.DropDownStyle = ComboBoxStyle.DropDownList; eventList.AccessibleName = "Pending reminders";
        eventList.SetBounds(145,15,160,24); notice.Controls.Add(eventList);
        eventList.SelectedIndexChanged += (s,e) => RenderNotice();
        ButtonAt(notice,"Got it",317,11,60,28,() => {
            string id = NoticeId();
            if (id != null && id.StartsWith("test-")) Quick("done",id);
            else ClearNotice(id);
        },false);
        noticeText.SetBounds(16,52,360,70); noticeText.AccessibleName = "Reminder message";
        noticeText.Multiline = true; noticeText.ReadOnly = true; noticeText.BackColor = Paper;
        noticeText.ForeColor = Ink; noticeText.BorderStyle = BorderStyle.None;
        noticeText.ScrollBars = ScrollBars.Vertical; noticeText.Font = new Font("Segoe UI",12);
        notice.Controls.Add(noticeText);
        ButtonAt(notice,"Snooze 10 min",16,143,115,32,() => Quick("snooze",NoticeId()),false);
        ButtonAt(notice,"Still working",138,143,102,32,() => {
            string id = NoticeId(); var t = Get(id);
            if (t != null) { RefreshTasks(t.Id); Say("You’re still working on " + t.Title + "."); }
            ClearNotice(id);
        },false);
        finish = ButtonAt(notice,"Done",247,143,129,32,() => {
            var t = Get(NoticeId()); if(t != null) Quick(t.Submission ? "submitted" : "done",t.Id);
        },true);
    }
    string NoticeId() {
        return eventList.SelectedIndex >= 0 && eventList.SelectedIndex < outstanding.Count ? outstanding[eventList.SelectedIndex].Id : null;
    }
    void RenderNotice() {
        var t = Get(NoticeId()); if (t == null) return;
        finish.Text = t.Submission ? "Mark submitted" : "Done"; finish.AccessibleName = finish.Text;
        var nudge = outstanding[eventList.SelectedIndex];
        string msg = NudgeMessage(nudge,t);
        noticeText.Text = outstanding.Count > 1 ? outstanding.Count + " reminders · " + msg : msg;
    }
    string NudgeMessage(Nudge nudge,TaskItem t) {
        return profile.Voice(nudge.Id.StartsWith("test-") ? "test" : nudge.Kind,nudge.Title,
            t != null && t.DeadlineUtc.HasValue ? Time(t.DeadlineUtc.Value) : "",t != null && t.Submission);
    }
    void ShowNotice() {
        string current = NoticeId();
        eventList.Items.Clear();
        foreach (var n in outstanding) eventList.Items.Add(n.Title);
        if (outstanding.Count == 0) { notice.Hide(); urgent = false; Invalidate(); return; }
        int idx = outstanding.FindIndex(n => n.Id == current);
        eventList.SelectedIndex = Math.Max(0,idx);
        RenderNotice(); Place(notice); urgent = true; Invalidate();
        if (!petHidden && !notice.Visible) notice.Show(this);
    }
    void ClearNotice(string id) {
        outstanding.RemoveAll(n => n.Id == id);
        ShowNotice();
    }
    void Poll() {
        if (DateTime.UtcNow < nextPoll) return;
        try {
            var due = store.TakeDue(DateTime.UtcNow);
            if (due.Count == 0) return;
            foreach (var n in due) {
                outstanding.RemoveAll(x=>x.Id == n.Id); outstanding.Add(n);
            }
            RefreshTasks(focusId);
            ShowNotice();
            tray.BalloonTipTitle = due.Count == 1 ? profile.Name + " reminder" : profile.Name + " · " + due.Count + " reminders";
            var newest = due[0];
            var task = Get(newest.Id);
            string first = NudgeMessage(newest,task);
            if (due.Count > 1) first += " Open the inbox for the other reminders.";
            tray.BalloonTipText = first.Length > 245 ? first.Substring(0,244) + "…" : first;
            tray.ShowBalloonTip(10000);
            System.Media.SystemSounds.Asterisk.Play();
        } catch(Exception e) {
            nextPoll = DateTime.UtcNow.AddMinutes(1);
            if (!petHidden) ShowEditor();
            Say("I couldn’t check reminders. I’ll retry in a minute. " + e.Message);
        }
    }
}
