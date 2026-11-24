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
    static Intent At(DateTime clock,string text,string context = null) {
        return Language.Read(text,clock,new List<TaskItem>(),null,context).GetAwaiter().GetResult();
    }
    static string Turn(string request,Intent question) {
        return "User: "+request+"\nBlink: "+(question.ContextReply ?? question.Reply);
    }
    static void CheckReminder(Intent reminder,string title,DateTime localWhen,string scenario) {
        Check(reminder.Action=="create" && reminder.Title==title && reminder.CheckUtc==localWhen.ToUniversalTime()
            && !reminder.DeadlineUtc.HasValue && !reminder.Submission,scenario);
    }
    static void CheckCalendarClarification() {
        var clock=new DateTime(2026,11,25,0,39,0,DateTimeKind.Local);
        const string request="remind me to drink water on 12:40am 21th november 2026";
        CheckReminder(At(clock,request.Replace("21th november","26th november")),"drink water",
            clock.Date.AddDays(1).AddMinutes(40),"explicit future day plus midnight clock");
        CheckReminder(At(clock,request.Replace("november 2026","december 2026")),"drink water",
            new DateTime(2026,12,21,0,40,0,DateTimeKind.Local),"nonstandard ordinal with a future date");
        var question=At(clock,request);
        Check(question.Action=="ask" && !question.CheckUtc.HasValue && !question.DeadlineUtc.HasValue,
            "an explicitly past calendar date needs clarification");
        string context=Turn(request,question);
        CheckReminder(At(clock,"2026-11-25",context),"drink water",clock.AddMinutes(1),
            "screenshot date reply keeps the task and its original 00:40 clock time");
        var late=At(clock.AddMinutes(2),"2026-11-25",context);
        Check(late.Action=="ask" && !late.CheckUtc.HasValue,"clock elapsed while the question was open");
        CheckReminder(At(clock.AddMinutes(2),"2026-11-26",context+"\n"+Turn("2026-11-25",late)),
            "drink water",clock.Date.AddDays(1).AddMinutes(40),"a second date reply repairs the elapsed date");
        var invalid=At(clock,"2026-11-31",context);
        Check(invalid.Action=="ask" && !invalid.CheckUtc.HasValue,"invalid calendar day does not schedule");
        var midnight=new DateTime(2026,12,31,23,59,0,DateTimeKind.Local);
        var yearEnd=At(midnight,request);
        CheckReminder(At(midnight,"2027-01-01",Turn(request,yearEnd)),"drink water",
            midnight.Date.AddDays(1).AddMinutes(40),"date clarification crosses midnight and year end");
        Check(At(clock,"2026-11-25").Action=="ask","a date on its own does not create a task");
        CheckReminder(At(clock,"Remind me to call Alex in 2 hours",context),"call Alex",clock.AddHours(2),
            "an independent reminder can replace a pending date question");
    }
    static void Main() {
        CheckCalendarClarification();
        CheckReminder(Read("remind me to call Mum on 04-07-2027 at 11:15"),"call Mum",new DateTime(2027,7,4,11,15,0),"day first date and 24 hour clock");
        CheckReminder(Read("remind me to call Mum this Wednesday at 4 pm"),"call Mum",Now.Date.AddDays(1).AddHours(16),"this Wednesday");
        string friday="submit report by this Friday";
        var deadline=Read("5 pm",null,Turn(friday,Read(friday)));
        Check(deadline.DeadlineUtc==Now.Date.AddDays(3).AddHours(17).ToUniversalTime(),"by this Friday retains deadline");
        Check(!Language.IsNewRequest("the 2nd at 14:30"),"a conversational ordinal answers the pending question");
        string week="remind me to call Mum the first week of July";
        var pick=Read(week);
        Check(pick.Action=="ask" && pick.Reply.Contains("7 July 2027"),"week of month asks for day");
        CheckReminder(Read("the 2nd at 14:30",null,Turn(week,pick)),"call Mum",new DateTime(2027,7,2,14,30,0),"week reply preserves task");
        var sent=new DateTime(2026,11,25,1,54,10,DateTimeKind.Local);
        string first="remind me at 01:54am on 25/11/2026 to drink water";
        string correction="same date but the time is 01:55am";
        Check(!Language.IsNewRequest(correction),"time correction stays in conversation");
        CheckReminder(At(sent,correction,Turn(first,At(sent,first))),"drink water",sent.Date.AddMinutes(115),"same date preserves the task");
        Check(At(sent,correction).Action=="ask","orphaned correction cannot become a task");
        CheckReminder(Read("July 2 at 2 pm",null,Turn(week,pick)),"call Mum",new DateTime(2027,7,2,14,0,0),"month reply inherits resolved year");
        var simple=Read("Remind me to stretch in 20 minutes");
        Check(simple.Action=="create" && simple.Title=="stretch" && simple.CheckUtc==Now.ToUniversalTime().AddMinutes(20),"relative seconds");
        const string waterRequest="pls remind to drink water on 2:13pm later";
        var beforeTime=Now.Date.AddHours(14).AddMinutes(12);
        var water=Language.Read(waterRequest,beforeTime,Items,null,null).GetAwaiter().GetResult();
        Check(water.Action=="create" && water.Title=="drink water" && water.CheckUtc==beforeTime.AddMinutes(1).ToUniversalTime(),"casual same-day wall-clock reminder");
        var tomorrow=Language.Read(waterRequest,beforeTime.AddMinutes(2),Items,null,null).GetAwaiter().GetResult();
        Check(tomorrow.CheckUtc==beforeTime.AddDays(1).AddMinutes(1).ToUniversalTime(),"a passed clock time uses its next occurrence");
        Check(Read("please remind me on 2:13 pm to drink water").Title=="drink water","clock before title");
        Check(Read("remind me to work on 2 slides in 10 minutes").Title=="work on 2 slides","a count after on is not a clock");
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
