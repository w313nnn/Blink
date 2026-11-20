using System;
using System.Collections.Generic;

class LanguageChecks {
    static readonly DateTime Now = new DateTime(2026,11,17,8,0,0,DateTimeKind.Local);
    static List<TaskItem> Items=new List<TaskItem>();
    static int checks;
    static void Check(bool ok,string what) {
        if(!ok) throw new Exception(what);
        checks++;
    }
    static Intent Read(string request,TaskItem selected=null,string context=null) {
        return Language.Read(request,Now,Items,selected,context).GetAwaiter().GetResult();
    }
    static void Main() {
        var simple=Read("Remind me to stretch in 20 minutes");
        Check(simple.Action=="create" && simple.Title=="stretch" && simple.CheckUtc==Now.ToUniversalTime().AddMinutes(20),"relative seconds");
        string report="My OS lab report PDF is due Friday at 5. Check in with me tomorrow at 7 pm to see how the writing is going.";
        var question=Read(report);
        Check(question.Action=="ask" && question.Reply.Contains("5 pm"),"ambiguity");
        var create=Read("yes",null,"User: "+report+"\nBlink: "+question.Reply);
        Check(create.Action=="create" && create.Title=="OS lab report PDF" && create.File=="PDF" && create.Submission,"report detail");
        Check(create.DeadlineUtc==Now.Date.AddDays(3).AddHours(17).ToUniversalTime() && create.CheckUtc==Now.Date.AddDays(1).AddHours(19).ToUniversalTime(),"independent times");
        var morning=Read("5 am",null,report);
        Check(morning.DeadlineUtc.Value.ToLocalTime().Hour==5,"am override");
        Check(!Read("Remind me to read a PDF in 1 hour").Submission,"reading distinction");
        Check(Read("Remind me tomorrow at 7 pm to buy paper").Title=="buy paper","time before title");
        var task=new TaskItem {Title=create.Title,File="PDF",Submission=true,DeadlineUtc=create.DeadlineUtc,CheckUtc=create.CheckUtc};
        Items.Add(task);
        Check(Read("Check again in an hour",task).CheckUtc==Now.ToUniversalTime().AddHours(1),"snooze");
        Check(Read("Move the report reminder back an hour",task).CheckUtc==task.CheckUtc.Value.AddHours(1),"move");
        Check(Read("I finished writing",task).Action=="draft","draft");
        Check(Read("I finished",task).Action=="ask","avoid silent submission");
        Check(Read("I submitted it",task).Action=="submitted","submission");
        Check(Read("What should I work on now?").Id==task.Id,"triage");
        Items.Add(new TaskItem {Title="Math report",Submission=true});
        Check(Read("I submitted the report").Action=="ask","ambiguous target");
        Check(Read("I submitted the Math report").Id==Items[1].Id,"specific target");
        Check(Read("Still writing",task).Action=="working","ongoing task");
        Check(Read("I submitted the report",task).Action=="ask","selected target cannot override ambiguity");
        Check(Read("Math report",task,"User: I submitted the report\nBlink: Which task do you mean? Reply with its title.").Id==Items[1].Id,"clarified target");
        Check(Read("I submitted the science paper",task).Action=="ask","explicit unmatched target");
        Check(!Read("Read the lab report PDF by tomorrow at noon").Submission,"reading a report");
        Check(Read("Remind me to stretch at 7:30 pm").CheckUtc==Now.Date.AddHours(19.5).ToUniversalTime(),"clock with colon");
        Check(Read("Remind me to stretch next month at 5 pm").Action=="ask","unsupported date modifier");
        Check(Read("2026-12-05",null,"User: Remind me to stretch next month at 5 pm\nBlink: Which date do you mean?").CheckUtc==new DateTime(2026,12,5,17,0,0,DateTimeKind.Local).ToUniversalTime(),"unsupported date clarification");
        Check(Read("I haven't submitted the Math report",task).Action=="ask","negated completion");
        Check(Read("Don't remind me to stretch in 15 minutes").Action=="ask","negated creation");
        Items.Add(new TaskItem {Title="Work on slides",CheckUtc=Now.AddHours(2).ToUniversalTime()});
        Items.Add(new TaskItem {Title="Archive notes",Stage="done"});
        var list=Read("What reminders do I have?");
        Check(list.Action=="list" && list.Reply.Contains("Work on slides") && !list.Reply.Contains("Archive notes"),"query active reminders without a model");
        Check(Read("What's due today?").Reply.Contains("nothing due"),"a progress check is not a deadline");
        Check(Read("show my reminders for today").Reply.Contains("Work on slides") && !Read("show my reminders for today").Reply.Contains("OS lab"),"today uses local calendar boundaries");
        Console.WriteLine(checks+" language checks passed");
    }
}
