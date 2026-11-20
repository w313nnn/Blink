using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

class OllamaChecks {
    static readonly DateTime clock = new DateTime(2030,11,20,8,0,0,DateTimeKind.Local);
    static readonly List<TaskItem> tasks = new List<TaskItem>();
    const string request = "Organize my literature review after lunch";
    static Intent Read(string query) { return Language.Read(query,clock,tasks,null,null).GetAwaiter().GetResult(); }
    static void Check(bool ok,string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Main() {
        using (var fake = new FakeOllamaTransport()) {
            fake.Attach();
            Check(Language.ModelStatus().GetAwaiter().GetResult().StartsWith("Local AI ready") ,"local model status");
            fake.Chat = "{\"action\":\"create\",\"title\":\"Literature review\",\"deadline\":\"2030-11-20 14:00:00\"}";
            var result = Read(request);
            Check(result.Action == "create" && result.Title=="Literature review" &&
                result.DeadlineUtc == clock.AddHours(6).ToUniversalTime(), "chat response becomes a timed task proposal");
            var body=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(fake.LastChatBody);
            Check(fake.ChatRequests==1 && (string)body["model"]=="blink-test:local" && (bool)body["stream"]==false
                && (string)body["format"]=="json" && fake.LastChatBody.Contains(request), "chat payload names local model and user request");
            var sub=new TaskItem {Title="Coursework",Submission=true}; tasks.Add(sub);
            fake.Chat="{\"action\":\"done\",\"id\":\""+sub.Id+"\"}";
            Check(Read(request).Action=="ask", "submission completion needs explicit confirmation");
            fake.Chat="{\"action\":\"edit\",\"id\":\"missing\",\"deadline\":\"2031-01-01 01:00:00\"}";
            Check(Read(request).Action=="ask", "unknown target rejected");
            fake.Chat="{\"action\":\"create\",\"title\":\"Study\",\"check\":\"2001-01-01 12:00:00\"}";
            Check(Read(request).Action=="ask", "past model time rejected");
            fake.Chat="model response that is not JSON";
            Check(Read(request).Action=="ask", "malformed model output handled");
            var pet=new PetProfile {Name="Pip",Character="Library bird",Personality="Playful"};
            fake.Chat="{\"action\":\"ask\",\"reply\":\"Do you mean Friday at 5 pm?\"}";
            fake.Style="{\"opening\":\"playful\",\"action\":\"submitted\",\"deadline\":\"2040-01-01 12:00:00\"}";
            var petReply=Language.Read(request,clock,tasks,null,null,pet).GetAwaiter().GetResult();
            Check(petReply.Reply=="Psst! Do you mean Friday at 5 pm?" && petReply.ContextReply=="Do you mean Friday at 5 pm?", "pet voice preserves the full canonical question");
            Check(fake.LastChatBody.Contains("Pip") && fake.LastChatBody.Contains("Library bird") && fake.LastChatBody.Contains("Playful") && !fake.LastInterpretation.Contains("Library bird"),"profile only reaches the style selection");
            Check(petReply.Action=="ask" && !petReply.DeadlineUtc.HasValue, "extra style fields cannot schedule or complete a task");
            int beforeRepeat=fake.ChatRequests;
            Language.Read(request,clock,tasks,null,null,pet).GetAwaiter().GetResult();
            Check(fake.ChatRequests==beforeRepeat+1, "unchanged profile needs no extra inference");
            pet.Personality="Direct";
            fake.Style="malformed";
            Check(Language.Read(request,clock,tasks,null,null,pet).GetAwaiter().GetResult().Reply=="Do you mean Friday at 5 pm?", "optional style failure preserves the original question");
            fake.Tags = "{\"models\":[{\"name\":\"cloud-model\"}]}";
            int prior=fake.ChatRequests;
            Check(Read(request).Action=="ask" && fake.ChatRequests==prior, "cloud-only inventory does not receive chat");
            fake.Offline=true;
            Check(Language.ModelStatus().GetAwaiter().GetResult().StartsWith("Offline reminders ready"), "unavailable server status");
            Check(Read(request).Action=="ask", "unavailable server guides user");
            var offline=Read("Remind me to stretch in 10 minutes");
            Check(offline.Action=="create" && offline.CheckUtc==clock.AddMinutes(10).ToUniversalTime(),"offline reminders still work");
        }
    }
}
