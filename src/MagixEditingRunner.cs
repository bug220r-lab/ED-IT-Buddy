using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace EditBuddy
{
    // UI Automation providers can hang. All provider calls live in an expendable
    // child, never in the pipe listener, coordinator, or WPF dispatcher.
    public sealed class MagixEditingRunner : IPortalActionRunner
    {
        private readonly string workerPath;
        public MagixEditingRunner() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ED-IT Buddy.EditorWorker.exe")) { }
        public MagixEditingRunner(string path) { workerPath = Path.GetFullPath(path); }

        public object ReadState()
        {
            WorkerReply reply = Run(new EditingWorkerRequest { method = "state" }, null, 6500);
            if (reply.state != null) return reply.state;
            return new Dictionary<string, object> { { "ready", false }, { "host", Environment.MachineName }, { "reason", reply.result == null ? "No editor worker result." : reply.result.message } };
        }

        public IList<PortalCapability> Capabilities()
        {
            return EditingCommandPolicy.Capabilities();
        }

        public PortalActionResult Execute(PortalStep step, PortalExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            string error = EditingCommandPolicy.Validate(step.action, step.args, context.Workspace);
            if (error != null) return Failure("failed_before_input", error, false);
            var request = new EditingWorkerRequest {
                method = "execute", action = step.action, args = step.args,
                workspace = context.Workspace, job_id = context.JobId, step_index = context.StepIndex
            };
            WorkerReply reply = Run(request, context, step.action == "export_video" ? 180000 : 45000);
            return reply.result ?? Failure("uncertain_after_input", "The editor worker returned no action result.", true);
        }

        private WorkerReply Run(EditingWorkerRequest request, PortalExecutionContext context, int deadline)
        {
            if (!File.Exists(workerPath)) return new WorkerReply { result = Failure("failed_before_input", "The bounded MAGIX editor worker is missing.", false) };
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            bool inputSent = false;
            WorkerReply final = null;
            string protocolError = null;
            var sync = new object();
            var stderr = new StringBuilder();
            using (var process = new Process())
            using (var lifetime = new EditingWorkerLifetime())
            {
                process.StartInfo = new ProcessStartInfo {
                    FileName = workerPath, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(workerPath)
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    lock (sync)
                    {
                        if (e.Data.Length > 1024 * 1024) { protocolError = "Editor worker output exceeded its bound."; return; }
                        try
                        {
                            var message = serializer.Deserialize<WorkerReply>(e.Data);
                            if (message == null) { protocolError = "Empty editor worker message."; return; }
                            if (message.kind == "before_input" && final == null && !inputSent && context != null)
                            {
                                context.ThrowIfCancellationRequested();
                                context.MarkInputSent(); // Persist intent before granting input.
                                inputSent = true;
                                context.ThrowIfCancellationRequested();
                                process.StandardInput.WriteLine("permit_input"); process.StandardInput.Flush();
                            }
                            else if (message.kind == "result" && final == null) final = message;
                            else protocolError = "Unexpected or duplicate editor worker message.";
                        }
                        catch (Exception) { protocolError = "Editor input was denied or worker protocol failed."; try { process.StandardInput.Close(); } catch { } }
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (sync) if (stderr.Length < 4096) stderr.AppendLine(e.Data); };
                try
                {
                    process.Start(); lifetime.Attach(process.Handle); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    process.StandardInput.WriteLine(serializer.Serialize(request)); process.StandardInput.Flush();
                    var clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(50))
                    {
                        if (clock.ElapsedMilliseconds > deadline || protocolError != null || (context != null && context.CancellationRequested))
                        {
                            try { process.Kill(); process.WaitForExit(1000); } catch { }
                            lock (sync)
                            {
                                string reason = clock.ElapsedMilliseconds > deadline ? "The MAGIX worker deadline expired." : "Stop or connection loss interrupted the worker.";
                                return new WorkerReply { result = Failure(inputSent ? "uncertain_after_input" : "failed_before_input", reason + " MAGIX was left open; no action will be replayed automatically.", inputSent) };
                            }
                        }
                    }
                    // Flush asynchronous stdout events after process exit.
                    process.WaitForExit();
                    lock (sync)
                    {
                        if (protocolError != null || final == null || process.ExitCode != 0)
                            return new WorkerReply { result = Failure(inputSent ? "uncertain_after_input" : "failed_before_input", protocolError ?? ("Editor worker failed: " + stderr.ToString().Trim()), inputSent) };
                        if (final.result != null && final.result.input_sent && !inputSent)
                            return new WorkerReply { result = Failure("uncertain_after_input", "Worker claimed input without the required durable coordinator permit.", true) };
                        if (final.result != null) final.result.input_sent = final.result.input_sent || inputSent;
                        return final;
                    }
                }
                catch (Exception e)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    return new WorkerReply { result = Failure(inputSent ? "uncertain_after_input" : "failed_before_input", e.Message, inputSent) };
                }
            }
        }

        internal static PortalActionResult Failure(string status, string message, bool sent)
        {
            return new PortalActionResult { status = status, message = message, input_sent = sent, artifacts = new List<string>() };
        }
    }

    public sealed class EditingWorkerRequest
    {
        public string method { get; set; }
        public string action { get; set; }
        public Dictionary<string, object> args { get; set; }
        public string workspace { get; set; }
        public string job_id { get; set; }
        public int step_index { get; set; }
    }

    public sealed class WorkerReply
    {
        public string kind { get; set; }
        public object state { get; set; }
        public PortalActionResult result { get; set; }
    }
}
