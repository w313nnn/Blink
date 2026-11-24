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
        Check(!got.Single().IsRead,"delivered reminder starts unread");
        store.MarkRead(new System.Collections.Generic.HashSet<string>{got.Single().MessageId});
        store=new TaskStore(path);
        Check(store.Inbox.Single().IsRead,"reading persists and leaves the reminder available for reply");
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

        string previousPath = Path.Combine(dir,"previous-tasks.json");
        var upload = new TaskItem { Title="Upload PDF", CheckUtc=now, DeadlineUtc=now.AddHours(1), Submission=true };
        var read = new TaskItem { Title="Read chapter", CheckUtc=now };
        File.WriteAllText(previousPath,new JavaScriptSerializer().Serialize(new[]{upload,read}));
        var chat = new TaskStore(previousPath);
        var delivered = chat.TakeDue(now, n => "How is " + n.Title + " going?");
        Check(delivered.Count==2 && delivered.Select(n=>n.MessageId).Distinct().Count()==2, "v1 task list becomes distinct inbox messages");
        var m = delivered[0];
        m.Text="changed snapshot";
        chat = new TaskStore(previousPath);
        Check(chat.TakeDue(now).Count==0 && chat.Inbox.Count==2 && chat.History.Count==2, "restart restores pending messages without duplicate delivery");
        Check(chat.Inbox[0].Text=="How is Upload PDF going?" && chat.History[0].Text==chat.Inbox[0].Text &&
              chat.Inbox[0].SentUtc.Kind==DateTimeKind.Utc && chat.History[0].AtUtc.Kind==DateTimeKind.Utc, "sent wording and UTC timestamps persisted");
        chat.RememberText(m.MessageId,"another message");
        Check(chat.History[0].Text=="How is Upload PDF going?", "exact sent wording is immutable");
        var sameTask = chat.TakeDue(now.AddHours(1)).Single();
        Check(chat.Inbox.Count==3 && sameTask.Id==m.Id && sameTask.MessageId!=m.MessageId, "second event preserves earlier pending message for same task");
        chat.RememberText(sameTask.MessageId,"Your upload is due now.");
        chat.RememberText(sameTask.MessageId,"Changed again");
        Check(chat.Inbox.Last().Text=="Your upload is due now.", "fallback wording can be recorded exactly once");
        var deadline = chat.Items.Single(t=>t.Id==m.Id);
        deadline.CheckUtc=now.AddMinutes(30);
        bool mismatch=false;
        try { chat.Answer(m.MessageId,"Snooze",read); } catch(ArgumentException) { mismatch=true; }
        Check(mismatch && chat.History.Count==3, "reply cannot mutate a different task");
        Check(chat.Clarify(m.MessageId,"later","When should I check back?") && chat.Inbox.Count==3 &&
              chat.History.Last().FromPet && chat.History.Last().Text=="When should I check back?", "clarifying reply stays pending with both sides recorded");
        int count = chat.History.Count;
        using (var handle=new FileStream(previousPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            bool failed=false;
            try { chat.Answer(m.MessageId,"In 30 minutes",deadline); } catch(IOException) { failed=true; }
            Check(failed && chat.History.Count==count && chat.Inbox.Count==3 && !chat.Items.Single(t=>t.Id==m.Id).CheckUtc.HasValue, "reply rolls back task, history and pending state together");
        }
        Check(chat.Answer(m.MessageId,"In 30 minutes",deadline) && !chat.Answer(m.MessageId,"Duplicate",deadline), "reply replay is idempotent");
        chat = new TaskStore(previousPath);
        Check(chat.Inbox.Count==2 && chat.Inbox.Any(n=>n.MessageId==sameTask.MessageId) && chat.Items.Single(t=>t.Id==m.Id).CheckUtc==deadline.CheckUtc &&
              chat.History.Last().Text=="In 30 minutes" && !chat.History.Last().FromPet, "one reply targets its message and persists scheduling changes");
        chat.TakeDue(now.AddHours(2));
        var complete=chat.Items.Single(t=>t.Id==m.Id); complete.Stage="done";
        Check(chat.Answer(sameTask.MessageId,"I submitted it",complete) && chat.Inbox.All(n=>n.Id!=m.Id), "completion closes obsolete prompts and leaves transcript");
        chat.Delete(read.Id);
        Check(chat.Inbox.Count==0 && chat.History.All(e=>e.TaskId!=read.Id) && chat.History.Any(e=>e.TaskId==m.Id), "deleting a task cleans only its own messages");
        var another=new TaskItem{Title="Stretch",CheckUtc=now}; chat.Save(another);
        var question=chat.TakeDue(now).Single();
        chat.Chat("__general","__general","Hello","Hi there");
        Check(chat.Inbox.Count==1 && chat.History.Last().TaskId=="__general","general chat preserves pending reminders");
        chat.Answer(question.MessageId,"All done",null,"Thanks for telling me.");
        chat = new TaskStore(previousPath);
        var pair=chat.History.Where(e=>e.MessageId==question.MessageId).Skip(1).ToList();
        Check(pair.Count==2 && pair[0].Text=="All done" && !pair[0].FromPet && pair[1].Text=="Thanks for telling me." && pair[1].FromPet &&
              pair[0].AtUtc.Kind==DateTimeKind.Utc && pair[1].AtUtc>=pair[0].AtUtc && chat.Inbox.Count==0,
              "reply and pet confirmation persist in order with UTC time");
        chat.Chat(question.MessageId,another.Id,"One more thing","I'm here.");
        Check(chat.Inbox.Count==0 && chat.History.Last().MessageId==question.MessageId,"chat in an answered thread keeps it answered");
    }
}
