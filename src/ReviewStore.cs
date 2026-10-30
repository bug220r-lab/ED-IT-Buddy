using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EditBuddy
{
    public sealed class ReviewDocument
    {
        internal Dictionary<string, object> Extra;
        public string ProjectName { get; set; }
        public string RevisionLabel { get; set; }
        public string ExportPath { get; set; }
        public bool MappingConfirmed { get; set; }
        public List<ReviewNote> Notes { get; set; }
        public ReviewDocument() { Extra = new Dictionary<string, object>(); Notes = new List<ReviewNote>(); }
    }

    public sealed class ReviewNote
    {
        internal Dictionary<string, object> Extra;
        public string Id { get; set; }
        public string Title { get; set; }
        public double? StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
        public string TimeLabel { get; set; }
        public string Observation { get; set; }
        public string Suggestion { get; set; }
        public string Status { get; set; }
        public List<ReviewStep> Steps { get; set; }
        public string DisplayLabel { get { return TimeLabel + "  ·  " + Title; } }
        public ReviewNote() { Extra = new Dictionary<string, object>(); Steps = new List<ReviewStep>(); }
    }

    public sealed class ReviewStep
    {
        internal Dictionary<string, object> Extra;
        public string Id { get; set; }
        public string Title { get; set; }
        public string Instruction { get; set; }
        public string Kind { get; set; }
        public string Action { get; set; }
        public bool Completed { get; set; }
        public ReviewStep() { Extra = new Dictionary<string, object>(); }
    }

    public sealed class ReviewStore
    {
        public string RootPath { get; private set; }
        public string DataPath { get; private set; }
        public string StatePath { get; private set; }
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 40 };
        private const int MaxFileBytes = 2097152;
        private bool stateObserved;
        private bool expectedStateExists;
        private string expectedStateHash;
        private long expectedStateLength;

        public ReviewStore(string root)
        {
            RootPath = Path.GetFullPath(root);
            DataPath = Path.Combine(RootPath, "user-data");
            Directory.CreateDirectory(DataPath);
            StatePath = Path.Combine(DataPath, "review-notes.json");
        }

        public ReviewDocument Load()
        {
            ReviewDocument document;
            if (File.Exists(StatePath)) document = Read(StatePath);
            else
            {
                stateObserved = true; expectedStateExists = false; expectedStateHash = null;
                string seed = Path.Combine(RootPath, "data", "review-notes.seed.json");
                document = File.Exists(seed) ? Read(seed) : EmptyDocument();
            }
            // A remembered selection cannot prove the current MAGIX project still matches.
            document.MappingConfirmed = false;
            return document;
        }

        public ReviewDocument Read(string path)
        {
            string fullPath = Path.GetFullPath(path);
            byte[] bytes = ReadBytes(fullPath);
            string text;
            using (var stream = new MemoryStream(bytes))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) text = reader.ReadToEnd();
            object value;
            try { value = json.DeserializeObject(text); }
            catch (ArgumentException error) { throw new InvalidDataException("The notes file is not valid JSON.", error); }
            ReviewDocument document = Parse(ObjectMap(value, "notes document"));
            // A failed import must not authorize replacing a file changed outside this session.
            if (IsStatePath(fullPath)) { stateObserved = true; expectedStateExists = true; expectedStateHash = Hash(bytes); expectedStateLength = bytes.Length; }
            return document;
        }

        public void AcknowledgeRecoveredState(string recoveryPath)
        {
            string fullPath = Path.GetFullPath(recoveryPath);
            if (IsStatePath(fullPath) || !String.Equals(Path.GetDirectoryName(fullPath), DataPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recovery copy must be a separate file in the local notes folder.");
            long length = new FileInfo(StatePath).Length;
            if (new FileInfo(fullPath).Length != length) throw new IOException("The recovery copy does not match the saved notes.");
            string original = FileHash(StatePath);
            if (original != FileHash(fullPath)) throw new IOException("The recovery copy does not match the saved notes.");
            stateObserved = true; expectedStateExists = true; expectedStateHash = original; expectedStateLength = length;
        }

        private static ReviewDocument Parse(Dictionary<string, object> raw)
        {
            object version = Required(raw, "schemaVersion");
            if (!IsNumber(version) || Convert.ToDouble(version, CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("This notes file must have schemaVersion 1.");
            Boolean(raw, "mappingConfirmed");
            Text(raw, "projectPath", 32768, true);
            Dictionary<string, object> source = ObjectMap(Required(raw, "source"), "source");
            Text(source, "title", 500); Text(source, "url", 4000, true); Text(source, "description", 10000);
            Choice(source, "kind", "prior_critique", "manual", "imported");
            ReviewDocument result = new ReviewDocument();
            result.Extra = raw;
            result.ProjectName = Text(raw, "projectName", 200);
            result.RevisionLabel = Text(raw, "revisionLabel", 200);
            result.ExportPath = Text(raw, "exportPath", 32768, true);
            result.MappingConfirmed = false;
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in Items(raw, "notes", 1000))
            {
                Dictionary<string, object> n = ObjectMap(item, "note");
                ReviewNote note = new ReviewNote();
                note.Extra = n;
                note.Id = Text(n, "id", 200);
                if (String.IsNullOrWhiteSpace(note.Id) || !ids.Add(note.Id)) throw new InvalidDataException("Note IDs must be nonempty and unique.");
                note.Title = Text(n, "title", 240);
                if (String.IsNullOrWhiteSpace(note.Title)) throw new InvalidDataException("Each note needs a title.");
                note.StartSeconds = Number(n, "startSeconds");
                note.EndSeconds = Number(n, "endSeconds");
                if (note.StartSeconds.HasValue && note.EndSeconds.HasValue && note.EndSeconds.Value < note.StartSeconds.Value) throw new InvalidDataException("A note's end must not precede its start.");
                note.TimeLabel = Text(n, "timeLabel", 160);
                note.Observation = Text(n, "observation", 10000);
                note.Suggestion = Text(n, "suggestion", 10000);
                note.Status = Choice(n, "status", "suggested", "in_progress", "reviewed", "dismissed");
                if (n.ContainsKey("editedByUser")) Boolean(n, "editedByUser");
                if (n.ContainsKey("recipeResetByUserEdit")) Boolean(n, "recipeResetByUserEdit");
                if (n.ContainsKey("editedAtUtc"))
                {
                    DateTimeOffset edited;
                    string timestamp = Text(n, "editedAtUtc", 100);
                    if (!Regex.IsMatch(timestamp, "^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})$") ||
                        !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out edited))
                        throw new InvalidDataException("editedAtUtc must be an ISO date-time string.");
                }
                HashSet<string> stepIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (object stepItem in Items(n, "steps", 50))
                {
                    Dictionary<string, object> s = ObjectMap(stepItem, "step");
                    ReviewStep step = new ReviewStep();
                    step.Extra = s;
                    step.Id = Text(s, "id", 200);
                    if (String.IsNullOrWhiteSpace(step.Id) || !stepIds.Add(step.Id)) throw new InvalidDataException("Step IDs must be nonempty and unique within a note.");
                    step.Title = Text(s, "title", 240);
                    if (String.IsNullOrWhiteSpace(step.Title)) throw new InvalidDataException("Each step needs a title.");
                    step.Instruction = Text(s, "instruction", 10000);
                    step.Kind = Choice(s, "kind", "manual", "assisted");
                    step.Action = Text(s, "action", 200, true);
                    step.Completed = Boolean(s, "completed");
                    if ((step.Kind == "manual" && step.Action != null) ||
                        (step.Kind == "assisted" && step.Action != "focus_magix" && step.Action != "inspect_controls"))
                        throw new InvalidDataException("A step must be manual with no action, or use a supported assisted action.");
                    note.Steps.Add(step);
                }
                result.Notes.Add(note);
            }
            return result;
        }

        public void Save(ReviewDocument document)
        {
            byte[] bytes = Serialize(document);
            // Serializes Buddy writers; the content check also detects edits made by other programs.
            using (new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (!stateObserved)
                {
                    if (File.Exists(StatePath)) throw new IOException("Load the saved notes before replacing them.");
                    stateObserved = true; expectedStateExists = false; expectedStateHash = null;
                }
                EnsureStateUnchanged();
                WriteAtomic(StatePath, bytes, EnsureStateUnchanged);
                expectedStateExists = true; expectedStateHash = Hash(bytes); expectedStateLength = bytes.Length;
            }
        }

        public void Write(string path, ReviewDocument document)
        {
            string fullPath = Path.GetFullPath(path);
            if (IsStatePath(fullPath)) { Save(document); return; }
            WriteAtomic(fullPath, Serialize(document), null);
        }

        private byte[] Serialize(ReviewDocument document)
        {
            if (document == null || document.Notes == null) throw new InvalidDataException("A notes document and notes list are required.");
            Dictionary<string, object> raw = new Dictionary<string, object>(document.Extra);
            raw["schemaVersion"] = 1;
            raw["projectName"] = document.ProjectName;
            raw["revisionLabel"] = document.RevisionLabel;
            raw["exportPath"] = document.ExportPath;
            if (!raw.ContainsKey("projectPath")) raw["projectPath"] = null;
            if (!raw.ContainsKey("source")) raw["source"] = ManualSource();
            // Confirmation is valid only in the current session.
            raw["mappingConfirmed"] = false;
            List<object> noteItems = new List<object>();
            foreach (ReviewNote note in document.Notes)
            {
                if (note == null || note.Steps == null) throw new InvalidDataException("Each note needs a steps list.");
                Dictionary<string, object> n = new Dictionary<string, object>(note.Extra);
                n["id"] = note.Id; n["title"] = note.Title;
                n["startSeconds"] = note.StartSeconds; n["endSeconds"] = note.EndSeconds;
                n["timeLabel"] = note.TimeLabel; n["observation"] = note.Observation;
                n["suggestion"] = note.Suggestion; n["status"] = note.Status;
                List<object> stepItems = new List<object>();
                foreach (ReviewStep step in note.Steps)
                {
                    if (step == null) throw new InvalidDataException("A step cannot be null.");
                    Dictionary<string, object> s = new Dictionary<string, object>(step.Extra);
                    s["id"] = step.Id; s["title"] = step.Title; s["instruction"] = step.Instruction;
                    s["kind"] = step.Kind; s["action"] = step.Action; s["completed"] = step.Completed;
                    stepItems.Add(s);
                }
                n["steps"] = stepItems; noteItems.Add(n);
            }
            raw["notes"] = noteItems;
            Parse(raw); // A saved file must pass the same validation as an imported file.
            string output = json.Serialize(raw);
            if (Encoding.UTF8.GetByteCount(output) > MaxFileBytes) throw new InvalidDataException("Notes exceed the 2 MB UTF-8 file limit.");
            return new UTF8Encoding(false, true).GetBytes(output);
        }

        private static void WriteAtomic(string fullPath, byte[] bytes, Action beforeCommit)
        {
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (beforeCommit != null) beforeCommit();
                if (File.Exists(fullPath)) File.Replace(temporary, fullPath, fullPath + ".bak", true);
                else File.Move(temporary, fullPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private void EnsureStateUnchanged()
        {
            bool exists = File.Exists(StatePath);
            if (exists != expectedStateExists || (exists && (new FileInfo(StatePath).Length != expectedStateLength || FileHash(StatePath) != expectedStateHash)))
                throw new IOException("Saved notes changed outside this session. Your changes are still in memory. Export them to a separate file, then reload or import the current saved notes before saving again.");
        }

        private bool IsStatePath(string fullPath) { return String.Equals(fullPath, StatePath, StringComparison.OrdinalIgnoreCase); }

        private static byte[] ReadBytes(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaxFileBytes) throw new InvalidDataException("Notes file must be no larger than 2 MB.");
                using (var memory = new MemoryStream()) { file.CopyTo(memory); return memory.ToArray(); }
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        private static string FileHash(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(file));
        }

        public static ReviewDocument EmptyDocument()
        {
            ReviewDocument document = new ReviewDocument { ProjectName = "My edit", RevisionLabel = "Working revision", MappingConfirmed = false };
            document.Extra["projectPath"] = null;
            document.Extra["source"] = ManualSource();
            return document;
        }

        private static Dictionary<string, object> ManualSource()
        {
            return new Dictionary<string, object> { { "title", "Local review notes" }, { "url", null }, { "kind", "manual" }, { "description", "Notes authored locally in ED-IT Buddy." } };
        }

        public static ReviewStep ManualStep(string instruction)
        {
            return new ReviewStep { Id = Guid.NewGuid().ToString("N"), Title = "Try the proposed change", Instruction = instruction, Kind = "manual", Action = null, Completed = false };
        }

        public static string FormatTime(double? seconds)
        {
            if (!seconds.HasValue) return "Time not set";
            TimeSpan t = TimeSpan.FromSeconds(seconds.Value);
            return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:000}", (int)t.TotalMinutes, t.Seconds, t.Milliseconds);
        }

        public void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(DataPath, "buddy.log"), DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine); }
            catch { }
        }

        private static Dictionary<string, object> ObjectMap(object item, string label)
        {
            Dictionary<string, object> result = item as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("Expected an object for " + label + ".");
            return result;
        }
        private static IEnumerable Items(Dictionary<string, object> map, string key, int max)
        {
            object value = Required(map, key);
            IList list = value as IList;
            if (list == null || list.Count > max) throw new InvalidDataException("Invalid or oversized " + key + " list.");
            return list;
        }
        private static string Text(Dictionary<string, object> map, string key, int max, bool allowNull = false)
        {
            object value = Required(map, key);
            if (value == null && allowNull) return null;
            string text = value as string;
            if (text == null || text.Length > max) throw new InvalidDataException("Invalid or oversized " + key + ".");
            return text;
        }
        private static bool Boolean(Dictionary<string, object> map, string key)
        { object value = Required(map, key); if (!(value is bool)) throw new InvalidDataException(key + " must be a boolean."); return (bool)value; }
        private static object Required(Dictionary<string, object> map, string key)
        { object value; if (!map.TryGetValue(key, out value)) throw new InvalidDataException("Missing required field: " + key + "."); return value; }
        private static string Choice(Dictionary<string, object> map, string key, params string[] allowed)
        { string value = Text(map, key, 80); if (Array.IndexOf(allowed, value) < 0) throw new InvalidDataException("Unsupported " + key + ": " + value + "."); return value; }
        private static bool IsNumber(object value) { return value is int || value is long || value is decimal || value is double; }
        private static double? Number(Dictionary<string, object> map, string key)
        {
            object value = Required(map, key);
            if (value == null) return null;
            if (!IsNumber(value)) throw new InvalidDataException("Invalid time in " + key + ".");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || number < 0 || number > 604800) throw new InvalidDataException("Time must be between 0 and 604800 seconds.");
            return number;
        }
    }
}
