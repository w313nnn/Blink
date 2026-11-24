using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public sealed partial class TaskStore {
    bool checklistNeedsRefresh;
    public string ChecklistPath { get { return Path.Combine(Path.GetDirectoryName(path),"Blink checklist.txt"); } }

    // The JSON is the record of truth. A locked text viewer must not roll back a saved task or reply.
    void TryRefreshChecklist() {
        try { RefreshChecklist(); }
        catch (IOException) { checklistNeedsRefresh = true; }
        catch (UnauthorizedAccessException) { checklistNeedsRefresh = true; }
    }
    public void RefreshChecklist() {
        lock (gate) {
            var output = new StringBuilder();
            output.AppendLine("Blink checklist");
            output.AppendLine("Updated by Blink. Make changes in your conversation with the pet.");
            output.AppendLine();
            var pending = new HashSet<string>(data.Inbox.Select(n => n.Id));
            var active = data.Items.Where(t => t.Stage != "done")
                .OrderBy(NextEvent).ThenBy(t => t.Title,StringComparer.CurrentCultureIgnoreCase).ToList();
            if (active.Count == 0) output.AppendLine("Your checklist is clear.");
            foreach (var task in active) {
                output.AppendLine("[ ] " + SingleLine(task.Title));
                AppendTime(output,"Check-in",task.CheckUtc);
                AppendTime(output,"Deadline",task.DeadlineUtc);
                AppendTime(output,"Heads-up",task.WarnUtc);
                if (task.Submission) output.AppendLine(task.Stage == "draft ready"
                    ? "    Draft ready. Submission still pending." : "    Submission pending.");
                if (!String.IsNullOrWhiteSpace(task.File)) output.AppendLine("    File: " + SingleLine(task.File));
                if (pending.Contains(task.Id)) output.AppendLine("    Blink has a message waiting for your reply.");
                output.AppendLine();
            }

            string destination = ChecklistPath;
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try {
                File.WriteAllText(temp,output.ToString(),new UTF8Encoding(true));
                if (File.Exists(destination)) File.Replace(temp,destination,null);
                else File.Move(temp,destination);
                checklistNeedsRefresh = false;
            } finally {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
    static DateTime NextEvent(TaskItem item) {
        return new[] {item.CheckUtc,item.DeadlineUtc,item.WarnUtc}.Where(t => t.HasValue)
            .Select(t => t.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
    }
    static string SingleLine(string text) { return text.Replace('\r',' ').Replace('\n',' '); }
    static void AppendTime(StringBuilder output,string label,DateTime? utc) {
        if (utc.HasValue) output.AppendLine("    " + label + ": " +
            utc.Value.ToLocalTime().ToString("ddd, dd MMM yyyy, h:mm tt",CultureInfo.CurrentCulture));
    }
}
