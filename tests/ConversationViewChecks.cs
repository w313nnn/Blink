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

    static readonly string[] BubbleSamples = {
        "yes", "Got it.", "Got it.", "yes",
        "Please remind me to review the entire assignment and check all the references before I submit the final report tomorrow afternoon.",
        "Saved.\r\nSee you then."
    };
    static void CheckBubbles(Panel view) {
        var bubbles=view.Controls.Cast<Control>().Where(c=>c.GetType().Name=="MessageBubble")
            .OrderBy(c=>c.TabIndex).ToList();
        var samples=bubbles.Skip(bubbles.Count-BubbleSamples.Length).ToArray();
        Check(samples.Select(c=>c.Text).SequenceEqual(BubbleSamples),"The recent user and pet messages should be visible");
        for (int i=0;i<4;i++) {
            var bubble=samples[i];
            int ink=TextRenderer.MeasureText(bubble.Text,bubble.Font,new Size(Int32.MaxValue,Int32.MaxValue),
                TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding).Width;
            int content=bubble.ClientSize.Width-bubble.Padding.Horizontal;
            Check(bubble.Padding.Left>0 && bubble.Padding.Right>0 && content>=ink-1 &&
                content<=ink+bubble.Font.Height,"Excess width or clipped text for “"+bubble.Text+"”: "+bubble.Width+" pixels");
        }
        Check(samples[0].Width<samples[2].Width && samples[1].Width>samples[3].Width &&
            samples[0].Width==samples[3].Width && samples[1].Width==samples[2].Width,
            "Both sides should size a short reply to its own text");
        Check(samples[1].Left<view.ClientSize.Width/4 && samples[1].Left==samples[3].Left &&
            samples[3].Left==samples[5].Left && samples[0].Right>view.ClientSize.Width*3/4 &&
            samples[0].Right==samples[2].Right && samples[2].Right==samples[4].Right,
            "Pet messages should share a left edge and user messages should share a right edge");
        for (int i=4;i<samples.Length;i++) {
            var bubble=samples[i];
            var content=new Size(bubble.ClientSize.Width-bubble.Padding.Horizontal,Int32.MaxValue);
            int textHeight=TextRenderer.MeasureText(bubble.Text,bubble.Font,content,
                TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix).Height;
            Check(bubble.Left>=0 && bubble.Right<=view.ClientSize.Width &&
                bubble.Height>samples[0].Height && bubble.ClientSize.Height-bubble.Padding.Vertical>=textHeight,
                "Long and multiline text must fit inside the wrapped bubble");
        }
    }
    [STAThread] static void Main(string[] args) {
        string folder=Path.Combine(args.Length==0?Path.GetTempPath():args[0],"chat-view-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // This scroll test needs all 44 recent messages in a single period, even just after midnight on the first.
        new PetProfile { HistoryStartDay=DateTime.Today.Day<=15 ? 16 : 1 }.Save(folder);
        var seed=new TaskStore.SavedData();
        seed.Items.Add(new TaskItem { Id="notes",Title="Lecture notes" });
        for (int i=0;i<44;i++) seed.History.Add(new ChatEntry {
            MessageId="exchange-"+(i/2),TaskId="notes",FromPet=i%2==1,
            Text=i>=44-BubbleSamples.Length ? BubbleSamples[i-(44-BubbleSamples.Length)] : "Message "+i,
            AtUtc=DateTime.UtcNow.AddMinutes(i-44)
        });
        File.WriteAllText(Path.Combine(folder,"tasks.json"),new JavaScriptSerializer().Serialize(seed));
        Application.EnableVisualStyles();
        using (var dot=new ReminderDot(folder)) {
            dot.Show(); Invoke(dot,"OpenInbox",new object[]{null});
            var view=Field<Panel>(dot,"messageThread");

            var reply=Field<TextBox>(dot,"replyBox");
            var close=Field<Button>(dot,"closeChat");
            CheckBubbles(view);
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
            Check(reply.Text=="My draft","Refreshing messages discarded the draft");
            close.PerformClick(); Invoke(dot,"OpenInbox",new object[]{null});
            Check(reply.Text=="My draft","Reopening discarded the draft");
            dot.Close();
        }
        Console.WriteLine("Bubble sizing, alignment, wrapping, scroll anchors and per-thread drafts passed");
    }
}
