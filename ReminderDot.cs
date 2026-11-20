using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class Reminder {
    public string Message { get; set; }
    public long UtcTicks { get; set; }
    public override string ToString() {
        return new DateTime(UtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g") + "    " + Message;
    }
}

class ReminderDot : Form {
    readonly string file;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    List<Reminder> items;
    readonly Form panel = new Form();
    readonly TextBox input = new TextBox();
    readonly DateTimePicker when = new DateTimePicker();
    readonly ListBox upcoming = new ListBox();
    readonly Label status = new Label();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Button interpret = new Button();
    bool alerting, quitting;

    [STAThread] static void Main() {
        bool first;
        using (var single = new System.Threading.Mutex(true, @"Local\ReminderDot.v1", out first)) {
            if (!first) { MessageBox.Show("Reminder Dot is already running. Click the teal dot on your desktop."); return; }
            Application.EnableVisualStyles();
            try { Application.Run(new ReminderDot()); }
            catch (Exception e) { MessageBox.Show(e.Message, "Reminder Dot"); }
        }
    }

    public ReminderDot() {
        file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReminderDot", "reminders.json");
        items = File.Exists(file) ? json.Deserialize<List<Reminder>>(File.ReadAllText(file)) : new List<Reminder>();
        FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; ClientSize = new Size(46,46);
        var edge = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(edge.Right - 70, edge.Bottom - 82);
        BackColor = Color.FromArgb(15,99,91);
        using (var shape = new GraphicsPath()) { shape.AddEllipse(0,0,46,46); Region = new Region(shape); }
        Paint += delegate(object sender, PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(Brushes.Honeydew, 15,15,16,16);
        };
        Point start = Point.Empty, origin = Point.Empty;
        bool dragged = false;
        MouseDown += (s,e) => { start = Cursor.Position; origin = Location; dragged = false; };
        MouseMove += (s,e) => {
            if (e.Button != MouseButtons.Left) return;
            int dx = Cursor.Position.X-start.X, dy = Cursor.Position.Y-start.Y;
            if (Math.Abs(dx)+Math.Abs(dy)>4) dragged = true;
            if (dragged) Location = new Point(origin.X+dx,origin.Y+dy);
        };
        MouseUp += (s,e) => { if (!dragged && e.Button == MouseButtons.Left) OpenPanel(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open reminders", null, (s,e) => OpenPanel());
        var startup = new ToolStripMenuItem("Start when I sign in");
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        menu.Opening += delegate { using (var key = Registry.CurrentUser.OpenSubKey(runKey)) startup.Checked = key != null && key.GetValue("ReminderDot") != null; };
        startup.Click += delegate {
            try { using (var key = Registry.CurrentUser.CreateSubKey(runKey)) {
                if (startup.Checked) key.DeleteValue("ReminderDot",false);
                else key.SetValue("ReminderDot", "\"" + Application.ExecutablePath + "\" --background");
            }} catch (Exception ex) { MessageBox.Show(ex.Message, "Start at sign-in"); }
        };
        menu.Items.Add(startup);
        menu.Items.Add("Quit",null,(s,e) => { quitting = true; Close(); });
        ContextMenuStrip = menu;
        BuildPanel();
        timer.Interval = 1000; timer.Tick += (s,e) => CheckDue(); timer.Start();
        Shown += delegate { if (!Environment.GetCommandLineArgs().Contains("--background")) OpenPanel(); };
    }

    void OpenPanel() {
        panel.Show(); panel.Activate(); input.Focus();
    }
    void BuildPanel() {
        panel.Text = "Reminder Dot"; panel.Font = new Font("Segoe UI",10);
        panel.ClientSize = new Size(460,365); panel.FormBorderStyle = FormBorderStyle.FixedDialog;
        panel.MaximizeBox = false; panel.StartPosition = FormStartPosition.CenterScreen;
        panel.FormClosing += (s,e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; panel.Hide(); } };
        Label title = new Label { Text = "What should I remind you about?", AutoSize = true };
        title.Location = new Point(16,15);
        input.SetBounds(16,45,428,28); input.MaxLength = 500;
        interpret.Text = "Read time from text"; interpret.SetBounds(16,80,170,32);
        interpret.Click += async (s,e) => await Interpret();
        when.Format = DateTimePickerFormat.Custom; when.CustomFormat = "ddd, dd MMM yyyy   HH:mm:ss";
        when.Value = DateTime.Now.AddMinutes(10); when.SetBounds(16,123,278,30);
        var save = new Button { Text = "Set reminder" }; save.SetBounds(305,120,139,34);
        save.Click += delegate {
            if (input.Text.Trim().Length == 0 || when.Value <= DateTime.Now) { status.Text = "Enter a message and choose a future time."; return; }
            var next = new List<Reminder>(items) { new Reminder { Message = input.Text.Trim(), UtcTicks = when.Value.ToUniversalTime().Ticks } };
            if (Commit(next)) { status.Text = "Scheduled for " + when.Value.ToString("f"); input.Clear(); }
        };
        status.Text = "Try “Stretch in 20 minutes”, or choose a time."; status.SetBounds(16,163,428,44);
        var label = new Label { Text = "Upcoming reminders", AutoSize = true, Location = new Point(16,211) };
        upcoming.SetBounds(16,237,428,75); upcoming.HorizontalScrollbar = true;
        var remove = new Button { Text = "Remove selected" }; remove.SetBounds(16,318,155,32);
        remove.Click += delegate { var selected = upcoming.SelectedItem as Reminder; if (selected != null && Commit(items.Where(x=>x!=selected).ToList())) status.Text="Reminder removed."; };
        var test = new Button { Text = "Test in 10 seconds" };
        test.SetBounds(262,318,182,32);
        test.Click += delegate {
            var sample = new Reminder {
                Message = "Your test reminder is here. Click OK to dismiss it.",
                UtcTicks = DateTime.UtcNow.AddSeconds(10).Ticks
            };
            if (Commit(new List<Reminder>(items) { sample }))
                status.Text = "Test set. You can close this window and wait for the pop-up.";
        };
        panel.Controls.AddRange(new Control[]{ title,input,interpret,when,save,status,label,upcoming,remove,test });
        RefreshList();
    }

    void RefreshList() {
        upcoming.Items.Clear();
        upcoming.Items.AddRange(items.OrderBy(x=>x.UtcTicks).Cast<object>().ToArray());
    }
    bool Commit(List<Reminder> next) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllText(temp,json.Serialize(next));
            if (File.Exists(file)) File.Replace(temp,file,null); else File.Move(temp,file);
            items = next; RefreshList(); return true;
        } catch (Exception e) { MessageBox.Show(e.Message,"Could not save reminders"); return false; }
    }
    void CheckDue() {
        if (alerting) return;
        var due = items.OrderBy(x=>x.UtcTicks).FirstOrDefault();
        if (due == null || due.UtcTicks > DateTime.UtcNow.Ticks) return;
        alerting = true;
        try {
            System.Media.SystemSounds.Asterisk.Play();
            MessageBox.Show(this,due.Message,"Reminder Dot",MessageBoxButtons.OK,MessageBoxIcon.Information);
            Commit(items.Where(x=>x!=due).ToList());
        } finally { alerting = false; }
    }

    internal static bool Relative(string request, DateTime now, out DateTime date, out string message) {
        date = now; message = request;
        var match = Regex.Match(request,@"^(?<msg>.+?)\s+in\s+(?<n>\d{1,6})\s*(?<unit>seconds?|minutes?|hours?|days?)\s*[.!]?$",RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        int n = int.Parse(match.Groups["n"].Value);
        if (n == 0) return false;
        switch (char.ToLowerInvariant(match.Groups["unit"].Value[0])) {
            case 's': date = now.AddSeconds(n); break;
            case 'm': date = now.AddMinutes(n); break;
            case 'h': date = now.AddHours(n); break;
            default: date = now.AddDays(n); break;
        }
        message = Regex.Replace(match.Groups["msg"].Value,@"^remind me to\s+","",RegexOptions.IgnoreCase);
        return true;
    }
    async Task Interpret() {
        string request = input.Text.Trim(), message;
        if (request.Length == 0) { status.Text = "Type a reminder first."; return; }
        DateTime date;
        interpret.Enabled = false;
        try {
            if (!Relative(request, DateTime.Now, out date, out message)) {
                status.Text = "Checking the local AI model...";
                using (var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
                    client.Timeout = TimeSpan.FromSeconds(60);
                    const string endpoint = "http://127.0.0.1:11434";
                    var tags = json.Deserialize<ModelList>(await client.GetStringAsync(endpoint + "/api/tags"));
                    var model = tags.models.FirstOrDefault(m => !m.name.Contains("cloud"));
                    if (model == null) throw new InvalidOperationException();
                    string instruction = "Local time is " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ". Extract one reminder. Return JSON containing message and localTime (yyyy-MM-dd HH:mm:ss). If ambiguous or missing a time, return {}.";
                    var body = new { model = model.name, stream = false, format = "json", messages = new[]{ new {role="system",content=instruction}, new {role="user",content=request} } };
                    using (var reply = await client.PostAsync(endpoint + "/api/chat",new StringContent(json.Serialize(body),Encoding.UTF8,"application/json"))) {
                        reply.EnsureSuccessStatusCode();
                        var parsed = json.Deserialize<AiReminder>(json.Deserialize<Chat>(await reply.Content.ReadAsStringAsync()).message.content);
                        if (String.IsNullOrWhiteSpace(parsed.message) || !DateTime.TryParseExact(parsed.localTime,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)) {
                            status.Text = "Please choose a date and time below."; return;
                        }
                        message = parsed.message;
                    }
                }
            }
            if (request != input.Text.Trim()) { status.Text = "Text changed. Read the time again."; return; }
            if (date <= DateTime.Now || date > when.MaxDate) { status.Text = "Choose a future date and time."; return; }
            when.Value = date; input.Text = message; status.Text = "Review the message and time, then click Set reminder.";
        } catch (Exception) { status.Text = "AI unavailable. Choose a time or try “Stretch in 20 minutes”."; }
        finally { interpret.Enabled = true; }
    }
    public class Model { public string name {get;set;} }
    public class ModelList { public Model[] models {get;set;} }
    public class Chat { public Content message {get;set;} }
    public class Content { public string content {get;set;} }
    public class AiReminder { public string message {get;set;} public string localTime {get;set;} }
}
