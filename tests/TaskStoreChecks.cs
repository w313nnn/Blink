using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

class TaskStoreChecks {
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static void Main(string[] args) {
        var dir = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "reminders.json");
        var path = Path.Combine(dir, "tasks.json");
        var now = new DateTime(2031, 6, 1, 0, 3, 0, DateTimeKind.Utc);
        File.WriteAllText(old, new JavaScriptSerializer().Serialize(new[]{new {Message="Stretch",UtcTicks=now.Ticks}}));
        var store = new TaskStore(path);
        Check(store.Items.Single().CheckUtc == now && File.Exists(old), "legacy reminder migration");
        var sample = store.Items.Single();
        var got = store.TakeDue(now);
        Check(got.Single().Kind == "check-in", "legacy reminder fires");
        store.Delete(sample.Id);
        store = new TaskStore(path);
        Check(store.Items.Count == 0, "migration does not repeat on restart");
        var report = new TaskItem { Title = "Report", DeadlineUtc=now.AddHours(2), CheckUtc=now, WarnUtc=now.AddHours(1), Submission=true, File="report.pdf" };
        store.Save(report);
        var copy = store.Items.Single(); copy.Title = "edit";
        Check(store.Items.Single().Title == "Report", "isolated task snapshots");
        Check(store.TakeDue(now).Single().Kind == "check-in", "separate progress check");
        report = store.Items.Single(); report.CheckUtc = now.AddMinutes(20); report.Stage = "draft ready"; store.Save(report);
        store = new TaskStore(path);
        Check(store.Items.Single().Stage=="draft ready" && store.Items.Single().DeadlineUtc==now.AddHours(2), "draft and snooze preserve deadline after restart");
        var all = store.TakeDue(now.AddDays(1));
        Check(all.Count == 1 && all[0].Kind == "deadline", "missed milestones coalesced");
        store = new TaskStore(path);
        Check(store.TakeDue(now.AddDays(1)).Count == 0, "delivered events stay consumed after restart");
        report = store.Items.Single(); report.DeadlineUtc=now.AddDays(2); store.Save(report);
        Check(store.TakeDue(now.AddDays(2)).Single().Kind == "deadline", "rescheduled deadline rearms");
        report.Stage="done"; report.CheckUtc=now; store.Save(report);
        Check(store.TakeDue(now.AddDays(3)).Count==0, "completed tasks stay quiet");
        var second = new TaskItem{Title="Read",CheckUtc=now};
        using (var handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            bool failed=false;
            try { store.Save(second); } catch(IOException) { failed=true; }
            Check(failed && store.Items.Count==1,"failed replacement leaves current list intact");
        }
        store.Save(second);
        var two = new List<Nudge>[2];
        Parallel.For(0,2,i=>two[i]=store.TakeDue(now));
        Check(two[0].Count+two[1].Count == 1, "simultaneous ticks deliver once");
    }
}
