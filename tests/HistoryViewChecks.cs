using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class HistoryViewChecks {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(ReminderDot pet,string name) {
        return (T)typeof(ReminderDot).GetField(name,Private).GetValue(pet);
    }
    static void Call(ReminderDot pet,string name,params object[] args) {
        typeof(ReminderDot).GetMethod(name,Private).Invoke(pet,args);
    }
    static void Check(bool ok,string reason) { if (!ok) throw new Exception(reason); }
    static ChatEntry Entry(string id,string task,string text,DateTime local) {
        return new ChatEntry {MessageId=id,TaskId=task,Text=text,FromPet=true,AtUtc=local.ToUniversalTime()};
    }
    static string[] Bubbles(ReminderDot pet) {
        return Field<Panel>(pet,"messageThread").Controls.Cast<Control>()
            .Where(c=>c.GetType().Name=="MessageBubble").Select(c=>c.Text).ToArray();
    }
    static void Click(ReminderDot pet,string field) {
        var control=Field<Button>(pet,field);
        Check(control.Visible && control.Enabled,field+" should be available");
        control.PerformClick();
    }
    [STAThread] static void Main(string[] args) {
        string folder=Path.Combine(args.Length==0 ? Path.GetTempPath() : args[0],"history-view-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var profile=new PetProfile {HistoryStartDay=6}; profile.Save(folder);
        var current=profile.HistoryAt(DateTime.Now);
        var previous=current.Shift(-1);
        var seed=new TaskStore.SavedData();
        seed.Items.Add(new TaskItem {Id="lab",Title="Lab"});
        seed.Items.Add(new TaskItem {Id="old",Title="Old only"});
        seed.History.Add(Entry("old-only","old","Prior task",previous.Start));
        seed.History.Add(Entry("unanswered","lab","Original reminder",previous.Start.AddMinutes(1)));
        seed.Inbox.Add(new Nudge {Id="lab",MessageId="unanswered",Title="Lab",Text="Original reminder",
            Kind="check-in",SentUtc=previous.Start.AddMinutes(1).ToUniversalTime()});
        for(int i=0;i<81;i++) seed.History.Add(Entry("before-"+i,"lab","Previous "+i,previous.Start.AddMinutes(i+2)));
        for(int i=0;i<45;i++) seed.History.Add(Entry("current-"+i,"lab","Current "+i,current.Start.AddMinutes(i)));
        seed.History.Add(Entry("future","lab","Next boundary",current.End));
        // Deliberately out of insertion order; the date and time determine the period and page.
        seed.History.Add(Entry("before-last","lab","Previous end",current.Start.AddSeconds(-1)));
        File.WriteAllText(Path.Combine(folder,"tasks.json"),new JavaScriptSerializer().Serialize(seed));
        Application.EnableVisualStyles();
        using(var pet=new ReminderDot(folder)) {
            pet.Show(); Call(pet,"OpenInbox",new object[]{null});
            var store=Field<TaskStore>(pet,"store");
            var selector=Field<ComboBox>(pet,"eventList");
            var reply=Field<TextBox>(pet,"replyBox");
            var notice=Field<Form>(pet,"notice");
            var range=Field<Button>(pet,"historyRange");
            var view=Field<Panel>(pet,"messageThread");
            Check(Bubbles(pet).Length==40 && Bubbles(pet).All(s=>s.StartsWith("Current ")) &&
                Bubbles(pet).Contains("Current 44"),"Current page leaked messages or lost its newest entry");
            Check(selector.Items.Cast<object>().All(o=>!o.ToString().Contains("Old only")),"Another period leaked into the thread list");
            Check(!Field<Button>(pet,"nextPeriod").Enabled,"Next period should stop at the current interval");
            Click(pet,"olderMessages");
            Check(Bubbles(pet).Length==5 && Bubbles(pet).Contains("Current 0"),"Inclusive start or the older page is wrong");
            Click(pet,"newerMessages");
            Check(Bubbles(pet).Length==40 && view.Controls.Count==80,"Paging should bound native controls");
            reply.Text="Draft"; string liveRange=range.Text;
            Click(pet,"previousPeriod");
            Check(range.Text!=liveRange && Bubbles(pet).Contains("Previous end") && !Bubbles(pet).Any(s=>s.StartsWith("Current ")),
                "Previous period should end immediately before the current one");
            Check(reply.Text=="Draft" && store.Inbox.Count==1,"Browsing discarded the draft or unread reminder");
            Click(pet,"previousPeriod");
            var empty=Field<Label>(pet,"emptyHistory");
            Check(Bubbles(pet).Length==0 && empty.Visible && empty.Text=="No messages in this period." &&
                notice.Controls.GetChildIndex(empty)<notice.Controls.GetChildIndex(view),"Historical empty state is missing or obscured");
            reply.Clear(); string emptyRange=range.Text;
            Click(pet,"closeChat"); Call(pet,"OpenInbox",new object[]{null});
            Check(range.Text==emptyRange && Bubbles(pet).Length==0,"Reopen displaced a historical reader");
            Call(pet,"OpenInbox","unanswered");
            Check(Bubbles(pet).Contains("Original reminder") && store.Inbox.Count==1,
                "Addressing an unread reminder should open its older page without answering it");
            selector.SelectedItem=selector.Items.Cast<object>().First(o=>o.ToString()=="All messages");
            Call(pet,"ShowUpcoming");
            Check(range.Text==liveRange && store.Inbox.Count==1 && Bubbles(pet).Contains("Show my upcoming reminders"),
                "A new chat must appear in the current period and leave unrelated reminders unanswered");
            store.CommitChat("unanswered","lab","Change when you remind me","What time today?",null,
                new ChatPrompt {Context="What time today?",SelectedTaskId="lab"});
            Call(pet,"SyncInbox","unanswered");
            Check(selector.SelectedItem.ToString().Contains("Awaiting reply") && Field<Button>(pet,"finish").Visible &&
                !Bubbles(pet).Contains("Original reminder") && Bubbles(pet).Contains("What time today?"),
                "A reminder continued across periods lost its pending state or leaked old messages");
            var liveProfile=Field<PetProfile>(pet,"profile");
            foreach (string span in new[]{"6 months","Yearly"}) {
                liveProfile.HistoryPeriod=span; liveProfile.HistoryStartDay=5;
                Call(pet,"ResetHistoryView");
                var expected=liveProfile.HistoryAt(DateTime.Now);
                Check(Field<DateTime>(pet,"periodStart")==expected.Start && Field<DateTime>(pet,"periodEnd")==expected.End &&
                    store.Inbox.Count==1,"Settings did not apply the new period or altered the inbox");
            }
            pet.Close();
        }
        Console.WriteLine("Conversation periods, date boundaries, paging, drafts and unread navigation passed");
    }
}
