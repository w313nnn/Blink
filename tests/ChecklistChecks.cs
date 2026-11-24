using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

class ChecklistChecks {
    static readonly DateTime Now = new DateTime(2026,11,25,0,39,0,DateTimeKind.Local);
    static void Check(bool ok,string name) {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    static Intent Read(string text, TaskStore store, TaskItem selected = null) {
        return Language.Read(text, Now, store.Items, selected, null).GetAwaiter().GetResult();
    }
    static void Main(string[] args) {
        string dir=Path.Combine(args[0],"checklist-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(dir,"tasks.json");
        var store=new TaskStore(path);
        Check(File.ReadAllText(store.ChecklistPath).Contains("clear"),"checklist available on first run");
        var capture=Read("Add a task to review lecture notes",store);
        Check(capture.Action=="create" && capture.Title=="review lecture notes" && !capture.DeadlineUtc.HasValue && !capture.CheckUtc.HasValue,"untimed task capture");
        var task=new TaskItem{Title=capture.Title};
        store.Save(task);
        var deadline=Read("Add a task to submit the art report by tomorrow at noon",store);
        Check(deadline.Action=="create" && deadline.Title=="submit the art report" && deadline.Submission && deadline.DeadlineUtc==Now.Date.AddDays(1).AddHours(12).ToUniversalTime(),"task capture with a submission deadline");
        var report=new TaskItem{Title=deadline.Title,Submission=true,DeadlineUtc=deadline.DeadlineUtc,CheckUtc=Now.AddHours(1).ToUniversalTime()};
        store.Save(report);
        string file=File.ReadAllText(store.ChecklistPath);
        Check(file.IndexOf(report.Title)<file.IndexOf(task.Title) && file.Contains("Check-in") && file.Contains("Deadline"),"next timed item listed ahead of untimed item");
        var rename=Read("Rename lecture notes to review chapter five",store);
        Check(rename.Action=="rename" && rename.Id==task.Id && rename.Title=="review chapter five","rename by unambiguous title fragment");
        task.Title=rename.Title; store.Save(task);
        var cancel=Read("Cancel the art report reminder",store);
        Check(cancel.Action=="cancel" && cancel.Id==report.Id,"cancel specific reminder");
        var other=new TaskItem{Title="edit the art report"};
        store.Save(other);
        var ambiguous=Read("cancel art report",store,report);
        Check(ambiguous.Action=="ask","ambiguous cancellation leaves both tasks active");
        var clarified=Language.Read(other.Title,Now,store.Items,report,"User: cancel art report\nBlink: "+ambiguous.Reply).GetAwaiter().GetResult();
        Check(clarified.Action=="cancel" && clarified.Id==other.Id,"the next reply identifies the task to cancel");
        Check(Read("What's on my checklist?",store).Action=="list","checklist question works offline");
        report.Stage="draft ready"; store.Save(report);
        Check(File.ReadAllText(store.ChecklistPath).Contains("Submission still pending"),"draft remains on checklist until submitted");
        report.Stage="done";store.Save(report);
        store=new TaskStore(path);
        file=File.ReadAllText(store.ChecklistPath);
        Check(!file.Contains(report.Title) && file.Contains(task.Title) && store.Items.Any(t=>t.Id==report.Id && t.Stage=="done"),"completion removed from checklist and survived restart with history");
        using(var locked=new FileStream(store.ChecklistPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            task.Title="review chapter six"; store.Save(task);
            Check(store.Items.Any(t=>t.Title=="review chapter six"),"locked text copy does not prevent durable task update");
        }
        store.RefreshChecklist();
        Check(File.ReadAllText(store.ChecklistPath).Contains("review chapter six"),"checklist can be refreshed from canonical task data");
    }
}
