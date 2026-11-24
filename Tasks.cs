using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public sealed class TaskItem {
    public string Id { get; set; }
    public string Title { get; set; }
    public string File { get; set; }
    public string Stage { get; set; }
    public bool Submission { get; set; }
    public DateTime? DeadlineUtc { get; set; }
    public DateTime? CheckUtc { get; set; }
    public DateTime? WarnUtc { get; set; }
    public bool DeadlineDelivered { get; set; }

    public TaskItem() {
        Id = Guid.NewGuid().ToString("N");
        Stage = "working";
    }

    public TaskItem Copy() { return (TaskItem)MemberwiseClone(); }
}

public sealed class Nudge {
    // Id is the task ID. Several outstanding messages may refer to one task.
    public string Id { get; set; }
    public string MessageId { get; set; }
    public string Title { get; set; }
    public string Kind { get; set; }
    public DateTime SentUtc { get; set; }
    public string Text { get; set; }
    public bool HasExactText { get; set; }

    public Nudge Copy() { return (Nudge)MemberwiseClone(); }
}

public sealed class ChatEntry {
    public string MessageId { get; set; }
    public string TaskId { get; set; }
    public string Text { get; set; }
    public bool FromPet { get; set; }
    public DateTime AtUtc { get; set; }

    public ChatEntry Copy() { return (ChatEntry)MemberwiseClone(); }
}

public sealed class ChatPrompt {
    public string Context { get; set; }
    public string SelectedTaskId { get; set; }
    public ChatPrompt Copy() { return (ChatPrompt)MemberwiseClone(); }
}

public sealed partial class TaskStore {
    // Treat all objects in the committed state as immutable. Writers replace changed records.
    public sealed class SavedData {
        public List<TaskItem> Items { get; set; }
        public List<Nudge> Inbox { get; set; }
        public List<ChatEntry> History { get; set; }
        public Dictionary<string,ChatPrompt> Questions { get; set; }

        public SavedData() {
            Items = new List<TaskItem>();
            Inbox = new List<Nudge>();
            History = new List<ChatEntry>();
            Questions = new Dictionary<string,ChatPrompt>();
        }
        public SavedData Copy() {
            return new SavedData {
                Items = new List<TaskItem>(Items), Inbox = new List<Nudge>(Inbox), History = new List<ChatEntry>(History),
                Questions = new Dictionary<string,ChatPrompt>(Questions)
            };
        }
    }

    readonly string path;
    readonly object gate = new object();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    SavedData data;
    long revision;
    public long Revision { get { lock (gate) return revision; } }

    public TaskItem FindActive(string id) {
        if (id==null) return null;
        lock(gate) {
            var item=data.Items.FirstOrDefault(t=>t.Id==id && t.Stage!="done");
            return item==null ? null : item.Copy();
        }
    }

    public TaskStore(string path) {
        this.path = Path.GetFullPath(path);
        data = new SavedData();
        if (File.Exists(this.path)) {
            string saved = File.ReadAllText(this.path);
            if (saved.TrimStart().StartsWith("[", StringComparison.Ordinal))
                data.Items = json.Deserialize<List<TaskItem>>(saved);
            else data = json.Deserialize<SavedData>(saved);
            if (data == null || data.Items == null || data.Inbox == null || data.History == null)
                throw new InvalidDataException("Blink could not read the saved messages and tasks.");
            if (data.Questions == null) data.Questions = new Dictionary<string,ChatPrompt>();
            foreach (var task in data.Items) Validate(task);
            if (data.Items.Select(t => t.Id).Distinct().Count() != data.Items.Count)
                throw new InvalidDataException("The task list has duplicate identifiers.");
            foreach (var n in data.Inbox) n.SentUtc = Utc(n.SentUtc).Value;
            foreach (var e in data.History) e.AtUtc = Utc(e.AtUtc).Value;
            TryRefreshChecklist();
            return;
        }

        string legacyPath = Path.Combine(Path.GetDirectoryName(this.path), "reminders.json");
        if (!File.Exists(legacyPath)) { TryRefreshChecklist(); return; }
        var old = json.Deserialize<List<LegacyReminder>>(File.ReadAllText(legacyPath));
        if (old == null) throw new InvalidDataException("Blink could not import the previous reminder list.");
        var imported = new SavedData();
        imported.Items = old.Select(r => new TaskItem {
            Title = r.Message, CheckUtc = new DateTime(r.UtcTicks, DateTimeKind.Utc)
        }).ToList();
        foreach (var task in imported.Items) Validate(task);
        Commit(imported);
    }

