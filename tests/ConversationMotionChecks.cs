using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

class ConversationMotionChecks {
    sealed class Host : Form {
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    static int checks;
    static void Check(bool passed, string label) {
        if (!passed) throw new Exception(label);
        checks++;
    }
    static void Pump(int milliseconds) {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) {
            Application.DoEvents();
            Thread.Sleep(8);
        }
        Application.DoEvents();
    }
    static bool PumpUntil(Func<bool> completed,int timeoutMilliseconds) {
        var elapsed=Stopwatch.StartNew();
        while (!completed() && elapsed.ElapsedMilliseconds<timeoutMilliseconds) {
            Application.DoEvents();
            Thread.Sleep(4);
        }
        Application.DoEvents();
        return completed();
    }
    static bool TimerActive(ConversationMotion pet) {
        var field = typeof(ConversationMotion).GetField("timer",BindingFlags.Instance | BindingFlags.NonPublic);
        return ((System.Windows.Forms.Timer)field.GetValue(pet)).Enabled;
    }
    static long Frame(ConversationMotion pet) {
        using (var bitmap = new Bitmap(pet.Width,pet.Height)) {
            pet.DrawToBitmap(bitmap,pet.ClientRectangle);
            long hash = 0;
            var colors = new HashSet<int>();
            for (int y=0; y<bitmap.Height; y+=2) {
                for (int x=0; x<bitmap.Width; x+=2) {
                    int pixel = bitmap.GetPixel(x,y).ToArgb();
                    colors.Add(pixel);
                    hash = unchecked(hash*31+pixel);
                }
            }
            if (colors.Count < 24) throw new Exception("Rendered pet lost its shaded artwork");
            return hash;
        }
    }
    static bool Centered(Control pet, Control anchor) {
        return Math.Abs(pet.Left+pet.Width/2-anchor.Left-anchor.ClientSize.Width/2)<=1 &&
               Math.Abs(pet.Top+pet.Height/2-anchor.Top-anchor.ClientSize.Height/2)<=1;
    }
    static void CheckCoatFade() {
        using (var start=PetArtwork.Render(104,PetTheme.For("Red").Coat,false,false,false,false,13))
        using (var end=PetArtwork.Render(104,PetTheme.For("Blue").Coat,false,false,false,false,13))
        using (var fade=new PetArtwork.CoatBlend()) {
            fade.ReadStart(start);
            Bitmap half=fade.MixEnd(end,.5);
            bool softEdge=false,solidEye=false;
            for (int y=0; y<half.Height; y++) for (int x=0; x<half.Width; x++) {
                Color a=start.GetPixel(x,y),b=end.GetPixel(x,y),m=half.GetPixel(x,y);
                if (m.A!=a.A) throw new Exception("Theme blend altered silhouette alpha");
                if (a.A>0 && a.A<255) softEdge=true;
                if (a.A==255 && b.A==255) {
                    if (Math.Abs(m.R-(a.R+b.R)/2)>1 || Math.Abs(m.G-(a.G+b.G)/2)>1 || Math.Abs(m.B-(a.B+b.B)/2)>1)
                        throw new Exception("Theme blend altered color interpolation");
                    if (m.R<75 && m.G<75 && m.B<75) solidEye=true;
                }
            }
            Check(softEdge && solidEye,"theme fade keeps antialiased edges and solid eyes");
            fade.ReadStart(end);
            Check(Object.ReferenceEquals(half,fade.MixEnd(start,.25)),"overlapping fades reuse the same size buffer");
        }
    }
    static void Run(bool animate) {
        using (var form = new Host { ClientSize = new Size(460,270), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-10000,-10000) }) {
            var thread = new Panel { Bounds = new Rectangle(17,20,375,150) };
            var input = new TextBox { Bounds = new Rectangle(10,185,340,30) };
            form.Controls.Add(thread);
            form.Controls.Add(input);
            using (var pet = new ConversationMotion(form,thread,() => animate)) {
                var red=PetTheme.For("Red");
                var green=PetTheme.For("Green");
                form.Show();
                form.ActiveControl=input;
                Check(!pet.Visible && !TimerActive(pet),"idle overlay");
                var first=pet.BeginBusy(red,13);
                var second=pet.BeginBusy(red,13);
                Check(pet.Visible && !pet.CanSelect && form.ActiveControl==input,"composer keeps focus");
                Check(Centered(pet,thread),"initial center");
                Check(TimerActive(pet)==animate,"busy timer follows animation preference");
                var frames=new HashSet<long>();
                for (int i=0; i<6; i++) { frames.Add(Frame(pet)); Pump(70); }
                Check(animate ? frames.Count>=4 : frames.Count==1,"motion and static rendering");
                first.Dispose();
                first.Dispose();
                Check(pet.Visible,"nested busy scopes");
                form.Hide();
                Pump(40);
                Check(!pet.Visible && !TimerActive(pet),"hidden host pauses");
                form.Show();
                Pump(25);
                Check(pet.Visible && TimerActive(pet)==animate,"pending operation resumes");
                thread.SetBounds(25,30,210,99);
                Check(Centered(pet,thread),"resize center");
                pet.Transition(red,green,13);
                second.Dispose();
                Check(pet.Visible,"theme change survives end of busy work");
                long firstThemeFrame=Frame(pet);
                Pump(150);
                Check(animate ? Frame(pet)!=firstThemeFrame : Frame(pet)==firstThemeFrame,"theme transition respects motion preference");
                if (!animate && pet.Visible) {
                    // Dispatch an early tick, as Windows can do for a reduced-motion preview.
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    var timer=(System.Windows.Forms.Timer)typeof(ConversationMotion).GetField("timer",flags).GetValue(pet);
                    int firstInterval=timer.Interval;
                    typeof(ConversationMotion).GetMethod("Advance",flags).Invoke(pet,new object[]{timer,EventArgs.Empty});
                    Check(!pet.Visible || timer.Interval<firstInterval,"early static timer uses the remaining time");
                }
                Check(PumpUntil(() => !pet.Visible && !TimerActive(pet),3000),
                    "final pose settles and timer stops (animations " + (animate ? "enabled" : "disabled") + ")");
                var late=pet.BeginBusy(green,13);
                form.Close();
                late.Dispose();
                Check(pet.IsDisposed,"late completion after close");
            }
        }
    }
    [STAThread] static void Main() {
        Application.EnableVisualStyles();
        CheckCoatFade();
        Run(true);
        Run(false);
        Console.WriteLine(checks+" conversation motion checks passed");
    }
}
