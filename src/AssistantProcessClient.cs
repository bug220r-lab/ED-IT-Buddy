using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EditBuddy
{
    // This process is the local task broker. Closing it does not cancel MSI jobs.
    internal sealed class AssistantProcessClient : IDisposable
    {
        private sealed class Pending
        {
            internal readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Timer Timeout;
        }

        private readonly object gate = new object();
        private readonly object writerGate = new object();
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        private readonly Process process;
        private readonly StreamWriter input;
        private bool disposed;
        private bool failed;
        private string lastDiagnostic = "";
        internal event Action<Dictionary<string, object>> Notification;
        internal event Action<string> Disconnected;

        internal AssistantProcessClient(string root)
        {
            string script = Path.Combine(Path.GetFullPath(root), "assistant", "client.mjs");
            if (!File.Exists(script)) throw new FileNotFoundException("The Buddy assistant backend is missing. Keep the assistant folder beside the application package.", script);
            process = new Process { StartInfo = new ProcessStartInfo {
                FileName = FindNode(root), Arguments = Quote(script) + " --rpc", WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }, EnableRaisingEvents = true };
            process.Exited += delegate { FailAll("Buddy's local task broker stopped." + (String.IsNullOrWhiteSpace(lastDiagnostic) ? "" : " " + lastDiagnostic)); };
            try { if (!process.Start()) throw new InvalidOperationException("The Buddy task broker could not start."); }
            catch { process.Dispose(); throw; }
            // Framework Process.StandardInput uses the Windows console code page.
            // The broker protocol is UTF-8 regardless of the current locale.
            input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            Task.Run((Action)ReadOutput);
            Task.Run((Action)ReadDiagnostics);
        }

        internal bool IsRunning
        {
            get { lock (gate) { try { return !disposed && !failed && !process.HasExited; } catch { return false; } } }
        }

        internal Task<object> RequestAsync(string method, object parameters, int timeoutMilliseconds = 120000)
        {
            string id = Guid.NewGuid().ToString("N");
            Pending item = new Pending();
            string line = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Serialize(new { id = id, method = method, @params = parameters ?? new object() });
            lock (gate)
            {
                if (disposed || !IsRunning) throw new InvalidOperationException("Connect Buddy before starting this operation.");
                pending.Add(id, item);
                item.Timeout = new Timer(delegate { CompleteError(id, "The task broker did not reply in time. Refresh task status before repeating work."); }, null, timeoutMilliseconds, Timeout.Infinite);
            }
            ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    lock (writerGate)
                    {
                        lock (gate) { if (disposed || !pending.ContainsKey(id)) return; }
                        input.WriteLine(line); input.Flush();
                    }
                }
                catch (Exception error) { CompleteError(id, "Could not contact the task broker: " + error.Message); }
            });
            return item.Completion.Task;
        }

        private void ReadOutput()
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("The task broker returned an oversized response.");
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    Dictionary<string, object> message = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.DeserializeObject(line) as Dictionary<string, object>;
                    if (message == null) throw new InvalidDataException("The task broker returned an invalid response.");
                    object idValue;
                    if (!message.TryGetValue("id", out idValue) || idValue == null)
                    {
                        Action<Dictionary<string, object>> handler = Notification;
                        if (handler != null) { try { handler(message); } catch { } }
                        continue;
                    }
                    Pending item = Take(Convert.ToString(idValue, System.Globalization.CultureInfo.InvariantCulture));
                    if (item == null) continue;
                    object error;
                    if (message.TryGetValue("error", out error) && error != null)
                    {
                        Dictionary<string, object> detail = error as Dictionary<string, object>;
                        object description;
                        item.Completion.TrySetException(new InvalidOperationException(detail != null && detail.TryGetValue("message", out description) ? Convert.ToString(description) : Convert.ToString(error)));
                    }
                    else { object result; message.TryGetValue("result", out result); item.Completion.TrySetResult(result); }
                }
                FailAll("Buddy's local task broker disconnected. MSI work may still be running; reconnect to retrieve its receipts.");
            }
            catch (Exception error) { FailAll("The task broker connection failed: " + error.Message); }
        }

        private void ReadDiagnostics()
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                    if (!String.IsNullOrWhiteSpace(line)) lastDiagnostic = line.Length > 1200 ? line.Substring(0, 1200) : line;
            }
            catch { }
        }

        private Pending Take(string id)
        {
            lock (gate)
            {
                Pending item;
                if (!pending.TryGetValue(id, out item)) return null;
                pending.Remove(id); if (item.Timeout != null) item.Timeout.Dispose(); return item;
            }
        }

        private void CompleteError(string id, string message)
        { Pending item = Take(id); if (item != null) item.Completion.TrySetException(new InvalidOperationException(message)); }

        private void FailAll(string message)
        {
            List<Pending> items;
            bool notify;
            lock (gate) { notify = !failed; failed = true; items = new List<Pending>(pending.Values); pending.Clear(); }
            foreach (Pending item in items) { if (item.Timeout != null) item.Timeout.Dispose(); item.Completion.TrySetException(new InvalidOperationException(message)); }
            Action<string> handler = Disconnected; if (notify && handler != null) { try { handler(message); } catch { } }
        }

        internal static string FindNode(string root)
        {
            string configured = Environment.GetEnvironmentVariable("EDIT_BUDDY_NODE");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured) || !String.Equals(Path.GetExtension(configured), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("EDIT_BUDDY_NODE must point to an existing node.exe.");
                return configured;
            }
            foreach (string candidate in new[] {
                Path.Combine(root, "runtime", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe") })
                if (File.Exists(candidate)) return candidate;
            return "node.exe";
        }

        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new InvalidDataException("Invalid assistant executable path.");
            return "\"" + value + "\"";
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            FailAll("Buddy is closing. MSI background jobs remain independent.");
            // EOF lets the broker detach cleanly; bounded fallback terminates only this local child.
            Task.Run(delegate { try { lock (writerGate) { input.Close(); } } catch { } });
            Task.Run(async delegate {
                try { await Task.Delay(2500); if (!process.HasExited) process.Kill(); } catch { }
                try { process.Dispose(); } catch { }
            });
        }
    }
}
