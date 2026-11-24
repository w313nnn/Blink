using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Web.Script.Serialization;

class ProfileChecks {
    static void Check(bool ok, string note) { if(!ok) throw new Exception(note); Console.WriteLine("PASS " + note); }
    static bool Rejects(Action fn) { try { fn(); } catch(Exception) { return true; } return false; }
    static double LinearChannel(byte c) {
        double v=c/255.0;
        return v<=0.04045 ? v/12.92 : Math.Pow((v+0.055)/1.055,2.4);
    }
    static double Luminance(Color color) {
        return 0.2126*LinearChannel(color.R)+0.7152*LinearChannel(color.G)+0.0722*LinearChannel(color.B);
    }
    static double Contrast(Color a,Color b) {
        double x=Luminance(a),y=Luminance(b);
        return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05);
    }
    static void CheckThemes(string root) {
        var rainbow = new[]{"Red","Orange","Yellow","Green","Blue","Purple"};
        Check(PetProfile.Appearances.SequenceEqual(rainbow) &&
            new PetProfile().Appearance=="Green","six colors in rainbow order and a green default");
        var coats=new HashSet<int>();
        var outgoingColors=new HashSet<int>();
        foreach(string name in PetProfile.Appearances) {
            string dir=Path.Combine(root,"palette-"+name);
            var profile=new PetProfile { Appearance=name, Name="Pip" };
            profile.Save(dir);
            var reopened=PetProfile.Load(dir);
            var theme=reopened.GetTheme();
            Check(reopened.Appearance==name && reopened.GetColor().ToArgb()==theme.Coat.ToArgb(),name+" coat survives reload");
            Check(Contrast(theme.Ink,theme.Canvas)>=4.5 && Contrast(theme.Ink,theme.Soft)>=4.5 && Contrast(theme.Ink,theme.Incoming)>=4.5 &&
                Contrast(theme.Ink,theme.Outgoing)>=4.5 && Contrast(theme.OnAccent,theme.Accent)>=4.5,
                name+" has readable chat text and button labels");
            coats.Add(theme.Coat.ToArgb());outgoingColors.Add(theme.Outgoing.ToArgb());
        }
        Check(coats.Count==rainbow.Length && outgoingColors.Count==rainbow.Length,"distinct coats and chat palettes for every rainbow color");
        foreach(var pair in new Dictionary<string,string>{{"Fern","Green"},{"Ocean","Blue"},{"Plum","Purple"}}) {
            string dir=Path.Combine(root,"legacy-"+pair.Key);
            Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,"pet-profile.json");
            var legacy=new {Name="Pip",Appearance=pair.Key,Character="Sleepy",Personality="Playful",SizeChoice="Medium"};
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(legacy));
            var old=PetProfile.Load(dir);
            Check(old.Name=="Pip" && old.Character=="Sleepy" && old.Personality=="Playful" && old.SizeChoice=="Medium" &&
                  old.Appearance==pair.Value,"legacy "+pair.Key+" migrates to "+pair.Value+" with identity retained");
            old.Save(dir);
            Check(PetProfile.Load(dir).Appearance==pair.Value,"migrated "+pair.Value+" persists");
        }
    }
    static DateTime Local(int year,int month,int day) {
        return new DateTime(year,month,day,0,0,0,DateTimeKind.Local);
    }
    static void CheckHistoryWindow(PetProfile profile,DateTime at,DateTime start,DateTime end,string note) {
        var window=profile.HistoryAt(at);
        Check(window.Start==start && window.End==end && window.Start.Kind==DateTimeKind.Local &&
            window.End.Kind==DateTimeKind.Local,note);
        Check(profile.HistoryAt(start).Start==start && profile.HistoryAt(end.AddTicks(-1)).Start==start &&
            profile.HistoryAt(end).Start==end,"start is inclusive and end is exclusive for "+note);
    }
    static void CheckHistory(string root) {
        string dir=Path.Combine(root,"history-settings");
        var profile=PetProfile.Load(dir);
        Check(PetProfile.HistoryPeriods.SequenceEqual(new[]{"Monthly","6 months","Yearly"}) &&
            profile.HistoryPeriod=="Monthly" && profile.HistoryStartDay==1,"new profile defaults to calendar months");
        CheckHistoryWindow(profile,Local(2026,12,31),Local(2026,12,1),Local(2027,1,1),"default month across year end");

        Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"pet-profile.json");
        const string petFields="\"Name\":\"Luna\",\"Appearance\":\"Orange\",\"SizeChoice\":\"Medium\",\"Personality\":\"Playful\"";
        File.WriteAllText(path,"{"+petFields+"}");
        profile=PetProfile.Load(dir);
        Check(profile.HistoryPeriod=="Monthly" && profile.HistoryStartDay==1 && profile.Name=="Luna","old profile gains history defaults");
        File.WriteAllText(path,"{"+petFields+",\"HistoryPeriod\":\"Yearly\",\"HistoryStartDay\":\"invalid\"}");
        profile=PetProfile.Load(dir);
        Check(profile.HistoryPeriod=="Yearly" && profile.HistoryStartDay==1 && profile.Appearance=="Orange" &&
            profile.SizeChoice=="Medium" && profile.Personality=="Playful","damaged start day preserves period and pet settings");
        File.WriteAllText(path,"{"+petFields+",\"HistoryPeriod\":\"Weekly\",\"HistoryStartDay\":31}");
        profile=PetProfile.Load(dir);
        Check(profile.HistoryPeriod=="Monthly" && profile.HistoryStartDay==31,"unknown period preserves a valid start day");
        File.WriteAllText(path,"{"+petFields+",\"HistoryStartDay\":32}");
        Check(PetProfile.Load(dir).HistoryStartDay==1,"out of range start day falls back to the first");

        CheckHistoryWindow(profile,Local(2024,2,28),Local(2024,1,31),Local(2024,2,29),"leap year before clamped day 31");
        CheckHistoryWindow(profile,Local(2025,2,28),Local(2025,2,28),Local(2025,3,31),"short month restores the chosen day in March");
        var april=profile.HistoryAt(Local(2025,1,31)).Shift(3);
        Check(april.Start==Local(2025,4,30) && april.End==Local(2025,5,31) &&
            april.Shift(-3).Start==Local(2025,1,31),"calendar navigation retains its original start day");
        profile.HistoryPeriod="6 months"; profile.HistoryStartDay=12;
        CheckHistoryWindow(profile,Local(2026,7,11),Local(2026,1,12),Local(2026,7,12),"January half-year");
        CheckHistoryWindow(profile,Local(2026,7,12).ToUniversalTime(),Local(2026,7,12),Local(2027,1,12),"July half-year at local midnight");
        profile.HistoryPeriod="Yearly";
        CheckHistoryWindow(profile,Local(2026,1,11),Local(2025,1,12),Local(2026,1,12),"yearly before chosen January day");
        profile.Save(dir);
        var saved=PetProfile.Load(dir);
        Check(saved.HistoryPeriod=="Yearly" && saved.HistoryStartDay==12 && saved.Name=="Luna","history settings persist with pet identity");
    }
    static void Main(string[] args) {
        string root=Path.GetFullPath(args[0]);
        CheckThemes(root);
        CheckHistory(root);
        string first=Path.Combine(root,"first"),second=Path.Combine(root,"second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var defaults=PetProfile.Load(first);
        var changed=defaults.Copy();
        changed.Name="Pip"; changed.Personality="Playful"; changed.Appearance="Purple"; changed.Character="Gentle and fond of puns.";
        Check(defaults.Name!="Pip" && defaults.Character!=changed.Character,"edits use a detached copy");
        changed.Save(first);
        var read=PetProfile.Load(first);
        Check(read.Name=="Pip" && read.Personality=="Playful" && read.Appearance=="Purple" && read.Character==changed.Character,"name, voice, appearance and characteristic survive restart");
        Check(PetProfile.Load(second).Name==defaults.Name,"separate profiles for distinct data directories");
        foreach(var kind in new[]{"check-in","warning","deadline","test"}) {
            var voices=new List<string>();
            foreach(var personality in new[]{"Calm","Playful","Direct"}) {
                read.Personality=personality;
                voices.Add(read.Voice(kind,"Network lab","12:30",true));
            }
            Check(voices.All(t=>!String.IsNullOrWhiteSpace(t)) && voices.Distinct().Count()==3,kind+" has a distinct nudge for each personality");
        }
        foreach(Action<PetProfile> edit in new Action<PetProfile>[] { p=>p.Name="  ",p=>p.Name=new string('x',33),p=>p.Character=new string('y',161),p=>p.Appearance="Neon",p=>p.Personality="Ghost" }) {
            var bad=changed.Copy(); edit(bad);
            Check(Rejects(()=>bad.Save(first)) && PetProfile.Load(first).Name=="Pip","invalid edit rejected; saved pet remains available");
        }
        var ready=changed.Copy(); ready.Name="Blinky";
        string file=Directory.GetFiles(first,"*.json").Single();
        using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
            Check(Rejects(()=>ready.Save(first)),"save reports failure on locked destination");
        Check(PetProfile.Load(first).Name=="Pip","failed save preserves prior pet");
    }
}
