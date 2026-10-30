using System;
using System.Threading;
using EditBuddy;

internal static class CompanionStateTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        string mutexA = BuddyApplication.InstanceMutexName("C:\\Buddy\\Project\\", "test-user");
        string mutexB = BuddyApplication.InstanceMutexName("c:\\buddy\\project", "test-user");
        Check(mutexA == mutexB, "Same Windows path created different instance names.");
        Check(mutexA != BuddyApplication.InstanceMutexName("C:\\Buddy\\Other", "test-user"), "Different projects share an instance lock.");
        Check(mutexA != BuddyApplication.InstanceMutexName("C:\\Buddy\\Project", "another-user"), "Different users share an instance lock.");
        bool createdFirst; bool createdSecond;
        using (Mutex first = new Mutex(true, mutexA + "_test", out createdFirst))
        {
            try { using (Mutex second = new Mutex(true, mutexB + "_test", out createdSecond)) Check(createdFirst && !createdSecond, "Second instance acquired an independent lock."); }
            finally { if (createdFirst) first.ReleaseMutex(); }
        }
        ConnectionReport previous = new ConnectionReport { ProcessId = 100, WindowTitle = "Project A", Connected = true };
        Check(!CompanionWindow.CanRequestMagix(true, false) && !CompanionWindow.CanRequestMagix(true, true), "Offline mode permits MAGIX operations.");
        Check(!CompanionWindow.CanRequestMagix(false, true) && CompanionWindow.CanRequestMagix(false, false), "Busy/online gate is inconsistent.");
        Check(!CompanionWindow.CanUseConnectedAction(true, false, previous, "focus_magix") && !CompanionWindow.CanUseConnectedAction(true, false, previous, "inspect_controls"), "An existing connection bypasses offline mode.");
        Check(!CompanionWindow.CanUseConnectedAction(false, false, new ConnectionReport { Connected = false }, "focus_magix"), "Disconnected action remained enabled.");
        Check(!CompanionWindow.CanUseConnectedAction(false, false, null, "inspect_controls"), "Absent connection remained enabled.");
        Check(CompanionWindow.CanUseConnectedAction(false, false, previous, "focus_magix"), "Explicit online connection cannot use focus action.");
        Check(CompanionWindow.OfflinePreferenceFromJson(null) && CompanionWindow.OfflinePreferenceFromJson("broken") && CompanionWindow.OfflinePreferenceFromJson("{}"), "Missing or damaged preference enables computer access.");
        Check(CompanionWindow.OfflinePreferenceFromJson("{\"workOffline\":true}") && !CompanionWindow.OfflinePreferenceFromJson("{\"workOffline\":false}"), "Saved offline preference ignored.");
        Check(!CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 100, WindowTitle = "Project A", Connected = true }), "Stable action invalidates mapping.");
        Check(CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 100, WindowTitle = "Project B", Connected = true }), "Changed project title keeps mapping.");
        Check(CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 101, WindowTitle = "Project A", Connected = true }), "Changed process keeps mapping.");
        Check(CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 100, ProcessStartUtcTicks = 1, WindowTitle = "Project A", Connected = true }), "Reused process ID keeps mapping.");
        Check(CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 100, WindowHandle = 1, WindowTitle = "Project A", Connected = true }), "Replaced editor window keeps mapping.");
        Check(CompanionWindow.ConnectionIdentityChanged(previous, new ConnectionReport { ProcessId = 100, WindowTitle = "Project A", Connected = false }), "Disconnected state keeps mapping.");
        double? parsed;
        Check(CompanionWindow.TryOptionalTime("", out parsed) && !parsed.HasValue, "Blank position becomes zero.");
        Check(CompanionWindow.TryOptionalTime("24.2", out parsed) && parsed.Value == 24.2, "Valid position rejected.");
        Check(!CompanionWindow.TryOptionalTime("NaN", out parsed), "NaN position accepted.");
        Check(!CompanionWindow.TryOptionalTime("-1", out parsed), "Negative position accepted.");
        ReviewNote note = new ReviewNote { Title = "Original", StartSeconds = null, EndSeconds = null, TimeLabel = "Position unknown", Observation = "Original observation", Suggestion = "Original suggestion", Status = "dismissed" };
        note.Extra["sourceMarker"] = "retain";
        note.Steps.Add(new ReviewStep { Title = "Old recipe", Instruction = "Old instruction", Kind = "assisted", Action = "focus_magix", Completed = true });
        note.Steps.Add(ReviewStore.ManualStep("Second old instruction"));
        CompanionWindow.ApplyNoteEdits(note, note.Title, null, null, note.Observation, note.Suggestion, false);
        Check(note.Status == "dismissed" && note.Steps.Count == 2 && note.TimeLabel == "Position unknown", "Unchanged note was rewritten.");
        Check(CompanionWindow.ApplyNoteEdits(note, note.Title, null, null, "Edited observation", "New suggested change", true), "Suggestion change was not detected.");
        Check(note.Steps.Count == 1 && note.Steps[0].Kind == "manual" && note.Steps[0].Action == null && note.Steps[0].Instruction == "New suggested change", "Old recipe survived edited suggestion.");
        Check((bool)note.Extra["editedByUser"] && note.Extra["sourceMarker"].ToString() == "retain" && note.Extra.ContainsKey("editedAtUtc"), "Edit provenance or original metadata lost.");
        Check(note.Status == "suggested" && !note.Steps[0].Completed, "Edited note retained completed state.");
        Console.WriteLine("PASS: offline guards/preferences, disconnected actions, single-instance scope, stable/changed mapping identity, optional time validation, unchanged-note preservation, recipe replacement and edit provenance.");
        return 0;
    }
}
