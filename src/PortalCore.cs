using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace EditBuddy
{
    public sealed class PortalStep
    {
        public string action { get; set; }
        public Dictionary<string, object> args { get; set; }
    }
    public sealed class PortalSequence
    {
        public string request_id { get; set; }
        public string workspace { get; set; }
        public List<PortalStep> steps { get; set; }
    }
    public sealed class PortalCapability
    {
        public string action { get; set; }
        public bool supported { get; set; }
        public string evidence { get; set; }
        public string reason { get; set; }
        // Null retains the original single-action contract. Batch-only support
        // must not advertise an independently usable trim or append operation.
        public string execution_mode { get; set; }
    }
    public sealed class PortalActionResult
    {
        public string status { get; set; }
        public string message { get; set; }
        public object evidence { get; set; }
        public List<string> artifacts { get; set; }
        public bool input_sent { get; set; }
        public string verification_kind { get; set; }
    }
    public sealed class PortalStepReceipt
    {
        public int index { get; set; }
        public string action { get; set; }
        public Dictionary<string, object> args { get; set; }
        public string status { get; set; }
        public bool input_sent { get; set; }
        public string started_utc { get; set; }
        public string finished_utc { get; set; }
        public PortalActionResult result { get; set; }
        public string batch_id { get; set; }
        public int? batch_start_index { get; set; }
        public int? batch_step_count { get; set; }
    }
    public sealed class PortalJob
    {
        public string job_id { get; set; }
        public string request_id { get; set; }
        public string request_fingerprint { get; set; }
        public string workspace { get; set; }
        public string status { get; set; }
        public string message { get; set; }
        public int current_step { get; set; }
        public bool stop_requested { get; set; }
        public string created_utc { get; set; }
        public string updated_utc { get; set; }
        public List<PortalStepReceipt> steps { get; set; }
        public List<string> artifacts { get; set; }
    }
    public interface IPortalActionRunner
    {
        object ReadState();
        IList<PortalCapability> Capabilities();
        PortalActionResult Execute(PortalStep step, PortalExecutionContext context);
    }
    public sealed class PortalBatchPlan
    {
        public int start_index { get; set; }
        public int count { get; set; }
        public List<PortalStep> steps { get; set; }
        public string batch_id { get; set; }
        public bool supported { get; set; }
        public string evidence { get; set; }
        public object preparation { get; set; }
    }
    public interface IPortalBatchActionRunner
    {
        // Pure planning only: no files, editor input, or external side effects.
        PortalBatchPlan PlanBatch(PortalSequence sequence, int startIndex);
        // MarkInputSent persists intent for the entire batch before native input.
        // Preparation is not success. A verified result must carry a native proof.
        PortalActionResult ExecuteBatch(PortalBatchPlan plan, PortalExecutionContext context);
    }
    public sealed class PortalExecutionContext
    {
        private readonly Func<bool> cancelled;
        private readonly Action inputSent;
        public string Workspace { get; private set; }
        public string JobId { get; private set; }
        public int StepIndex { get; private set; }
        public bool CancellationRequested { get { return cancelled(); } }
        internal PortalExecutionContext(string workspace, string jobId, int index, Func<bool> cancellation, Action markInput)
        { Workspace = workspace; JobId = jobId; StepIndex = index; cancelled = cancellation; inputSent = markInput; }
        public void MarkInputSent() { inputSent(); }
        public void ThrowIfCancellationRequested()
        { if (CancellationRequested) throw new OperationCanceledException("The job was stopped or its connection lease expired."); }
    }
    public sealed class PortalException : Exception
    {
        public string Code { get; private set; }
        public PortalException(string code, string message) : base(message) { Code = code; }
    }
    public static class PortalValidation
    {
        public static readonly string[] Actions = { "create_project", "import_media", "trim_clip", "append_clip", "add_title", "save_project", "export_video" };
        private static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]> {
            { "create_project", new[] { "project_path", "width", "height", "fps" } },
            { "import_media", new[] { "media_path", "clip_id" } },
            { "trim_clip", new[] { "clip_id", "source_in_frame", "source_out_frame" } },
            { "append_clip", new[] { "clip_id", "track" } },
            { "add_title", new[] { "text", "timeline_frame", "duration_frames", "track" } },
            { "save_project", new[] { "project_path" } },
            { "export_video", new[] { "output_path" } }
        };
        public static void Id(string value, string label)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 80 || value.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-' && c != '.'))
                throw new PortalException("invalid_request", label + " must contain 1–80 ASCII letters, digits, periods, underscores, or hyphens.");
        }
        public static string Text(IDictionary<string, object> args, string name, int max)
        {
            object value;
            if (!args.TryGetValue(name, out value) || !(value is string) || String.IsNullOrWhiteSpace((string)value) || ((string)value).Length > max || ((string)value).IndexOf('\0') >= 0)
                throw new PortalException("invalid_request", name + " must be nonempty text of at most " + max + " characters.");
            return (string)value;
        }
        public static int Integer(IDictionary<string, object> args, string name, int min, int max)
        {
            object value;
            if (!args.TryGetValue(name, out value) || !(value is int || value is long || value is decimal || value is double))
                throw new PortalException("invalid_request", name + " must be an integer.");
            decimal number;
            try { number = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
            catch { throw new PortalException("invalid_request", name + " is outside its supported range."); }
            if (Decimal.Truncate(number) != number || number < min || number > max)
                throw new PortalException("invalid_request", name + " is outside its supported integer range.");
            return (int)number;
        }
        public static string UnderRoot(string path, string root, bool allowRoot)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 240 || !Path.IsPathRooted(path) || path.StartsWith("\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
                throw new PortalException("invalid_path", "Only ordinary absolute local paths are accepted.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!(allowRoot && String.Equals(full, parent, StringComparison.OrdinalIgnoreCase)) && !full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new PortalException("invalid_path", "The requested path is outside its assigned Buddy workspace.");
            // Check every existing ancestor, including the configured root. No junction escape.
            string cursor = full;
            while (!String.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new PortalException("invalid_path", "Reparse points are not accepted in portal workspace paths.");
                cursor = Path.GetDirectoryName(cursor);
            }
            return full;
        }
        public static void Validate(PortalSequence sequence, string workspaceRoot)
        {
            if (sequence == null) throw new PortalException("invalid_request", "A sequence is required.");
            Id(sequence.request_id, "request_id");
            sequence.workspace = UnderRoot(sequence.workspace, workspaceRoot, false);
            if (sequence.steps == null || sequence.steps.Count < 1 || sequence.steps.Count > 64)
                throw new PortalException("invalid_request", "A sequence must contain between 1 and 64 steps.");
            foreach (PortalStep step in sequence.steps)
            {
                string[] allowed;
                if (step == null || step.action == null || !Fields.TryGetValue(step.action, out allowed))
                    throw new PortalException("unsupported_action", "The sequence contains an unknown editing action.");
                if (step.args == null || step.args.Count != allowed.Length || step.args.Keys.Any(k => !allowed.Contains(k)))
                    throw new PortalException("invalid_request", "Unexpected or missing arguments for " + step.action + ".");
                foreach (string key in allowed.Where(k => k.EndsWith("_path", StringComparison.Ordinal)))
                    step.args[key] = UnderRoot(Text(step.args, key, 240), sequence.workspace, false);
                if (step.args.ContainsKey("project_path") && !String.Equals(Path.GetExtension((string)step.args["project_path"]), ".mvp", StringComparison.OrdinalIgnoreCase))
                    throw new PortalException("invalid_path", "Project paths must end in .MVP.");
                if (step.args.ContainsKey("output_path") && !String.Equals(Path.GetExtension((string)step.args["output_path"]), ".mp4", StringComparison.OrdinalIgnoreCase))
                    throw new PortalException("invalid_path", "The first portal export format is MP4.");
                if (step.args.ContainsKey("clip_id")) Id(Text(step.args, "clip_id", 80), "clip_id");
                if (step.action == "create_project")
                { Integer(step.args, "width", 16, 7680); Integer(step.args, "height", 16, 4320); Integer(step.args, "fps", 1, 240); }
                if (step.action == "trim_clip")
                {
                    int first = Integer(step.args, "source_in_frame", 0, 100000000);
                    int end = Integer(step.args, "source_out_frame", 1, 100000000);
                    if (end <= first) throw new PortalException("invalid_request", "source_out_frame must follow source_in_frame; the end is exclusive.");
                }
                if (step.args.ContainsKey("track")) Integer(step.args, "track", 1, 99);
                if (step.action == "add_title")
                { Text(step.args, "text", 500); Integer(step.args, "timeline_frame", 0, 100000000); Integer(step.args, "duration_frames", 1, 100000000); }
            }
        }
        public static object Canonical(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map != null) return new SortedDictionary<string, object>(map.ToDictionary(kv => kv.Key, kv => Canonical(kv.Value)), StringComparer.Ordinal);
            var sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) return sequence.Cast<object>().Select(Canonical).ToArray();
            return value;
        }
    }
    public sealed class PortalCoordinator : IDisposable
    {
        private sealed class ExecutionUnit
        {
            internal int Start;
            internal int Count;
            internal PortalBatchPlan Batch;
        }
        private readonly object gate = new object();
        private readonly IPortalActionRunner runner;
        private readonly string receiptsDirectory;
        private readonly string workspaceRoot;
        private readonly Dictionary<string, PortalJob> jobs = new Dictionary<string, PortalJob>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> requestJobs = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Timer leaseTimer;
        private readonly Func<DateTime> now;
        private PortalJob current;
        private string owner;
        private DateTime leaseUntil;
        private string stopStatus;
        private bool online;
        private bool disposed;
        public const int LeaseSeconds = 15;
        public PortalCoordinator(string receiptsDirectory, string workspaceRoot, IPortalActionRunner runner)
            : this(receiptsDirectory, workspaceRoot, runner, () => DateTime.UtcNow) { }
        public PortalCoordinator(string receiptsDirectory, string workspaceRoot, IPortalActionRunner runner, Func<DateTime> clock)
        {
            if (runner == null || clock == null) throw new ArgumentNullException("runner");
            this.runner = runner; this.now = clock;
            this.receiptsDirectory = Path.GetFullPath(receiptsDirectory);
            this.workspaceRoot = Path.GetFullPath(workspaceRoot);
            Directory.CreateDirectory(this.receiptsDirectory);
            Directory.CreateDirectory(this.workspaceRoot);
            LoadReceipts();
            leaseTimer = new Timer(o => CheckLease(), null, 1000, 1000);
        }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 }; }
        private string Timestamp() { return now().ToString("o", CultureInfo.InvariantCulture); }
        private static PortalJob Clone(PortalJob job) { return job == null ? null : Json().Deserialize<PortalJob>(Json().Serialize(job)); }
        public void SetOnline(bool value)
        {
            lock (gate)
            {
                if (disposed && value) throw new ObjectDisposedException("PortalCoordinator");
                online = value;
                if (!value) RequestStop("paused", "Buddy was disconnected; no further editing steps will start.");
            }
        }
        public object GetState()
        {
            object magix;
            try { magix = runner.ReadState(); }
            catch (Exception ex) { magix = new { ready = false, error = ex.Message }; }
            lock (gate)
            {
                CheckLeaseLocked();
                return new { host = Environment.MachineName, user_sid = WindowsIdentity.GetCurrent().User.Value,
                    session_id = Process.GetCurrentProcess().SessionId, online = online, current_job = Clone(current), magix = magix };
            }
        }
        public IList<PortalCapability> GetCapabilities()
        {
            IList<PortalCapability> reported = runner.Capabilities() ?? new List<PortalCapability>();
            return PortalValidation.Actions.Select(a => reported.FirstOrDefault(c => c.action == a) ?? new PortalCapability {
                action = a, supported = false, reason = "This action has not passed live validation on this MAGIX build."
            }).ToList();
        }
        public PortalJob Submit(PortalSequence submitted, string connectionId)
        {
            // Copy before validation so a caller cannot change a queued action.
            PortalSequence sequence = Json().Deserialize<PortalSequence>(Json().Serialize(submitted));
            PortalValidation.Validate(sequence, workspaceRoot);
            string fingerprint;
            using (SHA256 sha = SHA256.Create())
                fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json().Serialize(PortalValidation.Canonical(Json().DeserializeObject(Json().Serialize(sequence))))))).Replace("-", "").ToLowerInvariant();
            lock (gate)
            {
                string existing;
                if (requestJobs.TryGetValue(sequence.request_id, out existing))
                {
                    PortalJob job = jobs[existing];
                    if (job.request_fingerprint != fingerprint) throw new PortalException("request_conflict", "This request_id already belongs to a different sequence.");
                    return Clone(job);
                }
                if (disposed || !online) throw new PortalException("offline", "Buddy is offline. No editing input was sent.");
                if (current != null && (current.status == "running" || current.status == "queued"))
                    throw new PortalException("busy", "Another sequence is running. No new sequence was started.");
                if (String.IsNullOrEmpty(connectionId)) throw new PortalException("invalid_request", "A live client connection is required.");
                IList<PortalCapability> capabilities = GetCapabilities();
                foreach (PortalStep step in sequence.steps)
                    if (!capabilities.Any(c => c.action == step.action && c.supported))
                        throw new PortalException("unsupported_action", step.action + " has not passed live validation. The sequence was not started.");
                List<ExecutionUnit> execution = PlanExecution(sequence, capabilities);
                Directory.CreateDirectory(sequence.workspace);
                var jobNew = new PortalJob {
                    job_id = Guid.NewGuid().ToString("N"), request_id = sequence.request_id, request_fingerprint = fingerprint,
                    workspace = sequence.workspace, status = "queued", message = "Sequence accepted.", current_step = -1,
                    created_utc = Timestamp(), updated_utc = Timestamp(), artifacts = new List<string>(),
                    steps = sequence.steps.Select((s, i) => new PortalStepReceipt { index = i, action = s.action, args = s.args, status = "pending" }).ToList()
                };
                foreach (ExecutionUnit unit in execution.Where(u => u.Batch != null))
                    foreach (PortalStepReceipt receipt in jobNew.steps.Skip(unit.Start).Take(unit.Count))
                    { receipt.batch_id = unit.Batch.batch_id; receipt.batch_start_index = unit.Start; receipt.batch_step_count = unit.Count; }
                Persist(jobNew); // Never enqueue a mutation until the durable receipt exists.
                jobs.Add(jobNew.job_id, jobNew); requestJobs.Add(jobNew.request_id, jobNew.job_id);
                current = jobNew; owner = connectionId; leaseUntil = now().AddSeconds(LeaseSeconds); stopStatus = null;
                var worker = new Thread(() => Run(jobNew, sequence, execution));
                worker.IsBackground = true; worker.Name = "Buddy portal job"; worker.SetApartmentState(ApartmentState.MTA); worker.Start();
                return Clone(jobNew);
            }
        }
        public PortalJob GetJob(string jobId)
        {
            lock (gate)
            { CheckLeaseLocked(); PortalJob job; if (!jobs.TryGetValue(jobId ?? "", out job)) throw new PortalException("not_found", "The job does not exist."); return Clone(job); }
        }
        public PortalJob StopJob(string jobId)
        {
            lock (gate)
            {
                PortalJob job; if (!jobs.TryGetValue(jobId ?? "", out job)) throw new PortalException("not_found", "The job does not exist.");
                if (ReferenceEquals(current, job)) RequestStop("stopped", "Stop requested. Completed steps remain applied; an in-flight operation may still finish.");
                return Clone(job);
            }
        }
        public void Heartbeat(string connectionId)
        {
            lock (gate)
            {
                CheckLeaseLocked();
                if (connectionId == owner && current != null && !current.stop_requested) leaseUntil = now().AddSeconds(LeaseSeconds);
            }
        }
        public void NotifyDisconnected(string connectionId)
        {
            lock (gate)
            {
                try { if (connectionId == owner) RequestStop("paused", "The controlling connection closed. No further editing steps will start."); }
                catch { online = false; } // A full disk must not crash the IPC thread or permit more input.
            }
        }
        public void CheckLease()
        {
            lock (gate)
            {
                try { if (!disposed) CheckLeaseLocked(); }
                catch { online = false; } // Timer callbacks cannot let storage failures escape the process.
            }
        }
        private void CheckLeaseLocked()
        { if (current != null && (current.status == "running" || current.status == "queued") && now() >= leaseUntil) RequestStop("paused", "The controlling connection lease expired. No further editing steps will start."); }
        private void RequestStop(string status, string message)
        {
            if (current == null || (current.status != "running" && current.status != "queued") || current.stop_requested) return;
            current.stop_requested = true; current.message = message; stopStatus = status;
            try { Persist(current); } catch { online = false; throw; }
        }
        private bool Cancelled(PortalJob job)
        { lock (gate) { CheckLeaseLocked(); return disposed || !online || job.stop_requested || !ReferenceEquals(current, job); } }
        private void MarkInput(PortalJob job, IList<PortalStepReceipt> steps)
        {
            lock (gate)
            {
                if (Cancelled(job)) throw new OperationCanceledException("No input may begin after cancellation.");
                foreach (PortalStepReceipt step in steps) step.input_sent = true;
                Persist(job); // A batch records every original step in one durable receipt.
            }
        }
        private static bool BatchEligible(PortalStep step)
        { return step.action == "import_media" || step.action == "trim_clip" || step.action == "append_clip"; }
        private static string Mode(PortalCapability capability)
        { return String.IsNullOrEmpty(capability.execution_mode) ? "single" : capability.execution_mode; }
        private static string CanonicalJson(object value)
        { return Json().Serialize(PortalValidation.Canonical(Json().DeserializeObject(Json().Serialize(value)))); }
        private List<ExecutionUnit> PlanExecution(PortalSequence sequence, IList<PortalCapability> capabilities)
        {
            var units = new List<ExecutionUnit>();
            var batchRunner = runner as IPortalBatchActionRunner;
            for (int i = 0; i < sequence.steps.Count;)
            {
                PortalCapability capability = capabilities.First(c => c.action == sequence.steps[i].action);
                string mode = Mode(capability);
                if (mode != "single" && mode != "batch" && mode != "both")
                    throw new PortalException("unsupported_action", "The runner advertised an invalid execution mode.");
                int count = 0;
                while (i + count < sequence.steps.Count && BatchEligible(sequence.steps[i + count])) count++;
                bool batchModes = count > 0 && sequence.steps.Skip(i).Take(count).All(s => {
                    string m = Mode(capabilities.First(c => c.action == s.action)); return m == "batch" || m == "both";
                });
                if (batchRunner != null && batchModes)
                {
                    // Give the planner a copy, so preparation cannot alter the accepted sequence.
                    PortalBatchPlan plan = batchRunner.PlanBatch(Json().Deserialize<PortalSequence>(Json().Serialize(sequence)), i);
                    if (plan != null && plan.supported)
                    {
                        if (plan.start_index != i || plan.count != count || plan.steps == null || plan.steps.Count != count ||
                            CanonicalJson(plan.steps) != CanonicalJson(sequence.steps.Skip(i).Take(count).ToList()))
                            throw new PortalException("invalid_batch", "The batch plan must preserve the full contiguous group and every requested argument.");
                        if (String.IsNullOrWhiteSpace(plan.evidence))
                            throw new PortalException("unsupported_action", "The proposed native batch has no capability verification evidence.");
                        plan.batch_id = Guid.NewGuid().ToString("N");
                        // Use our private validated step copies, not mutable planner references.
                        plan.steps = Json().Deserialize<List<PortalStep>>(Json().Serialize(sequence.steps.Skip(i).Take(count).ToList()));
                        units.Add(new ExecutionUnit { Start = i, Count = count, Batch = plan }); i += count; continue;
                    }
                }
                if (mode == "batch")
                    throw new PortalException("unsupported_action", sequence.steps[i].action + " is available only in a verified native batch; no supported group was planned.");
                units.Add(new ExecutionUnit { Start = i, Count = 1 }); i++;
            }
            return units;
        }
        private void Run(PortalJob job, PortalSequence sequence, IList<ExecutionUnit> execution)
        {
            try
            {
                foreach (ExecutionUnit unit in execution)
                {
                    int i = unit.Start;
                    List<PortalStepReceipt> receipts = job.steps.Skip(i).Take(unit.Count).ToList();
                    bool batch = unit.Batch != null;
                    lock (gate)
                    {
                        if (Cancelled(job)) { FinishStopped(job); return; }
                        job.status = "running"; job.current_step = i;
                        job.message = batch ? "Running native batch for " + unit.Count + " requested editing steps." : "Running " + sequence.steps[i].action + ".";
                        string started = Timestamp();
                        foreach (PortalStepReceipt receipt in receipts) { receipt.status = "running"; receipt.started_utc = started; }
                        Persist(job);
                    }
                    PortalActionResult result;
                    try {
                        var context = new PortalExecutionContext(job.workspace, job.job_id, i, () => Cancelled(job), () => MarkInput(job, receipts));
                        result = batch ? ((IPortalBatchActionRunner)runner).ExecuteBatch(unit.Batch, context) : runner.Execute(sequence.steps[i], context);
                    }
                    catch (Exception ex)
                    { bool sent = receipts.Any(r => r.input_sent); result = new PortalActionResult { status = sent ? "uncertain_after_input" : "failed_before_input", message = ex.Message, input_sent = sent }; }
                    lock (gate)
                    {
                        bool inputSent = receipts.Any(r => r.input_sent) || (result != null && result.input_sent);
                        if (result == null) result = new PortalActionResult { status = inputSent ? "uncertain_after_input" : "failed_before_input", message = "The action worker returned no result." };
                        string[] outcomes = { "verified_success", "blocked", "unsupported", "failed_before_input", "uncertain_after_input" };
                        if (!outcomes.Contains(result.status)) result.status = inputSent ? "uncertain_after_input" : "failed_before_input";
                        result.input_sent = inputSent;
                        // A failure after input can never be asserted to be a before-input failure.
                        if (inputSent && result.status == "failed_before_input") result.status = "uncertain_after_input";
                        if (batch && result.status == "verified_success" &&
                            ((result.verification_kind != "native_roundtrip" && result.verification_kind != "independent_native") || result.evidence == null))
                        {
                            result.status = inputSent ? "uncertain_after_input" : "failed_before_input";
                            result.message = "The batch produced no independent native verification. Preparation alone does not complete the requested steps.";
                        }
                        if (batch && inputSent && Cancelled(job))
                        {
                            result.status = "uncertain_after_input";
                            result.message = "Stop or connection loss interrupted a native batch after input. Inspect the entire group; none of its steps will be replayed.";
                        }
                        string finished = Timestamp();
                        foreach (PortalStepReceipt receipt in receipts) {
                            receipt.input_sent = inputSent; receipt.result = result; receipt.status = result.status; receipt.finished_utc = finished;
                        }
                        if (result.artifacts != null)
                            foreach (string artifact in result.artifacts) {
                                string path = PortalValidation.UnderRoot(artifact, job.workspace, false);
                                if (!job.artifacts.Contains(path)) job.artifacts.Add(path);
                            }
                        if (result.status != "verified_success")
                        {
                            job.status = result.status == "uncertain_after_input" ? "uncertain" : result.status == "failed_before_input" ? "failed" : result.status;
                            job.message = result.message;
                            if (job.stop_requested && !inputSent) FinishStopped(job); else Persist(job);
                            return;
                        }
                        Persist(job);
                        if (Cancelled(job)) { FinishStopped(job); return; }
                    }
                }
                lock (gate) { job.status = "completed"; job.message = "Every requested editing step returned verified success."; Persist(job); }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    job.status = "uncertain"; job.stop_requested = true; job.message = "Coordinator stopped: " + ex.Message + ". Do not replay this request.";
                    try { Persist(job); } catch { online = false; }
                }
            }
        }
        private void FinishStopped(PortalJob job)
        { job.status = stopStatus ?? "stopped"; job.stop_requested = true; Persist(job); }
        private void Persist(PortalJob job)
        {
            job.updated_utc = Timestamp();
            string path = Path.Combine(receiptsDirectory, job.job_id + ".json");
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json().Serialize(job));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private void LoadReceipts()
        {
            foreach (string path in Directory.GetFiles(receiptsDirectory, "*.json"))
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new PortalException("receipt_error", "A stored job receipt exceeds its size limit.");
                PortalJob job;
                try { job = Json().Deserialize<PortalJob>(File.ReadAllText(path)); }
                catch { throw new PortalException("receipt_error", "A stored job receipt is unreadable. Existing jobs must be reconciled before editing."); }
                if (job == null || job.steps == null || String.IsNullOrEmpty(job.request_fingerprint)) throw new PortalException("receipt_error", "A stored job receipt is incomplete.");
                PortalValidation.Id(job.job_id, "job_id"); PortalValidation.Id(job.request_id, "request_id");
                if (!String.Equals(Path.GetFileNameWithoutExtension(path), job.job_id, StringComparison.Ordinal) || requestJobs.ContainsKey(job.request_id))
                    throw new PortalException("receipt_error", "Stored job identities conflict.");
                if (job.status == "running" || job.status == "queued")
                {
                    job.status = "uncertain"; job.stop_requested = true;
                    job.message = "Buddy restarted before a final receipt. Inspect MAGIX; this request will not be replayed.";
                    foreach (PortalStepReceipt step in job.steps.Where(s => s.status == "running")) step.status = "uncertain_after_input";
                    Persist(job);
                }
                jobs.Add(job.job_id, job); requestJobs.Add(job.request_id, job.job_id);
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                try { RequestStop("paused", "Buddy is shutting down. No further editing steps will start."); }
                finally { online = false; disposed = true; leaseTimer.Dispose(); }
            }
        }
    }
}
