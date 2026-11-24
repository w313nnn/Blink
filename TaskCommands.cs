using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

static class TaskCommands {
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Polite = @"^(?:(?:can|could|would) you\s+)?(?:(?:please|pls|plz)\s+)?";
    const string TaskWord = @"(?:task|to-do|todo|reminder)";
    static readonly Regex NewTask = new Regex(Polite + @"(?:add|create|make|track)\s+(?:(?:a|an|the|new|a new)\s+)?(?:task|to-do|todo)(?:\s+(?:to|for|called|named)\s+|\s*[:\-]\s*|\s+)(?<title>.+)$", Options);
    static readonly Regex ToList = new Regex(Polite + @"(?:add|put)\s+(?<title>.+?)\s+(?:to|on)\s+(?:my|the)\s+(?:to-do|todo|task)\s*list$", Options);
    static readonly Regex Rename = new Regex(Polite + @"rename\s+(?<target>.+?)\s+to\s+(?<title>.+)$", Options);
    static readonly Regex Cancel = new Regex(Polite + @"(?:cancel|remove|delete)\s+(?<target>.+)$", Options);

    static string Clean(string value) {
        return value.Trim().Trim('“','”','"').Trim();
    }
    public static string CaptureTitle(string request) {
        Match m = NewTask.Match(request);
        if (!m.Success) m = ToList.Match(request);
        return m.Success ? Clean(m.Groups["title"].Value) : null;
    }

    static string Key(string value) {
        return Regex.Replace(value,@"[^\p{L}\p{N}]+"," ").Trim();
    }
    static Intent Ask(string text) { return new Intent { Action="ask", Reply=text }; }
    static Intent ForTarget(Match command, bool renaming, TaskItem chosen) {
        string title = Clean(command.Groups["title"].Value);
        if (renaming && (title.Length == 0 || title.Length > 300))
            return Ask("What shorter title would you like to use?");
        return new Intent {Action=renaming ? "rename" : "cancel", Id=chosen.Id, Title=renaming ? title : null};
    }
    public static Intent ApplyToClarifiedTarget(string original, TaskItem chosen) {
        if (chosen == null || chosen.Stage == "done") return null;
        Match rename = Rename.Match(original);
        if (rename.Success) return ForTarget(rename,true,chosen);
        Match cancel = Cancel.Match(original);
        return cancel.Success ? ForTarget(cancel,false,chosen) : null;
    }

    static List<TaskItem> FindTargets(string target, List<TaskItem> tasks, TaskItem selected) {
        var active = tasks.Where(t => t.Stage != "done").ToList();
        target = Clean(target);
        if (Regex.IsMatch(target,@"^(?:it|this|that)(?:\s+"+TaskWord+@")?$",Options)) {
            if (selected != null) return active.Where(t => t.Id == selected.Id).ToList();
            return active;
        }
        string exactKey = Key(target);
        var exact = active.Where(t => String.Equals(Key(t.Title),exactKey,StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        target = Regex.Replace(target,@"^(?:(?:the|my)\s+)?(?:"+TaskWord+@"\s+(?:(?:called|named|to|for)\s+)?)?","",Options);
        target = Regex.Replace(target,@"\s+"+TaskWord+"$","",Options);
        string key = Key(target);
        if (key.Length == 0) return new List<TaskItem>();
        exact = active.Where(t => String.Equals(Key(t.Title),key,StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        string phrase = " " + key + " ";
        return active.Where(t => (" " + Key(t.Title) + " ").IndexOf(phrase,StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    public static Intent Parse(string request, List<TaskItem> tasks, TaskItem selected, bool hasTiming) {
        Match command = Rename.Match(request);
        bool renaming = command.Success;
        if (!renaming) command = Cancel.Match(request);
        if (command.Success) {
            string target = command.Groups["target"].Value;
            var matches = FindTargets(target,tasks,selected);
            if (matches.Count == 0) return Ask("I couldn’t find an active task matching “"+target+"”. What’s its title?");
            if (matches.Count > 1) {
                string choices = String.Join(", ",matches.Take(4).Select(t => "“"+t.Title+"”"));
                return Ask("Which task do you mean? I found "+choices+".");
            }
            return ForTarget(command,renaming,matches[0]);
        }
        string title = CaptureTitle(request);
        if (title == null || hasTiming) return null;
        if (title.Length == 0 || title.Length > 300) return Ask("What short title should I use for the task?");
        // A deadline or recurrence needs the timing parser, even when it used unfamiliar wording.
        if (Regex.IsMatch(title,@"\b(?:due|deadline|daily|weekly|monthly|every|tomorrow|tonight|today|next week|next month|at \d|by (?:\d|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|\d{1,2}:\d{2})\b",Options))
            return null;
        return new Intent {Action="create", Title=title};
    }
}