    public List<TaskItem> Items {
        get { lock (gate) return data.Items.Select(t => t.Copy()).ToList(); }
    }
    public List<Nudge> Inbox {
        get { lock (gate) return data.Inbox.OrderBy(n => n.SentUtc).Select(n => n.Copy()).ToList(); }
    }
    public List<ChatEntry> History {
        get { lock (gate) return data.History.Select(e => e.Copy()).ToList(); }
    }
    public ChatPrompt Question(string messageId) {
        lock (gate) {
            ChatPrompt question;
            return data.Questions.TryGetValue(messageId,out question) && question != null ? question.Copy() : null;
        }
    }

    static void Put(SavedData next, TaskItem value) {
        var task = value.Copy();
        Validate(task);
        int index = next.Items.FindIndex(t => t.Id == task.Id);
        task.DeadlineDelivered = index >= 0 && next.Items[index].DeadlineUtc == task.DeadlineUtc && next.Items[index].DeadlineDelivered;
        if (index >= 0) next.Items[index] = task;
        else next.Items.Add(task);
        if (task.Stage == "done") {
            foreach (var n in next.Inbox.Where(n=>n.Id==task.Id)) next.Questions.Remove(n.MessageId);
            next.Inbox.RemoveAll(n => n.Id == task.Id);
        }
    }

    public void Save(TaskItem item) {
        lock (gate) {
            var next = data.Copy();
            Put(next,item);
            Commit(next);
        }
    }

    public void Delete(string id) {
        lock (gate) {
            var next = data.Copy();
            if (next.Items.RemoveAll(t => t.Id == id) == 0) return;
            foreach (var e in next.History.Where(e => e.TaskId == id)) next.Questions.Remove(e.MessageId);
            next.Inbox.RemoveAll(n => n.Id == id);
            next.History.RemoveAll(e => e.TaskId == id);
            Commit(next);
        }
    }

    public List<Nudge> TakeDue(DateTime utcNow) { return TakeDue(utcNow,null); }

    public List<Nudge> TakeDue(DateTime utcNow, Func<Nudge,string> makeText) {
        lock (gate) {
            utcNow = Utc(utcNow).Value;
            var alerts = new List<Nudge>();
            SavedData next = null;
            for (int i = 0; i < data.Items.Count; i++) {
                var task = data.Items[i];
                if (task.Stage == "done") continue;
                bool deadline = !task.DeadlineDelivered && task.DeadlineUtc <= utcNow;
                bool warning = task.WarnUtc <= utcNow;
                bool check = task.CheckUtc <= utcNow;
                if (!deadline && !warning && !check) continue;

                // Collapse missed milestones for one task into the most time-sensitive message.
                var fired = task.Copy();
                if (deadline) fired.DeadlineDelivered = true;
                if (warning) fired.WarnUtc = null;
                if (check) fired.CheckUtc = null;
                if (next == null) next = data.Copy();
                next.Items[i] = fired;
                var n = new Nudge { Id = task.Id, Title = task.Title, MessageId = Guid.NewGuid().ToString("N"),
                    SentUtc = utcNow, Kind = deadline ? "deadline" : warning ? "warning" : "check-in" };
                string text = makeText == null ? null : makeText(n.Copy());
                n.HasExactText = !String.IsNullOrWhiteSpace(text);
                n.Text = n.HasExactText ? text : "Reminder: " + task.Title;
                next.Inbox.Add(n);
                next.History.Add(new ChatEntry { MessageId=n.MessageId, TaskId=n.Id, AtUtc=n.SentUtc, Text=n.Text, FromPet=true });
                alerts.Add(n.Copy());
            }
            if (next != null) Commit(next);
            return alerts;
        }
    }

