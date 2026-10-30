using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using EditBuddy;

internal static class ReviewStoreTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
    private static int failures;
    private static string root;
    private static string run;
    private static ReviewStore store;

    public static int Main(string[] args)
    {
        root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
        run = Path.Combine(root, "tests", "artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        store = new ReviewStore(run);
        Test("Seed round-trip preserves progress and source; mapping resets", delegate {
            ReviewDocument doc = store.Read(Path.Combine(root, "data", "review-notes.seed.json"));
            doc.MappingConfirmed = true;
            doc.Notes[0].Steps[0].Completed = true;
            string path = Path.Combine(run, "roundtrip.json");
            store.Write(path, doc);
            ReviewDocument reloaded = store.Read(path);
            Check(reloaded.Notes.Count == 3, "seed notes lost");
            Check(reloaded.Notes[0].Steps[0].Completed, "completion lost");
            Check(!reloaded.MappingConfirmed, "stale project mapping restored");
            Check(Map(ReadJson(path)["source"])["kind"].ToString() == "prior_critique", "source lost");
        });
        Test("Duplicate note IDs rejected", delegate {
            Dictionary<string, object> raw = Seed();
            IList notes = (IList)raw["notes"];
            Map(notes[1])["id"] = Map(notes[0])["id"];
            ExpectInvalid(delegate { store.Read(Fixture("duplicate-notes", raw)); });
        });
        Test("Unsupported schema version rejected", delegate {
            Dictionary<string, object> raw = Seed(); raw["schemaVersion"] = 2;
            ExpectInvalid(delegate { store.Read(Fixture("version", raw)); });
        });
        Test("Unknown imported actions cannot execute", delegate {
            Dictionary<string, object> raw = Seed();
            Dictionary<string, object> step = FirstStep(raw);
            step["kind"] = "assisted"; step["action"] = "run_arbitrary_command";
            try {
                ReviewStep loaded = store.Read(Fixture("unknown-action", raw)).Notes[0].Steps[0];
                Check(loaded.Kind == "manual" && loaded.Action == null, "unknown action remained enabled");
            } catch (InvalidDataException) { }
        });
        Test("Duplicate step IDs rejected", delegate {
            Dictionary<string, object> raw = Seed();
            IList steps = (IList)FirstNote(raw)["steps"];
            Map(steps[1])["id"] = Map(steps[0])["id"];
            ExpectInvalid(delegate { store.Read(Fixture("duplicate-steps", raw)); });
        });
        Test("Dismissed status survives import and export", delegate {
            Dictionary<string, object> raw = Seed(); FirstNote(raw)["status"] = "dismissed";
            ReviewDocument doc = store.Read(Fixture("dismissed", raw));
            Check(doc.Notes[0].Status == "dismissed", "dismissed changed to " + doc.Notes[0].Status);
        });
        Test("Unknown time remains unknown on round-trip", delegate {
            Dictionary<string, object> raw = Seed();
            FirstNote(raw)["startSeconds"] = null; FirstNote(raw)["endSeconds"] = null;
            FirstNote(raw)["timeLabel"] = "Position not known";
            ReviewDocument doc = store.Read(Fixture("unknown-time", raw));
            string output = Path.Combine(run, "unknown-time-saved.json"); store.Write(output, doc);
            Dictionary<string, object> note = FirstNote(ReadJson(output));
            Check(note["startSeconds"] == null && note["endSeconds"] == null, "unknown position was converted to a known zero");
        });
        Test("Unicode write either rejects size or remains readable", delegate {
            ReviewDocument doc = store.Read(Path.Combine(root, "data", "review-notes.seed.json"));
            doc.Notes.Clear();
            for (int i = 0; i < 80; i++) {
                ReviewNote note = new ReviewNote { Id = "unicode-" + i, Title = "Unicode note", StartSeconds = 0, EndSeconds = 1,
                    TimeLabel = "00:00", Observation = new string('\u753b', 10000), Suggestion = "Compare", Status = "suggested" };
                note.Steps.Add(ReviewStore.ManualStep("Compare")); doc.Notes.Add(note);
            }
            string output = Path.Combine(run, "unicode-size.json");
            store.Write(output, store.Read(Path.Combine(root, "data", "review-notes.seed.json")));
            try { store.Write(output, doc); }
            catch (InvalidDataException) { Check(store.Read(output).Notes.Count == 3, "oversized write damaged the previous notes file"); return; }
            try { store.Read(output); }
            catch (InvalidDataException error) { throw new Exception("Saved " + new FileInfo(output).Length + " UTF-8 bytes but cannot reopen: " + error.Message); }
        });
        Test("New empty document retains required v1 metadata", delegate {
            string output = Path.Combine(run, "empty.json"); store.Write(output, ReviewStore.EmptyDocument());
            Dictionary<string, object> raw = ReadJson(output);
            Check(raw.ContainsKey("source") && raw.ContainsKey("projectPath"), "new document omits schema-required source/projectPath fields");
        });
        Test("Required document fields and their types are enforced", delegate {
            Dictionary<string, object> raw = Seed(); raw.Remove("notes");
            ExpectInvalid(delegate { store.Read(Fixture("missing-notes", raw)); });
            raw = Seed(); raw["notes"] = null;
            ExpectInvalid(delegate { store.Read(Fixture("null-notes", raw)); });
            raw = Seed(); raw["schemaVersion"] = "1";
            ExpectInvalid(delegate { store.Read(Fixture("string-version", raw)); });
            raw = Seed(); raw["mappingConfirmed"] = "false";
            ExpectInvalid(delegate { store.Read(Fixture("string-mapping", raw)); });
            raw = Seed(); raw["projectPath"] = new object[0];
            ExpectInvalid(delegate { store.Read(Fixture("invalid-project-path", raw)); });
        });
        Test("Invalid metadata and step booleans cannot silently normalize", delegate {
            Dictionary<string, object> raw = Seed(); raw["source"] = "not an object";
            ExpectInvalid(delegate { store.Read(Fixture("invalid-source", raw)); });
            raw = Seed(); Map(raw["source"])["kind"] = "guessed";
            ExpectInvalid(delegate { store.Read(Fixture("invalid-source-kind", raw)); });
            raw = Seed(); FirstStep(raw)["completed"] = "true";
            ExpectInvalid(delegate { store.Read(Fixture("invalid-completion", raw)); });
            raw = Seed(); FirstNote(raw)["status"] = "unexpected";
            ExpectInvalid(delegate { store.Read(Fixture("invalid-status", raw)); });
            raw = Seed(); FirstNote(raw)["editedByUser"] = 1;
            ExpectInvalid(delegate { store.Read(Fixture("invalid-provenance", raw)); });
            raw = Seed(); FirstNote(raw)["editedAtUtc"] = "2026-10-29T16:30:00";
            ExpectInvalid(delegate { store.Read(Fixture("ambiguous-provenance-time", raw)); });
        });
        Test("Manual steps cannot retain an assisted action", delegate {
            Dictionary<string, object> raw = Seed(); FirstStep(raw)["kind"] = "manual";
            ExpectInvalid(delegate { store.Read(Fixture("manual-action", raw)); });
        });
        Test("Valid empty steps and inert extension data round-trip unchanged", delegate {
            Dictionary<string, object> raw = Seed();
            FirstNote(raw)["steps"] = new object[0];
            FirstNote(raw)["customLabel"] = "my local annotation";
            FirstNote(raw)["editedAtUtc"] = "2026-10-29T16:30:00.0000000Z";
            raw["customMetadata"] = new Dictionary<string, object> { { "checked", false } };
            ReviewDocument doc = store.Read(Fixture("extension-data", raw));
            string output = Path.Combine(run, "extension-data-saved.json"); store.Write(output, doc);
            Dictionary<string, object> saved = ReadJson(output);
            Check(((IList)FirstNote(saved)["steps"]).Count == 0, "a step was silently invented");
            Check(FirstNote(saved)["customLabel"].ToString() == "my local annotation", "note extension lost");
            Check(Map(saved["customMetadata"])["checked"] is bool, "document extension lost");
        });
        Test("Invalid in-memory edits cannot replace valid saved notes", delegate {
            string local = Path.Combine(run, "invalid-save");
            ReviewStore target = new ReviewStore(local);
            ReviewDocument doc = target.Read(Path.Combine(root, "data", "review-notes.seed.json"));
            target.Save(doc); string prior = File.ReadAllText(target.StatePath);
            doc.Notes[0].Title = "";
            ExpectInvalid(delegate { target.Save(doc); });
            Check(File.ReadAllText(target.StatePath) == prior, "invalid save replaced previous state");
        });
        Test("External state edits are preserved and memory can export separately", delegate {
            ReviewStore first = new ReviewStore(Path.Combine(run, "external-edit"));
            ReviewDocument mine = first.Load(); first.Save(mine);
            ReviewStore second = new ReviewStore(first.RootPath);
            ReviewDocument theirs = second.Load(); theirs.ProjectName = "Changed externally"; second.Save(theirs);
            mine.ProjectName = "My unsaved work";
            ExpectIo(delegate { first.Save(mine); });
            ExpectIo(delegate { first.Write(first.StatePath, mine); });
            Check(ReadJson(first.StatePath)["projectName"].ToString() == "Changed externally", "external edit was overwritten");
            string rescued = Path.Combine(first.DataPath, "my-work.json"); first.Write(rescued, mine);
            Check(ReadJson(rescued)["projectName"].ToString() == "My unsaved work", "memory export failed");
            ReviewDocument merged = first.Read(first.StatePath); merged.RevisionLabel = "Reloaded"; first.Save(merged);
            Check(ReadJson(first.StatePath)["revisionLabel"].ToString() == "Reloaded", "explicit reload could not save");
        });
        Test("A deleted or newly created state file causes a save conflict", delegate {
            ReviewStore deleted = new ReviewStore(Path.Combine(run, "deleted-state"));
            ReviewDocument doc = deleted.Load(); deleted.Save(doc);
            File.Delete(deleted.StatePath);
            ExpectIo(delegate { deleted.Save(doc); });
            Check(!File.Exists(deleted.StatePath), "deleted state was silently recreated");
            ReviewStore first = new ReviewStore(Path.Combine(run, "new-state"));
            ReviewDocument mine = first.Load();
            ReviewStore second = new ReviewStore(first.RootPath);
            ReviewDocument theirs = second.Load(); theirs.ProjectName = "External first save"; second.Save(theirs);
            ExpectIo(delegate { first.Save(mine); });
            Check(ReadJson(first.StatePath)["projectName"].ToString() == "External first save", "new external state was overwritten");
        });
        Test("Successful state saves preserve the immediately prior file", delegate {
            ReviewStore target = new ReviewStore(Path.Combine(run, "backup"));
            ReviewDocument doc = target.Load(); target.Save(doc);
            string prior = File.ReadAllText(target.StatePath);
            doc.ProjectName = "Second save"; target.Save(doc);
            Check(File.ReadAllText(target.StatePath + ".bak") == prior, "prior saved notes were not retained");
            Check(Directory.GetFiles(target.DataPath, "*.tmp").Length == 0, "temporary file left after success");
        });
        Test("Failed import of changed state does not refresh save authorization", delegate {
            ReviewStore target = new ReviewStore(Path.Combine(run, "failed-reload"));
            ReviewDocument doc = target.Load(); target.Save(doc);
            File.WriteAllText(target.StatePath, "{ malformed json", new UTF8Encoding(false));
            ExpectInvalid(delegate { target.Read(target.StatePath); });
            ExpectIo(delegate { target.Save(doc); });
            Check(File.ReadAllText(target.StatePath) == "{ malformed json", "failed import allowed stale overwrite");
        });
        Test("Startup recovery requires a separate byte-identical retained copy", delegate {
            ReviewStore target = new ReviewStore(Path.Combine(run, "recovery"));
            File.WriteAllText(target.StatePath, "{ corrupted notes", new UTF8Encoding(false));
            ExpectInvalid(delegate { target.Load(); });
            ExpectIo(delegate { target.Save(ReviewStore.EmptyDocument()); });
            string recovery = Path.Combine(target.DataPath, "notes-recovery-test.json");
            File.WriteAllText(recovery, "wrong content", new UTF8Encoding(false));
            ExpectIo(delegate { target.AcknowledgeRecoveredState(recovery); });
            ExpectIo(delegate { target.AcknowledgeRecoveredState(target.StatePath); });
            File.Copy(target.StatePath, recovery, true);
            target.AcknowledgeRecoveredState(recovery);
            target.Save(ReviewStore.EmptyDocument());
            Check(File.ReadAllText(recovery) == "{ corrupted notes", "original recovery copy was altered");
            Check(target.Read(target.StatePath).Notes.Count == 0, "replacement notes were not readable");
        });
        Console.WriteLine("Results: " + failures + " failed. Fixtures: " + run);
        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
    private static Dictionary<string, object> ReadJson(string path) { return Map(Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8))); }
    private static Dictionary<string, object> Seed() { return ReadJson(Path.Combine(root, "data", "review-notes.seed.json")); }
    private static Dictionary<string, object> FirstNote(Dictionary<string, object> raw) { return Map(((IList)raw["notes"])[0]); }
    private static Dictionary<string, object> FirstStep(Dictionary<string, object> raw) { return Map(((IList)FirstNote(raw)["steps"])[0]); }
    private static string Fixture(string name, Dictionary<string, object> raw) { string path = Path.Combine(run, name + ".json"); File.WriteAllText(path, Json.Serialize(raw), new UTF8Encoding(false)); return path; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectInvalid(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Input unexpectedly accepted"); }
    private static void ExpectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Conflicting save unexpectedly accepted"); }
    private static void Test(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
}
