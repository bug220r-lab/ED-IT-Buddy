using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using EditBuddy;

internal static class PortalCoreTests
{
    private static int assertions;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Error(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (PortalException ex) { Check(ex.Code == code, "Expected " + code + ", got " + ex.Code); }
    }
    private sealed class Runner : IPortalActionRunner
    {
        public int Calls;
        public bool Supported = true;
        public bool Input;
        public bool ThrowAfterInput;
        public ManualResetEvent Entered = new ManualResetEvent(false);
        public ManualResetEvent Continue = new ManualResetEvent(true);
        public ManualResetEvent StateEntered = new ManualResetEvent(false);
        public ManualResetEvent StateContinue = new ManualResetEvent(true);
        public object ReadState() { StateEntered.Set(); if (!StateContinue.WaitOne(5000)) throw new TimeoutException("Fake state read timed out."); return new { ready = true }; }
        public IList<PortalCapability> Capabilities() { return PortalValidation.Actions.Select(a => new PortalCapability { action = a, supported = Supported }).ToList(); }
        public PortalActionResult Execute(PortalStep step, PortalExecutionContext context)
        {
            Interlocked.Increment(ref Calls);
            if (Input) context.MarkInputSent();
            Entered.Set();
            if (!Continue.WaitOne(5000)) throw new Exception("Test runner did not resume.");
            if (ThrowAfterInput) throw new IOException("Simulated worker failure.");
            return new PortalActionResult { status = "verified_success", message = "Fake runner verified its operation.", input_sent = Input };
        }
    }
    private sealed class BatchRunner : IPortalActionRunner, IPortalBatchActionRunner
    {
        public int BatchCalls;
        public int SingleCalls;
        public bool PlanSupported = true;
        public bool InvalidPlan;
        public bool Input = true;
        public bool Proof = true;
        public string ResultStatus = "verified_success";
        public ManualResetEvent Entered = new ManualResetEvent(false);
        public ManualResetEvent Continue = new ManualResetEvent(true);
        public object ReadState() { return new { ready = true }; }
        public IList<PortalCapability> Capabilities()
        {
            return PortalValidation.Actions.Select(a => new PortalCapability {
                action = a, supported = true, execution_mode = Eligible(a) ? "batch" : "single", evidence = "Synthetic test fixture capability."
            }).ToList();
        }
        private static bool Eligible(string action) { return action == "import_media" || action == "trim_clip" || action == "append_clip"; }
        public PortalBatchPlan PlanBatch(PortalSequence sequence, int start)
        {
            int count = sequence.steps.Skip(start).TakeWhile(s => Eligible(s.action)).Count();
            if (count == 0) return null;
            return new PortalBatchPlan { start_index = start, count = InvalidPlan ? count - 1 : count,
                steps = sequence.steps.Skip(start).Take(count).ToList(), supported = PlanSupported, evidence = "Synthetic test fixture capability." };
        }
        public PortalActionResult ExecuteBatch(PortalBatchPlan plan, PortalExecutionContext context)
        {
            Interlocked.Increment(ref BatchCalls);
            if (Input) context.MarkInputSent();
            Entered.Set();
            if (!Continue.WaitOne(5000)) throw new Exception("Batch fixture did not resume.");
            return new PortalActionResult { status = ResultStatus, input_sent = Input,
                verification_kind = Proof ? "native_roundtrip" : null, evidence = Proof ? new { fixture = true } : null,
                message = "Synthetic native batch result for coordinator tests only." };
        }
        public PortalActionResult Execute(PortalStep step, PortalExecutionContext context)
        { Interlocked.Increment(ref SingleCalls); return new PortalActionResult { status = "verified_success", message = "Synthetic single action." }; }
    }
    private static PortalSequence ClipSequence(string root, string id)
    {
        string workspace = Path.Combine(root, "demo"); var steps = new List<PortalStep>();
        foreach (string clip in new[] { "a", "b" })
        {
            steps.Add(new PortalStep { action = "import_media", args = new Dictionary<string,object> { {"media_path",Path.Combine(workspace,clip+".avi")},{"clip_id",clip} } });
            steps.Add(new PortalStep { action = "trim_clip", args = new Dictionary<string,object> { {"clip_id",clip},{"source_in_frame",0},{"source_out_frame",60} } });
            steps.Add(new PortalStep { action = "append_clip", args = new Dictionary<string,object> { {"clip_id",clip},{"track",1} } });
        }
        steps.Add(new PortalStep { action = "save_project", args = new Dictionary<string,object> { {"project_path",Path.Combine(workspace,"demo.MVP")} } });
        return new PortalSequence { request_id = id, workspace = workspace, steps = steps };
    }
    private static void BatchTests(string root)
    {
        string batchRoot = Path.Combine(root, "batch-success"); var runner = new BatchRunner(); runner.Continue.Reset();
        using (var c = new PortalCoordinator(Path.Combine(batchRoot, "receipts"), Path.Combine(batchRoot,"workspaces"), runner))
        {
            c.SetOnline(true); var sequence = ClipSequence(Path.Combine(batchRoot,"workspaces"), "batch"); var job = c.Submit(sequence,"owner");
            Check(runner.Entered.WaitOne(2000), "Batch runner did not enter.");
            PortalJob inFlight = c.GetJob(job.job_id);
            Check(inFlight.steps.Take(6).All(s => s.input_sent && s.status == "running"), "Batch input intent was not applied to every original step.");
            var stored = new JavaScriptSerializer().Deserialize<PortalJob>(File.ReadAllText(Path.Combine(batchRoot,"receipts",job.job_id+".json")));
            Check(stored.steps.Take(6).All(s => s.input_sent) && !stored.steps[6].input_sent, "Batch input intent was not atomically durable before execution.");
            Check(inFlight.steps.Take(6).Select(s=>s.batch_id).Distinct().Count()==1 && !String.IsNullOrEmpty(inFlight.steps[0].batch_id) && inFlight.steps[6].batch_id==null, "Original step receipts lost their shared batch identity.");
            runner.Continue.Set(); PortalJob completed=Finish(c,job.job_id);
            Check(completed.status=="completed" && runner.BatchCalls==1 && runner.SingleCalls==1, "Native batch was executed as individual edits or failed to continue.");
            Check(completed.steps.Take(6).All(s=>s.status=="verified_success" && s.result.verification_kind=="native_roundtrip"), "One verified native result was not applied to the whole group.");
            c.Submit(sequence,"other"); Check(runner.BatchCalls==1, "Native batch was replayed on a duplicate request.");
        }
        foreach (bool disconnect in new[] {false,true})
        {
            string dir=Path.Combine(root,disconnect?"batch-disconnect":"batch-stop"); var r=new BatchRunner(); r.Continue.Reset();
            using(var c=new PortalCoordinator(Path.Combine(dir,"receipts"),Path.Combine(dir,"workspaces"),r))
            {
                c.SetOnline(true);var sequence=ClipSequence(Path.Combine(dir,"workspaces"),"cancel");var job=c.Submit(sequence,"owner");
                Check(r.Entered.WaitOne(2000),"Cancellable batch did not enter.");
                if(disconnect)c.NotifyDisconnected("owner");else c.StopJob(job.job_id);
                r.Continue.Set();var result=Finish(c,job.job_id);
                Check(result.status=="uncertain" && result.steps.Take(6).All(s=>s.status=="uncertain_after_input") && result.steps[6].status=="pending", "Cancellation after batch input did not mark the entire group uncertain.");
                Check(r.SingleCalls==0,"Cancellation allowed later save.");c.Submit(sequence,"other");Check(r.BatchCalls==1,"Uncertain batch was replayed.");
            }
        }
        foreach(bool prepared in new[]{false,true})
        {
            string dir=Path.Combine(root,prepared?"batch-prepared":"batch-no-proof");var r=new BatchRunner {Input=!prepared,Proof=false,ResultStatus=prepared?"prepared":"verified_success"};
            using(var c=new PortalCoordinator(Path.Combine(dir,"receipts"),Path.Combine(dir,"workspaces"),r))
            {
                c.SetOnline(true);var job=c.Submit(ClipSequence(Path.Combine(dir,"workspaces"),"unverified"),"owner");var result=Finish(c,job.job_id);
                Check(result.status==(prepared?"failed":"uncertain") && result.steps.Take(6).All(s=>s.status!="verified_success") && r.SingleCalls==0,"Preparation or an unproved batch was treated as verified editing.");
            }
        }
        foreach(bool invalid in new[]{false,true})
        {
            string dir=Path.Combine(root,invalid?"batch-invalid":"batch-unsupported");var r=new BatchRunner{PlanSupported=invalid,InvalidPlan=invalid};
            using(var c=new PortalCoordinator(Path.Combine(dir,"receipts"),Path.Combine(dir,"workspaces"),r))
            {
                c.SetOnline(true);Error(invalid?"invalid_batch":"unsupported_action",()=>c.Submit(ClipSequence(Path.Combine(dir,"workspaces"),"invalid"),"owner"));
                Check(r.BatchCalls==0 && r.SingleCalls==0 && Directory.GetFiles(Path.Combine(dir,"receipts"),"*.json").Length==0,"An invalid or unverified plan started editing.");
            }
        }
    }
    private sealed class PipePeer : IDisposable
    {
        internal NamedPipeClientStream Pipe;
        internal StreamReader Reader;
        internal StreamWriter Writer;
        internal PipePeer(string name)
        {
            Pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous); Pipe.Connect(2000);
            Reader = new StreamReader(Pipe,new UTF8Encoding(false),false,1024,true);
            Writer = new StreamWriter(Pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
        }
        internal string Request(string id, string method)
        {
            Writer.WriteLine("{\"id\":\""+id+"\",\"method\":\""+method+"\",\"params\":{}}");
            Task<string> read=Reader.ReadLineAsync(); if(!read.Wait(2000))throw new Exception("Pipe response timed out."); return read.Result;
        }
        internal bool Ended()
        {
            try { Task<string> read=Reader.ReadLineAsync(); if(!read.Wait(2500))return false; return read.Result==null; }
            catch(AggregateException ex){if(ex.InnerException is IOException || ex.InnerException is ObjectDisposedException)return true;throw;}
            catch(IOException){return true;}
        }
        public void Dispose(){try{Writer.Dispose();}catch{}try{Reader.Dispose();}catch{}Pipe.Dispose();}
    }
    private static void PipeCapacityTests(string root)
    {
        WithCoordinator(root,"pipe-capacity",(c,r,w)=>{
            c.SetOnline(true); string name="EditBuddy.Capacity.Tests."+Guid.NewGuid().ToString("N");
            using(var server=new PortalPipeServer(c,name,WindowsIdentity.GetCurrent().User.Value,10000))
            {
                server.Start();var peers=new List<PipePeer>();
                try
                {
                    for(int i=0;i<PortalPipeServer.MaximumClients;i++){var peer=new PipePeer(name);peers.Add(peer);Check(peer.Request("ping"+i,"ping").Contains("lease_seconds"),"Client capacity was exhausted before eight accepted clients.");}
                    Check(server.ActiveClientCount==8,"The accepted client count is wrong.");
                    using(var excess=new PipePeer(name))Check(excess.Ended(),"An excess client was neither bounded nor rejected.");
                    Check((bool)((Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(new JavaScriptSerializer().Serialize(c.GetState())))["online"],"Capacity rejection took the coordinator offline.");
                    peers[0].Dispose();peers.RemoveAt(0);
                    for(int i=0;i<100 && server.ActiveClientCount==8;i++)Thread.Sleep(10);
                    using(var recovered=new PipePeer(name))Check(recovered.Request("recovered","ping").Contains("lease_seconds"),"Listener did not recover after a capacity slot was freed.");
                }
                finally{foreach(var peer in peers)peer.Dispose();}
                for(int i=0;i<100 && server.ActiveClientCount!=0;i++)Thread.Sleep(10);
                for(int i=0;i<12;i++)using(var peer=new PipePeer(name))Check(peer.Request("reconnect"+i,"ping").Contains("lease_seconds"),"Repeated EOF/reconnect exhausted the listener.");
            }
        });
        WithCoordinator(root,"pipe-idle",(c,r,w)=>{
            c.SetOnline(true);string name="EditBuddy.Idle.Tests."+Guid.NewGuid().ToString("N");
            using(var server=new PortalPipeServer(c,name,WindowsIdentity.GetCurrent().User.Value,400))
            {
                server.Start();
                using(var idle=new PipePeer(name)){Check(idle.Request("idle","ping").Contains("lease_seconds"),"Idle test peer did not connect.");Check(idle.Ended(),"An idle pipe was not closed within its bound.");}
                for(int i=0;i<100 && server.ActiveClientCount!=0;i++)Thread.Sleep(10);
                Check(server.ActiveClientCount==0,"Idle client retained its server slot.");
                using(var active=new PipePeer(name)){
                    for(int i=0;i<5;i++){Thread.Sleep(150);Check(active.Request("keepalive"+i,"ping").Contains("lease_seconds"),"Periodic requests did not renew client idle lifetime.");}
                }
                using(var recovered=new PipePeer(name))Check(recovered.Request("after-idle","ping").Contains("lease_seconds"),"Listener did not accept clients after idle expiry.");
            }
        });
    }
    private static PortalSequence Sequence(string root, string id, int count)
    {
        string workspace = Path.Combine(root, "demo");
        return new PortalSequence { request_id = id, workspace = workspace, steps = Enumerable.Range(0, count).Select(i => new PortalStep {
            action = "save_project", args = new Dictionary<string, object> { { "project_path", Path.Combine(workspace, "demo.MVP") } }
        }).ToList() };
    }
    private static PortalJob Finish(PortalCoordinator coordinator, string id)
    {
        for (int i = 0; i < 200; i++) { PortalJob job = coordinator.GetJob(id); if (job.status != "queued" && job.status != "running") return job; Thread.Sleep(10); }
        throw new Exception("Job did not settle.");
    }
    private static void WithCoordinator(string root, string name, Action<PortalCoordinator, Runner, string> test)
    {
        string folder = Path.Combine(root, name); var runner = new Runner();
        using (var coordinator = new PortalCoordinator(Path.Combine(folder, "receipts"), Path.Combine(folder, "workspaces"), runner))
            test(coordinator, runner, Path.Combine(folder, "workspaces"));
    }
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        try
        {
            BatchTests(root);
            PipeCapacityTests(root);
            WithCoordinator(root, "validation", (c, r, w) => {
                Error("offline", () => c.Submit(Sequence(w, "offline", 1), "owner"));
                c.SetOnline(true);
                var bad = Sequence(w, "unknown", 1); bad.steps[0].action = "send_keys";
                Error("unsupported_action", () => c.Submit(bad, "owner"));
                var escape = Sequence(w, "escape", 1); escape.steps[0].args["project_path"] = Path.Combine(root, "original.MVP");
                Error("invalid_path", () => c.Submit(escape, "owner"));
                var trim = Sequence(w, "trim", 1); trim.steps[0] = new PortalStep { action = "trim_clip", args = new Dictionary<string,object> { {"clip_id","a"},{"source_in_frame",1.5},{"source_out_frame",60} } };
                Error("invalid_request", () => c.Submit(trim, "owner"));
                r.Supported = false; Error("unsupported_action", () => c.Submit(Sequence(w, "unsupported", 1), "owner"));
                Check(r.Calls == 0, "Validation must precede every operation.");
            });
            WithCoordinator(root, "dedupe", (c, r, w) => {
                c.SetOnline(true); var sequence = Sequence(w, "same", 1); PortalJob first = c.Submit(sequence, "owner");
                Check(Finish(c, first.job_id).status == "completed", "Supported sequence did not complete.");
                PortalJob repeat = c.Submit(sequence, "other"); Check(repeat.job_id == first.job_id && r.Calls == 1, "Duplicate request replayed.");
                sequence.steps.Add(sequence.steps[0]); Error("request_conflict", () => c.Submit(sequence, "owner"));
            });
            WithCoordinator(root, "stop", (c, r, w) => {
                r.Continue.Reset(); c.SetOnline(true); PortalJob first = c.Submit(Sequence(w, "stop", 2), "owner");
                Check(r.Entered.WaitOne(2000), "Runner did not enter.");
                Error("busy", () => c.Submit(Sequence(w, "busy", 1), "other"));
                c.NotifyDisconnected("other"); Check(!c.GetJob(first.job_id).stop_requested, "Unrelated client disconnected the owner job.");
                c.StopJob(first.job_id); r.Continue.Set();
                Check(Finish(c, first.job_id).status == "stopped" && r.Calls == 1, "Stop allowed another step.");
            });
            WithCoordinator(root, "disconnect", (c, r, w) => {
                r.Continue.Reset(); c.SetOnline(true); PortalJob first = c.Submit(Sequence(w, "disconnect", 2), "owner");
                Check(r.Entered.WaitOne(2000), "Runner did not enter."); c.NotifyDisconnected("owner"); r.Continue.Set();
                Check(Finish(c, first.job_id).status == "paused" && r.Calls == 1, "Connection loss allowed another step.");
            });
            WithCoordinator(root, "uncertain", (c, r, w) => {
                r.Input = true; r.ThrowAfterInput = true; c.SetOnline(true); PortalJob job = c.Submit(Sequence(w, "uncertain", 2), "owner");
                PortalJob result = Finish(c, job.job_id);
                Check(result.status == "uncertain" && result.steps[0].input_sent && r.Calls == 1, "After-input failure was not uncertain.");
                c.Submit(Sequence(w, "uncertain", 2), "owner"); Check(r.Calls == 1, "Uncertain result was replayed.");
            });
            DateTime clock = DateTime.UtcNow; string leaseRoot = Path.Combine(root, "lease"); var leaseRunner = new Runner(); leaseRunner.Continue.Reset();
            using (var c = new PortalCoordinator(Path.Combine(leaseRoot, "receipts"), Path.Combine(leaseRoot, "workspaces"), leaseRunner, () => clock))
            {
                c.SetOnline(true); PortalJob job = c.Submit(Sequence(Path.Combine(leaseRoot, "workspaces"), "lease", 2), "owner");
                Check(leaseRunner.Entered.WaitOne(2000), "Runner did not enter."); clock = clock.AddSeconds(10); c.Heartbeat("owner"); clock = clock.AddSeconds(10); c.CheckLease();
                Check(!c.GetJob(job.job_id).stop_requested, "Owner heartbeat did not renew the lease.");
                clock = clock.AddSeconds(6); c.Heartbeat("other"); c.CheckLease(); leaseRunner.Continue.Set();
                Check(Finish(c, job.job_id).status == "paused" && leaseRunner.Calls == 1, "Expired lease allowed another step.");
            }
            string persistent = Path.Combine(root, "dedupe"); var restoredRunner = new Runner();
            using (var c = new PortalCoordinator(Path.Combine(persistent, "receipts"), Path.Combine(persistent, "workspaces"), restoredRunner))
            { c.SetOnline(true); Check(c.Submit(Sequence(Path.Combine(persistent, "workspaces"), "same", 1), "new").status == "completed" && restoredRunner.Calls == 0, "Restart lost idempotency receipt."); }
            WithCoordinator(root, "pipe", (c, r, w) => {
                string name = "EditBuddy.Portal.Tests." + Guid.NewGuid().ToString("N");
                using (var server = new PortalPipeServer(c, name, WindowsIdentity.GetCurrent().User.Value))
                {
                    server.Start();
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                    {
                        string pipeJobId;
                        try { client.Connect(2000); } catch { Console.Error.WriteLine("Pipe listener error: " + server.LastError); throw; }
                        using (var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, true))
                        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            writer.WriteLine("{\"id\":\"probe\",\"method\":\"get_state\",\"params\":{}}");
                            string state = reader.ReadLine(); Check(state.Contains("\"online\":false") && state.Contains("\"error\":null"), "Local pipe state roundtrip failed.");
                            writer.WriteLine("{\"id\":\"bad\",\"method\":\"send_keys\",\"params\":{}}");
                            Check(reader.ReadLine().Contains("unknown_method"), "Pipe accepted an unknown method.");
                            writer.WriteLine("{\"id\":\"extra\",\"method\":\"get_state\",\"params\":{\"extra\":true}}");
                            Check(reader.ReadLine().Contains("invalid_request"), "Pipe accepted extra fields.");
                            c.SetOnline(true); r.Continue.Reset();
                            var json = new JavaScriptSerializer();
                            writer.WriteLine(json.Serialize(new { id = "run", method = "run_sequence", @params = Sequence(w, "pipe-job", 2) }));
                            var response = json.Deserialize<Dictionary<string, object>>(reader.ReadLine());
                            Check(response["error"] == null, "Pipe rejected a valid sequence.");
                            pipeJobId = ((Dictionary<string, object>)response["result"])["job_id"].ToString();
                            Check(r.Entered.WaitOne(2000), "Pipe job did not enter runner.");
                            r.StateEntered.Reset(); r.StateContinue.Reset();
                            writer.WriteLine("{\"id\":\"slow-state\",\"method\":\"get_state\",\"params\":{}}");
                            Check(r.StateEntered.WaitOne(2000), "State query did not enter its slow runner.");
                            writer.WriteLine("{\"id\":\"live-ping\",\"method\":\"ping\",\"params\":{}}");
                            Check(reader.ReadLine().Contains("live-ping"), "A blocked state query delayed the heartbeat.");
                            writer.WriteLine(json.Serialize(new { id = "live-stop", method = "stop_job", @params = new { job_id = pipeJobId } }));
                            Check(reader.ReadLine().Contains("live-stop"), "A blocked state query delayed Stop.");
                            r.StateContinue.Set(); Check(reader.ReadLine().Contains("slow-state"), "State response lost its request correlation.");
                        }
                        client.Dispose();
                        for (int i = 0; i < 100 && !c.GetJob(pipeJobId).stop_requested; i++) Thread.Sleep(10);
                        r.Continue.Set(); Check(Finish(c, pipeJobId).status == "stopped" && r.Calls == 1, "Pipe Stop allowed another action.");
                    }
                    r.Entered.Reset(); r.Continue.Reset();
                    string disconnectedJob;
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                    {
                        client.Connect(2000);
                        using (var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, true))
                        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            var json = new JavaScriptSerializer();
                            writer.WriteLine(json.Serialize(new { id = "disconnect-run", method = "run_sequence", @params = Sequence(w, "pipe-disconnect", 2) }));
                            var response = json.Deserialize<Dictionary<string, object>>(reader.ReadLine());
                            Check(response["error"] == null, "The second pipe job was rejected.");
                            disconnectedJob = ((Dictionary<string, object>)response["result"])["job_id"].ToString();
                            Check(r.Entered.WaitOne(2000), "The second pipe job did not enter.");
                        }
                    }
                    for (int i = 0; i < 100 && !c.GetJob(disconnectedJob).stop_requested; i++) Thread.Sleep(10);
                    r.Continue.Set(); Check(Finish(c, disconnectedJob).status == "paused" && r.Calls == 2, "Real pipe EOF did not revoke the owner lease.");
                }
            });
            Console.WriteLine("PASS: " + assertions + " portal coordinator assertions."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
