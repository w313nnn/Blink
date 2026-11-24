using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class PetInteractionChecks {
    // The drag move uses WinForms' normal event entry point so the test does not
    // need a physical mouse button held down. Down/up use real window messages.
    sealed class Pet : ReminderDot {
        public Pet(string directory) : base(directory) { }
        protected override bool ShowWithoutActivation { get { return true; } }
        public void EnterPet() { OnMouseEnter(EventArgs.Empty); }
        public void LeavePet() { OnMouseLeave(EventArgs.Empty); }
        public void DragTo(int x,int y) { OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,x,y,0)); }
    }
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    const BindingFlags Member = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool pass,string label) {
        if(!pass) throw new Exception(label);
        checks++;
    }
    static void MouseButton(Pet pet,bool down) {
        SendMessage(pet.Handle, down ? 0x0201 : 0x0202, new IntPtr(down ? 1 : 0), new IntPtr((12<<16)|12));
    }
    static EyeExpression Face(Pet pet) {
        return (EyeExpression)typeof(ReminderDot).GetMethod("DesktopExpression",Member).Invoke(pet,null);
    }

    [STAThread] static void Main(string[] args) {
        string path=Path.Combine(args.Length==0 ? Path.GetTempPath() : args[0],"pet-input-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        new PetProfile { Character="Sleepy" }.Save(path);
        Application.EnableVisualStyles();
        using(var pet=new Pet(path)) {
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            pet.TopMost=false;
            pet.Location=new Point(area.Left+area.Width/3,area.Top+area.Height/3);
            pet.Show();
            var notice=(Form)typeof(ReminderDot).GetField("notice",Member).GetValue(pet);
            pet.LeavePet();
            Check(Face(pet)==EyeExpression.Exhausted,"idle character's contextual eyes");
            pet.EnterPet();
            Check(Face(pet)==EyeExpression.Happy,"pointer enters the pet");
            MouseButton(pet,true);
            Check(pet.Capture && Face(pet)==EyeExpression.Surprised,"pickup changes expression immediately");
            var start=pet.Location;
            pet.DragTo(38,36);
            Check(pet.Location.X>start.X && pet.Location.Y>start.Y && Face(pet)==EyeExpression.Surprised,"drag moves the surprised pet");
            pet.LeavePet();
            Check(Face(pet)==EyeExpression.Surprised,"held pet stays surprised beyond its original hover area");
            Check(!notice.Visible,"drag leaves conversation closed");
            MouseButton(pet,false);
            Check(!pet.Capture && Face(pet)==EyeExpression.Happy && !notice.Visible,"release over pet settles to happy without opening chat");
            pet.LeavePet();
            Check(Face(pet)==EyeExpression.Exhausted,"leaving restores contextual eyes");
            pet.EnterPet();
            MouseButton(pet,true);
            MouseButton(pet,false);
            Check(notice.Visible,"click opens the conversation");
            MouseButton(pet,true);
            MouseButton(pet,false);
            Check(!notice.Visible,"second click closes the conversation");
            MouseButton(pet,true);
            ReleaseCapture();
            Check(!pet.Capture && Face(pet)!=EyeExpression.Surprised,"lost capture settles the pet");
            var afterCaptureLoss=pet.Location;
            pet.DragTo(42,38);
            MouseButton(pet,false);
            Check(!notice.Visible && pet.Location==afterCaptureLoss,"late drag and release after capture loss are ignored");
            pet.Close();
        }
        Console.WriteLine(checks+" desktop pet interaction checks passed");
    }
}
