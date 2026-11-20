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
    public string Id { get; set; }
    public string Title { get; set; }
    public string Kind { get; set; }
}

public sealed class TaskStore {
    readonly string path;
    readonly object gate = new object();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    List<TaskItem> items;

    public TaskStore(string path) {
        this.path = Path.GetFullPath(path);
        if (System.IO.File.Exists(this.path)) {
            items = json.Deserialize<List<TaskItem>>(System.IO.File.ReadAllText(this.path));
            if (items == null) throw new InvalidDataException("Blink could not read the saved task list.");
            foreach (var task in items) Validate(task);
            if (items.Select(t => t.Id).Distinct().Count() != items.Count)
                throw new InvalidDataException("The task list has duplicate identifiers.");
            return;
        }

        items = new List<TaskItem>();
        string legacyPath = Path.Combine(Path.GetDirectoryName(this.path), "reminders.json");
        if (!System.IO.File.Exists(legacyPath)) return;
        var old = json.Deserialize<List<LegacyReminder>>(System.IO.File.ReadAllText(legacyPath));
        if (old == null) throw new InvalidDataException("Blink could not import the previous reminder list.");
        var imported = old.Select(r => new TaskItem {
            Title = r.Message, CheckUtc = new DateTime(r.UtcTicks, DateTimeKind.Utc)
        }).ToList();
        foreach (var task in imported) Validate(task);
        Commit(imported);
    }

    public List<TaskItem> Items {
        get { lock (gate) return items.Select(t => t.Copy()).ToList(); }
    }

    public void Save(TaskItem item) {
        lock (gate) {
            var task = item.Copy();
            Validate(task);
            var next = new List<TaskItem>(items);
            int index = next.FindIndex(t => t.Id == task.Id);
            task.DeadlineDelivered = index >= 0 && next[index].DeadlineUtc == task.DeadlineUtc && next[index].DeadlineDelivered;
            if (index >= 0) next[index] = task;
            else next.Add(task);
            Commit(next);
        }
    }

    public void Delete(string id) {
        lock (gate) {
            var next = items.Where(t => t.Id != id).ToList();
            if (next.Count != items.Count) Commit(next);
        }
    }

    public List<Nudge> TakeDue(DateTime utcNow) {
        lock (gate) {
            var alerts = new List<Nudge>();
            List<TaskItem> next = null;
            for (int i = 0; i < items.Count; i++) {
                var task = items[i];
                if (task.Stage == "done") continue;
                bool deadline = !task.DeadlineDelivered && task.DeadlineUtc <= utcNow;
                bool warning = task.WarnUtc <= utcNow;
                bool check = task.CheckUtc <= utcNow;
                if (!deadline && !warning && !check) continue;

                // Collapse missed milestones for one task into the most time-sensitive nudge.
                var fired = task.Copy();
                if (deadline) fired.DeadlineDelivered = true;
                if (warning) fired.WarnUtc = null;
                if (check) fired.CheckUtc = null;
                if (next == null) next = new List<TaskItem>(items);
                next[i] = fired;
                alerts.Add(new Nudge { Id = task.Id, Title = task.Title,
                    Kind = deadline ? "deadline" : warning ? "warning" : "check-in" });
            }
            if (next != null) Commit(next);
            return alerts;
        }
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
        // Fields explicitly represent UTC. Local values from controls retain their meaning.
        return value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime()
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }

    void Commit(List<TaskItem> next) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            byte[] data = Encoding.UTF8.GetBytes(json.Serialize(next));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, null);
            else System.IO.File.Move(temp, path);
            items = next;
        } finally {
            if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp);
        }
    }

    public sealed class LegacyReminder {
        public string Message { get; set; }
        public long UtcTicks { get; set; }
    }
}
