using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace EditBuddy
{
    public sealed class PortalPipeServer : IDisposable
    {
        private readonly PortalCoordinator coordinator;
        private readonly string pipeName;
        private readonly SecurityIdentifier accountSid;
        private readonly object gate = new object();
        private readonly List<NamedPipeServerStream> streams = new List<NamedPipeServerStream>();
        private readonly HashSet<NamedPipeServerStream> clients = new HashSet<NamedPipeServerStream>();
        private readonly int idleTimeoutMilliseconds;
        private bool stopped;
        private bool started;
        public string PipeName { get { return pipeName; } }
        public string LastError { get; private set; }
        public const int MaximumClients = 8;
        public const int DefaultIdleTimeoutMilliseconds = 30000;
        public int ActiveClientCount { get { lock (gate) { return clients.Count; } } }
        public PortalPipeServer(PortalCoordinator coordinator, string pipeName, string allowedAccountSid)
            : this(coordinator, pipeName, allowedAccountSid, DefaultIdleTimeoutMilliseconds) { }
        public PortalPipeServer(PortalCoordinator coordinator, string pipeName, string allowedAccountSid, int idleTimeoutMilliseconds)
        {
            if (coordinator == null) throw new ArgumentNullException("coordinator");
            if (String.IsNullOrEmpty(pipeName) || pipeName.Length > 200 || pipeName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new ArgumentException("A local pipe name is required.");
            this.coordinator = coordinator; this.pipeName = pipeName; accountSid = new SecurityIdentifier(allowedAccountSid);
            if (idleTimeoutMilliseconds < 100 || idleTimeoutMilliseconds > 300000) throw new ArgumentOutOfRangeException("idleTimeoutMilliseconds");
            this.idleTimeoutMilliseconds = idleTimeoutMilliseconds;
            if (!accountSid.Equals(WindowsIdentity.GetCurrent().User)) throw new InvalidOperationException("The portal pipe must belong to its running account.");
        }
        public static string PipeNameForSession(string sid, int sessionId)
        {
            if (sessionId <= 0) throw new InvalidOperationException("The UI portal must run in an interactive session, not session zero.");
            return "EditBuddy.Portal." + new SecurityIdentifier(sid).Value + "." + sessionId;
        }
        public void Start()
        {
            lock (gate)
            {
                if (started || stopped) throw new InvalidOperationException("The pipe server cannot be started twice.");
                // Create synchronously: an ACL or ownership error must fail startup, not hide in a thread.
                NamedPipeServerStream listener = CreatePipe(); streams.Add(listener); started = true;
                var thread = new Thread(() => Listen(listener)); thread.IsBackground = true; thread.Name = "Buddy portal IPC"; thread.Start();
            }
        }
        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(accountSid);
            security.AddAccessRule(new PipeAccessRule(accountSid, PipeAccessRights.FullControl, AccessControlType.Allow));
            byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
            GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pinned.AddrOfPinnedObject(), Inherit = false };
                // Byte mode, local clients only. The account ACL allows this user's separate SSH logon.
                SafePipeHandle handle = CreateNamedPipe("\\\\.\\pipe\\" + pipeName, 0x00000003 | 0x40000000,
                    // The application caps accepted clients at eight. Native 255
                    // leaves room for the listener and handles being cancelled.
                    0x00000008, 255, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error, "Unable to create the local portal pipe."); }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { pinned.Free(); }
        }
        private void Listen(NamedPipeServerStream listener)
        {
            while (true)
            {
                lock (gate) { if (stopped) return; }
                if (listener == null)
                {
                    try
                    {
                        lock (gate) { if (stopped) return; listener = CreatePipe(); streams.Add(listener); }
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        // Capacity and transient OS errors must not permanently
                        // stop the listener or disconnect healthy job owners.
                        Thread.Sleep(100); continue;
                    }
                }
                try { listener.WaitForConnection(); }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    lock (gate) { if (stopped) return; }
                    Remove(listener); listener = null; continue;
                }
                NamedPipeServerStream connected = listener;
                listener = null;
                bool accepted;
                lock (gate)
                {
                    accepted = !stopped && clients.Count < MaximumClients;
                    if (accepted) clients.Add(connected);
                }
                if (!accepted) { Remove(connected); continue; }
                var handler = new Thread(() => Serve(connected)); handler.IsBackground = true; handler.Name = "Buddy portal client"; handler.Start();
            }
        }
        private void Serve(NamedPipeServerStream pipe)
        {
            string connectionId = Guid.NewGuid().ToString("N");
            object writeGate = new object();
            int stateRunning = 0;
            long lastActivity = Stopwatch.GetTimestamp();
            int ended = 0;
            Timer idleTimer = new Timer(delegate {
                double idle = (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastActivity)) * 1000.0 / Stopwatch.Frequency;
                if (idle >= idleTimeoutMilliseconds && Interlocked.Exchange(ref ended, 1) == 0)
                { coordinator.NotifyDisconnected(connectionId); Remove(pipe); }
            }, null, Math.Min(1000, idleTimeoutMilliseconds), Math.Min(1000, Math.Max(50, idleTimeoutMilliseconds / 4)));
            try
            {
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    while (pipe.IsConnected)
                    {
                        string line = ReadBoundedLine(reader);
                        if (line == null) break;
                        if (line.Length == 0) continue;
                        Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                        string stateRequestId;
                        if (IsStateRequest(line, out stateRequestId))
                        {
                            if (Interlocked.CompareExchange(ref stateRunning, 1, 0) != 0)
                            {
                                var json = new JavaScriptSerializer();
                                lock (writeGate) writer.WriteLine(json.Serialize(new { id = stateRequestId, result = (object)null,
                                    error = new { code = "busy", message = "A state snapshot is already in progress on this connection." } }));
                                continue;
                            }
                            string stateLine = line;
                            ThreadPool.QueueUserWorkItem(delegate {
                                bool released = false;
                                try {
                                    string response = Dispatch(stateLine, connectionId);
                                    Interlocked.Exchange(ref stateRunning, 0); released = true;
                                    lock (writeGate) writer.WriteLine(response);
                                }
                                catch { /* A disconnected reader no longer needs the snapshot. */ }
                                finally { if (!released) Interlocked.Exchange(ref stateRunning, 0); }
                            });
                        }
                        else { string response = Dispatch(line, connectionId); lock (writeGate) writer.WriteLine(response); }
                    }
                }
            }
            catch (Exception) { /* The controlling lease is revoked below, including malformed/closed streams. */ }
            finally { Interlocked.Exchange(ref ended, 1); idleTimer.Dispose(); coordinator.NotifyDisconnected(connectionId); Remove(pipe); }
        }
        private static bool IsStateRequest(string line, out string id)
        {
            id = null;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 };
                var request = json.DeserializeObject(line) as Dictionary<string, object>;
                object method, value;
                if (request == null || !request.TryGetValue("method", out method) || !String.Equals(method as string, "get_state", StringComparison.Ordinal) || !request.TryGetValue("id", out value)) return false;
                id = value as string; return id != null && id.Length <= 100;
            }
            catch { return false; }
        }
        private void Remove(NamedPipeServerStream pipe)
        { lock (gate) { streams.Remove(pipe); clients.Remove(pipe); } ClosePipe(pipe); }
        private static void ClosePipe(NamedPipeServerStream pipe)
        {
            // Dispose alone can leave a synchronous reader holding a handle ref;
            // cancel its overlapped I/O before releasing the pipe.
            try { if (!pipe.SafePipeHandle.IsClosed) CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); } catch { }
            try { pipe.Dispose(); } catch { }
        }
        public string Dispatch(string line, string connectionId)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 };
            object requestId = null;
            try
            {
                var request = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (request == null || !request.ContainsKey("id") || !request.ContainsKey("method") || !request.ContainsKey("params") || request.Keys.Any(k => k != "id" && k != "method" && k != "params"))
                    throw new PortalException("invalid_request", "Expected id, method, and params.");
                requestId = request["id"];
                if (!(requestId is string) || ((string)requestId).Length > 100) throw new PortalException("invalid_request", "Request id must be bounded text.");
                string method = request["method"] as string;
                var parameters = request["params"] as Dictionary<string, object>;
                if (parameters == null) throw new PortalException("invalid_request", "params must be an object.");
                object result;
                switch (method)
                {
                    case "ping":
                        RequireKeys(parameters); coordinator.Heartbeat(connectionId); result = new { lease_seconds = PortalCoordinator.LeaseSeconds }; break;
                    case "get_state":
                        RequireKeys(parameters); result = coordinator.GetState(); break;
                    case "get_capabilities":
                        RequireKeys(parameters); result = coordinator.GetCapabilities(); break;
                    case "run_sequence":
                        RequireKeys(parameters, "request_id", "workspace", "steps");
                        var wireSteps = parameters["steps"] as object[];
                        if (wireSteps == null) throw new PortalException("invalid_request", "steps must be an array.");
                        foreach (object item in wireSteps)
                        {
                            var step = item as Dictionary<string, object>;
                            if (step == null) throw new PortalException("invalid_request", "Each step must be an object.");
                            RequireKeys(step, "action", "args");
                        }
                        result = coordinator.Submit(serializer.ConvertToType<PortalSequence>(parameters), connectionId); break;
                    case "get_job":
                        RequireKeys(parameters, "job_id"); result = coordinator.GetJob(PortalValidation.Text(parameters, "job_id", 80)); break;
                    case "stop_job":
                        RequireKeys(parameters, "job_id"); result = coordinator.StopJob(PortalValidation.Text(parameters, "job_id", 80)); break;
                    default: throw new PortalException("unknown_method", "This portal method is not supported.");
                }
                return serializer.Serialize(new { id = requestId, result = result, error = (object)null });
            }
            catch (Exception ex)
            {
                var known = ex as PortalException;
                return serializer.Serialize(new { id = requestId, result = (object)null, error = new {
                    code = known == null ? "internal_error" : known.Code,
                    message = known == null ? "The portal could not complete this request: " + ex.Message : ex.Message
                } });
            }
        }
        private static void RequireKeys(IDictionary<string, object> value, params string[] keys)
        { if (value.Count != keys.Length || keys.Any(k => !value.ContainsKey(k))) throw new PortalException("invalid_request", "Unexpected or missing request fields."); }
        private static string ReadBoundedLine(TextReader reader)
        {
            var line = new StringBuilder();
            while (true)
            {
                int next = reader.Read();
                if (next < 0) return line.Length == 0 ? null : line.ToString();
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= 1024 * 1024) throw new IOException("Portal request exceeded its size limit.");
                line.Append((char)next);
            }
        }
        public void Dispose()
        {
            NamedPipeServerStream[] copy;
            lock (gate) { if (stopped && streams.Count == 0) return; stopped = true; copy = streams.ToArray(); streams.Clear(); clients.Clear(); }
            foreach (NamedPipeServerStream stream in copy) ClosePipe(stream);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outBufferSize, uint inBufferSize, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIoEx(SafePipeHandle handle, IntPtr overlapped);
    }
}
