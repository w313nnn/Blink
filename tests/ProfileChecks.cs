using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

class ProfileChecks {
    static void Check(bool ok, string note) { if(!ok) throw new Exception(note); Console.WriteLine("PASS " + note); }
    static bool Rejects(Action fn) { try { fn(); } catch(Exception) { return true; } return false; }
    static void Main(string[] args) {
        string root=Path.GetFullPath(args[0]);
        string first=Path.Combine(root,"first"),second=Path.Combine(root,"second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var defaults=PetProfile.Load(first);
        var changed=defaults.Copy();
        changed.Name="Pip"; changed.Personality="Playful"; changed.Appearance="Plum"; changed.Character="Gentle and fond of puns.";
        Check(defaults.Name!="Pip" && defaults.Character!=changed.Character,"edits use a detached copy");
        changed.Save(first);
        var read=PetProfile.Load(first);
        Check(read.Name=="Pip" && read.Personality=="Playful" && read.Appearance=="Plum" && read.Character==changed.Character,"name, voice, appearance and characteristic survive restart");
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
