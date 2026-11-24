using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// The same palette supplies the pet coat and every conversation surface.
public sealed class PetTheme {
    public readonly Color Coat, Accent, Ink, Canvas, Incoming, Outgoing, Soft, Border, OnAccent;

    PetTheme(int coat, int accent, int ink) {
        Coat = Rgb(coat);
        Accent = Rgb(accent);
        Ink = Rgb(ink);
        Canvas = Tint(Coat, .91);
        Incoming = Tint(Coat, .66);
        Outgoing = Coat;
        Soft = Tint(Coat, .79);
        Border = Tint(Accent, .63);
        OnAccent = Color.White;
    }

    static Color Rgb(int hex) {
        return Color.FromArgb((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
    }
    static Color Tint(Color color, double white) {
        return Color.FromArgb((int)Math.Round(color.R + (255 - color.R) * white),
            (int)Math.Round(color.G + (255 - color.G) * white), (int)Math.Round(color.B + (255 - color.B) * white));
    }

    static readonly PetTheme Red = new PetTheme(0xF3A7A5, 0x923E45, 0x482326);
    static readonly PetTheme Yellow = new PetTheme(0xF6D77E, 0x755413, 0x403216);
    static readonly PetTheme Blue = new PetTheme(0xA5C8F3, 0x365E94, 0x22364F);
    static readonly PetTheme Purple = new PetTheme(0xC9B4EF, 0x664595, 0x392949);
    static readonly PetTheme Green = new PetTheme(0xA6DBB4, 0x396A4C, 0x243D2F);

    public static PetTheme For(string name) {
        switch (name) {
            case "Red": return Red;
            case "Yellow": return Yellow;
            case "Blue": case "Ocean": return Blue;
            case "Purple": case "Plum": return Purple;
            default: return Green;
        }
    }
}

public sealed class PetProfile {
    public const string DefaultName = "Blink";
    public static readonly string[] Personalities = { "Calm", "Playful", "Direct" };
    public static readonly string[] Appearances = { "Red", "Yellow", "Blue", "Purple", "Green" };
    public static readonly string[] Sizes = { "Small", "Medium" };

    public string Name { get; set; }
    public string Character { get; set; }
    public string Personality { get; set; }
    public string Appearance { get; set; }
    public string SizeChoice { get; set; }
    public int PetPixels {
        get { return SizeChoice == "Medium" ? 54 : 44; }
    }

    public PetProfile() {
        Name = DefaultName;
        Character = "Curious";
        Personality = "Calm";
        Appearance = "Green";
        SizeChoice = "Small";
    }
    public PetProfile Copy() { return (PetProfile)MemberwiseClone(); }

    public PetTheme GetTheme() { return PetTheme.For(Appearance); }
    public Color GetColor() { return GetTheme().Coat; }
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
            if (Personality == "Playful") return "Psst, it’s me! Can you text me back so I know this worked?";
            if (Personality == "Direct") return "Ten seconds are up. Did you get my message?";
            return "It’s been ten seconds. Did my message reach you?";
        }
        if (kind == "check-in") {
            if (Personality == "Playful") return "Hey, how’s “" + title + "” coming along? Still working on it?";
            if (Personality == "Direct") return "Checking in on “" + title + "”. How far did you get?";
            return "How’s “" + title + "” going? I said I’d check in with you.";
        }
        string due = title + (String.IsNullOrWhiteSpace(time) ? " is due." : " is due " + time + ".");
        if (Personality == "Direct") return due + (submission ? " Have you submitted it?" : " Is it done?");
        if (Personality == "Playful") return (kind == "warning" ? "A little heads-up! " : "Time check! ") + due + (submission ? " Did the upload go through?" : " How are you getting on?");
        return due + (submission ? " Did you manage to submit it?" : " How’s it going?");
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
        switch (Appearance) {
            case "Fern": Appearance = "Green"; break;
            case "Ocean": Appearance = "Blue"; break;
            case "Plum": Appearance = "Purple"; break;
        }
        if (!Personalities.Contains(Personality) || !Appearances.Contains(Appearance))
            throw new ArgumentException("Select a personality and appearance from the available choices.");
        if (!Sizes.Contains(SizeChoice)) throw new ArgumentException("Select a pet size from the available choices.");
    }
    public void Save(string dir) {
        Validate();
        Directory.CreateDirectory(dir);
        string path = ProfilePath(dir);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            var fields = new { Name, Character, Personality, Appearance, SizeChoice };
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(fields),new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp,path,null);
            else File.Move(temp,path);
        } finally {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
