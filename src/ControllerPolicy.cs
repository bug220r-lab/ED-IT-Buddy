using System;
using System.Collections.Generic;
using System.Linq;

namespace EditBuddy
{
    // Pure policy: tests can exercise targeting without reading or controlling Windows.
    internal static class ActionPolicy
    {
        internal static bool IsSupported(string action)
        {
            return action == "focus_magix" || action == "inspect_controls";
        }
    }

    internal sealed class WindowRecord
    {
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public string ClassName { get; set; }
        public bool Visible { get; set; }
        public bool Enabled { get; set; }
        public long Owner { get; set; }
        public long RootOwner { get; set; }
    }

    internal sealed class WindowDecision
    {
        public bool Connected { get; set; }
        public bool Blocked { get; set; }
        public long Handle { get; set; }
        public string Reason { get; set; }
    }

    internal static class EditorWindowPolicy
    {
        internal const string EditorClass = "MainWindow_Videodeluxe";

        internal static WindowDecision Select(IEnumerable<WindowRecord> windows, int expectedProcess)
        {
            if (expectedProcess <= 0) return new WindowDecision { Reason = "No valid MAGIX process was selected." };
            List<WindowRecord> candidates = (windows ?? Enumerable.Empty<WindowRecord>()).Where(w =>
                w != null && w.Handle != 0 && w.ProcessId == expectedProcess && w.Visible &&
                String.Equals(w.ClassName, EditorClass, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0)
                return new WindowDecision { Reason = "MAGIX is starting, but its editor window is not ready. Finish any setup and refresh." };
            if (candidates.Count != 1)
                return new WindowDecision { Blocked = true, Reason = "More than one MAGIX editor window was found. Close the extra editor window before connecting." };
            WindowRecord editor = candidates[0];
            bool secondary = windows.Any(w => w != null && w.Handle != editor.Handle && w.ProcessId == expectedProcess &&
                w.Visible && w.Enabled && w.Owner != 0 && w.RootOwner == editor.Handle);
            bool blocked = !editor.Enabled || secondary;
            return new WindowDecision {
                Connected = true, Handle = editor.Handle, Blocked = blocked,
                Reason = blocked ? "MAGIX is connected, with a dialog or secondary window open. Finish that window before editing. " : "Connected to the installed MAGIX editor. "
            };
        }
    }

    internal sealed class ControlRecord
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public bool Offscreen { get; set; }
        public string[] Patterns { get; set; }
    }

    internal static class ControlEvidence
    {
        internal static string Summarize(IEnumerable<ControlRecord> controls)
        {
            var list = (controls ?? Enumerable.Empty<ControlRecord>()).Where(c => c != null).ToList();
            var lines = new List<string>();
            string[,] known = { {"Import", "10912"}, {"Templates", "10913"}, {"Effects", "10915"}, {"Audio", "10916"} };
            for (int i=0; i<known.GetLength(0); i++)
            {
                string name=known[i,0], id=known[i,1];
                var matches=list.Where(c => c.Type == "Button" && c.Name == name && c.Id == id && c.Patterns != null && c.Patterns.Contains("Invoke")).ToList();
                if (matches.Count == 1)
                    lines.Add(name + ": accessible Invoke button observed; " + (matches[0].Enabled && !matches[0].Offscreen ? "available in this snapshot" : "currently disabled or offscreen") + ". Activation has not been validated.");
                else if (matches.Count > 1)
                    lines.Add(name + ": multiple matching buttons; target is ambiguous.");
            }
            if (list.Any(c => c.Type == "Custom" && c.Name == "timecode" && (c.Patterns == null || c.Patterns.Length == 0)))
                lines.Add("Timecode: custom control with no readable/writable pattern in this snapshot. Timeline seeking remains manual.");
            return lines.Count == 0 ? "No known panel controls were identified in this bounded snapshot." : String.Join(Environment.NewLine, lines);
        }
    }
}
