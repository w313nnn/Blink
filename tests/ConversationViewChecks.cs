using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class ConversationViewChecks {
    const BindingFlags Member=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(ReminderDot dot,string name) where T:class {
        return (T)typeof(ReminderDot).GetField(name,Member).GetValue(dot);
    }
    static void Invoke(ReminderDot dot,string name,params object[] args) {
        typeof(ReminderDot).GetMethod(name,Member).Invoke(dot,args);
    }
    static void Check(bool condition,string reason) { if (!condition) throw new Exception(reason); }

    [STAThread] static void Main(string[] args) {
        string folder=Path.Combine(args.Length==0?Path.GetTempPath():args[0],"chat-view-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var seed=new TaskStore.SavedData();
        seed.Items.Add(new TaskItem { Id="notes",Title="Lecture notes" });
        for (int i=0;i<44;i++) seed.History.Add(new ChatEntry {
            MessageId="exchange-"+(i/2),TaskId="notes",FromPet=i%2==1,
            Text="Message "+i,AtUtc=DateTime.UtcNow.AddMinutes(i-44)
        });
        File.WriteAllText(Path.Combine(folder,"tasks.json"),new JavaScriptSerializer().Serialize(seed));
        Application.EnableVisualStyles();
        using (var dot=new ReminderDot(folder)) {
            dot.Show(); Invoke(dot,"OpenInbox",new object[]{null});
            var view=Field<Panel>(dot,"messageThread");
            var selector=Field<ComboBox>(dot,"eventList");
            var reply=Field<TextBox>(dot,"replyBox");
            var close=Field<Button>(dot,"closeChat");
            view.AutoScrollPosition=new Point(0,800);
            var scroll=view.AutoScrollPosition;
            var controls=view.Controls.Cast<Control>().ToArray();
            Check(scroll.Y<0 && controls.Length>10,"History should be scrollable");
            Invoke(dot,"RenderNotice");
            Check(controls.SequenceEqual(view.Controls.Cast<Control>()) && controls.All(c=>!c.IsDisposed),"A status refresh replaced message controls");
            Check(view.AutoScrollPosition==scroll,"A status refresh moved the reader");
            var anchor=view.Controls.Cast<Control>().Where(c=>c.GetType().Name=="MessageBubble" && c.Top>=0).OrderBy(c=>c.Top).First();
            int top=anchor.Top;
            reply.Text="My draft"; Invoke(dot,"ShowUpcoming");
            Check(!anchor.IsDisposed && Math.Abs(anchor.Top-top)<=2,"New messages moved "+anchor.Text+" from "+top+" to "+anchor.Top);
            selector.SelectedIndex=1;
            Check(reply.TextLength==0,"Draft appeared in a different conversation");
            reply.Text="Notes draft"; selector.SelectedIndex=0;
            Check(reply.Text=="My draft","Changing threads discarded the draft");
            close.PerformClick(); Invoke(dot,"OpenInbox",new object[]{null});
            Check(reply.Text=="My draft","Reopening discarded the draft");
            selector.SelectedIndex=1;
            Check(reply.Text=="Notes draft","Second conversation lost its own draft");
            dot.Close();
        }
        Console.WriteLine("Conversation controls, scroll anchors and per-thread drafts passed");
    }
}