    public void RememberText(string messageId,string text) {
        if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a message.", "text");
        lock (gate) {
            int i = data.Inbox.FindIndex(n => n.MessageId == messageId);
            if (i < 0 || data.Inbox[i].HasExactText) return;
            var next = data.Copy();
            var nudge = next.Inbox[i].Copy();
            nudge.Text = text; nudge.HasExactText = true; next.Inbox[i] = nudge;
            int entry = next.History.FindIndex(e => e.FromPet && e.MessageId == messageId);
            var updated = next.History[entry].Copy(); updated.Text = text; next.History[entry] = updated;
            Commit(next);
        }
    }

    public bool Answer(string messageId,string reply,TaskItem changed) {
        return Answer(messageId,reply,changed,null);
    }

    public bool Answer(string messageId,string reply,TaskItem changed,string petResponse) {
        if (String.IsNullOrWhiteSpace(reply)) throw new ArgumentException("Enter a reply.", "reply");
        lock (gate) {
            var original = data.Inbox.FirstOrDefault(n => n.MessageId == messageId);
            if (original == null) return false;
            if (changed != null && changed.Id != original.Id)
                throw new ArgumentException("The reply belongs to a different task.", "changed");
            var next = data.Copy();
            if (changed != null) Put(next,changed);
            next.Inbox.RemoveAll(n => n.MessageId == messageId);
            next.Questions.Remove(messageId);
            AppendExchange(next,messageId,original.Id,reply,petResponse);
            Commit(next);
            return true;
        }
    }

    public bool Clarify(string messageId,string reply,string prompt) {
        if (String.IsNullOrWhiteSpace(reply) || String.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A clarification needs both a reply and a question.");
        lock (gate) {
            var original = data.Inbox.FirstOrDefault(n => n.MessageId == messageId);
            if (original == null) return false;
            var next = data.Copy();
            AppendExchange(next,messageId,original.Id,reply,prompt);
            Commit(next);
            return true;
        }
    }

    public void Chat(string messageId,string taskId,string userText,string petText) {
        CommitChat(messageId,taskId,userText,petText,null,null);
    }

    // Store what Blink says together with the task change or question that makes it true.
    public void CommitChat(string messageId,string taskId,string userText,string petText,TaskItem change,ChatPrompt nextQuestion) {
        if (String.IsNullOrWhiteSpace(messageId) || String.IsNullOrWhiteSpace(taskId) ||
            String.IsNullOrWhiteSpace(userText) || String.IsNullOrWhiteSpace(petText))
            throw new ArgumentException("A conversation needs a thread and both messages.");
        lock (gate) {
            var next = data.Copy();
            if (change != null) Put(next,change);
            if (nextQuestion == null) next.Questions.Remove(messageId);
            else next.Questions[messageId] = nextQuestion.Copy();
            AppendExchange(next,messageId,taskId,userText,petText);
            Commit(next);
        }
    }

    static void AppendExchange(SavedData next,string messageId,string taskId,string userText,string petText) {
        var now = DateTime.UtcNow;
        next.History.Add(new ChatEntry { MessageId=messageId, TaskId=taskId, AtUtc=now, Text=userText, FromPet=false });
        if (!String.IsNullOrWhiteSpace(petText))
            next.History.Add(new ChatEntry { MessageId=messageId, TaskId=taskId, AtUtc=now, Text=petText, FromPet=true });
    }

    static void Validate(TaskItem task) {
        if (task == null || String.IsNullOrWhiteSpace(task.Id) || String.IsNullOrWhiteSpace(task.Title))
            throw new InvalidDataException("Each task needs an ID and a title.");
        if (task.Stage != "working" && task.Stage != "draft ready" && task.Stage != "done")
            throw new InvalidDataException("Unrecognized task stage.");
        task.DeadlineUtc = Utc(task.DeadlineUtc);
        task.CheckUtc = Utc(task.CheckUtc);
        task.WarnUtc = Utc(task.WarnUtc);
    }

    static DateTime? Utc(DateTime? value) {
        if (!value.HasValue) return null;
        return value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime()
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }

    void Commit(SavedData next) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(next));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            bool checklistChanged = checklistNeedsRefresh || !next.Items.SequenceEqual(data.Items) || !next.Inbox.SequenceEqual(data.Inbox);
            data = next;
            revision++;
            if (checklistChanged) TryRefreshChecklist();
        } finally {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public sealed class LegacyReminder {
        public string Message { get; set; }
        public long UtcTicks { get; set; }
    }
}
