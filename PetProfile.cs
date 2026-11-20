using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public sealed class PetProfile {
    public const string DefaultName = "Blink";
    public static readonly string[] Personalities = { "Calm", "Playful", "Direct" };
    public static readonly string[] Appearances = { "Fern", "Ocean", "Plum" };

    public string Name { get; set; }
    public string Character { get; set; }
    public string Personality { get; set; }
    public string Appearance { get; set; }

    public PetProfile() {
        Name = DefaultName;
        Character = "Curious";
        Personality = "Calm";
        Appearance = "Fern";
    }
    public PetProfile Copy() { return (PetProfile)MemberwiseClone(); }

    public Color GetColor() {
        if (Appearance == "Ocean") return Color.FromArgb(34,84,121);
        if (Appearance == "Plum") return Color.FromArgb(103,62,109);
        return Color.FromArgb(20,91,79);
    }
    public int BlinkIntervalMs {
        get {
            if (String.Equals(Character,"Sleepy",StringComparison.OrdinalIgnoreCase)) return 3300;
            if (String.Equals(Character,"Energetic",StringComparison.OrdinalIgnoreCase)) return 2400;
            return 5000;
        }
    }
    public int EyeHeight {
        get {
            if (String.Equals(Character,"Sleepy",StringComparison.OrdinalIgnoreCase)) return 7;
            if (String.Equals(Character,"Energetic",StringComparison.OrdinalIgnoreCase)) return 15;
            return 13;
        }
    }
    public string Greeting() {
        if (Personality == "Playful") return "Hi, I’m " + Name + "! What are we working on together?";
        if (Personality == "Direct") return Name + " here. Give me a task and a time.";
        return "I’m " + Name + ". What would you like me to remember?";
    }
    public string Voice(string kind, string title, string time, bool submission) {
        if (kind == "test") {
            if (Personality == "Playful") return "Peekaboo! Your ten-second test reminder is here.";
            if (Personality == "Direct") return "Ten seconds elapsed. Your test reminder is ready.";
            return "Here’s your ten-second test reminder.";
        }
        if (kind == "check-in") {
            if (Personality == "Playful") return "Quick progress peek! How’s “" + title + "” coming along?";
            if (Personality == "Direct") return "Progress check: " + title + ". Where does it stand?";
            return "How’s “" + title + "” going?";
        }
        string due = title + (String.IsNullOrWhiteSpace(time) ? " is due." : " is due " + time + ".");
        if (Personality == "Direct") return due + (submission ? " Confirm when submitted." : "");
        if (Personality == "Playful") return (kind == "warning" ? "A little heads-up! " : "Time check! ") + due + (submission ? " Did the upload go through?" : "");
        return due + (submission ? " Have you submitted it?" : "");
    }

    static string ProfilePath(string dir) { return Path.Combine(dir,"pet-profile.json"); }
    public static PetProfile Load(string dir) {
        string path = ProfilePath(dir);
        if (!File.Exists(path)) return new PetProfile();
        var profile = new JavaScriptSerializer().Deserialize<PetProfile>(File.ReadAllText(path));
        if (profile == null) throw new InvalidDataException("The pet profile is empty.");
        profile.Validate();
        return profile;
    }
    void Validate() {
        Name = (Name ?? "").Trim();
        Character = (Character ?? "").Trim();
        if (Name.Length < 1 || Name.Length > 32 || Name.Any(Char.IsControl))
            throw new ArgumentException("Enter a pet name between 1 and 32 characters on one line.");
        if (Character.Length > 160 || Character.Any(Char.IsControl))
            throw new ArgumentException("Describe the character in up to 160 characters on one line.");
        if (!Personalities.Contains(Personality) || !Appearances.Contains(Appearance))
            throw new ArgumentException("Select a personality and appearance from the available choices.");
    }
    public void Save(string dir) {
        Validate();
        Directory.CreateDirectory(dir);
        string path = ProfilePath(dir);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            var fields = new { Name, Character, Personality, Appearance };
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(fields),new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp,path,null);
            else File.Move(temp,path);
        } finally {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
