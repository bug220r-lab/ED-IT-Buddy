using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EditBuddy
{
    internal static class PortalMain
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                bool host = Array.IndexOf(args, "--host") >= 0;
                if (host && !String.Equals(Environment.MachineName, "MSI", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The editing host is restricted to MSI. Use remote mode on this computer.");
                if (host && Process.GetCurrentProcess().SessionId == 0)
                    throw new InvalidOperationException("Start the host in jlewi's interactive desktop, not the SSH session.");
                string sid = WindowsIdentity.GetCurrent().User.Value;
                int session = Process.GetCurrentProcess().SessionId;
                bool owns;
                using (Mutex singleton = new Mutex(true, "Local\\ED_IT_Buddy_Portal_" + (host ? "Host_" : "Remote_") + sid + "_" + session, out owns))
                {
                    if (!owns) throw new InvalidOperationException("The Buddy portal is already running in this desktop session.");
                    try
                    {
                        Application app = new Application();
                        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        app.Run(new PortalWindow(host));
                    }
                    finally { singleton.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ED-IT Buddy", "portal", "state");
                Directory.CreateDirectory(state);
                File.AppendAllText(Path.Combine(state, "startup-errors.log"), DateTime.UtcNow.ToString("o") + " " + ex + Environment.NewLine);
                return 1;
            }
        }
    }

    internal sealed class PortalWindow : Window
    {
        private readonly bool host;
        private readonly TextBlock connection = new TextBlock();
        private readonly TextBlock detail = new TextBlock();
        private readonly TextBox report = new TextBox();
        private readonly Button connect = new Button();
        private readonly Button stop = new Button();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
        private PortalCoordinator coordinator;
        private PortalPipeServer server;
        private PortalRemoteClient client;
        private string activeJob;
        private bool refreshing;
        private bool closed;
        private int connectionGeneration;

        internal PortalWindow(bool isHost)
        {
            host = isHost;
            Title = host ? "ED-IT Buddy · MSI editing host" : "ED-IT Buddy · MSI portal";
            Width = 560; Height = 680; MinWidth = 430; MinHeight = 450;
            ShowActivated = false;
            MatrixTheme.Apply(this);
            FontSize = 14;
            DockPanel root = new DockPanel { Margin = new Thickness(20) }; Content = root;
            StackPanel header = new StackPanel();
            Border headerFrame = MatrixTheme.Header(header); headerFrame.Margin = new Thickness(0, 0, 0, 18);
            DockPanel.SetDock(headerFrame, Dock.Top); root.Children.Add(headerFrame);
            header.Children.Add(MatrixTheme.Eyebrow(host ? "MSI // EDITING HOST" : "MSI // EDITING PORTAL"));
            header.Children.Add(new TextBlock { Text = "ED-IT Buddy", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = MatrixTheme.Accent });
            header.Children.Add(new TextBlock { Text = host ? "Editing runs on this MSI desktop" : "MAGIX on MSI · this computer stays free", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 20), Foreground = MatrixTheme.Muted });
            connection.Text = "Connecting…"; connection.FontSize = 18; connection.FontWeight = FontWeights.SemiBold;
            connection.Foreground = MatrixTheme.Accent; connection.TextWrapping = TextWrapping.Wrap; header.Children.Add(connection);
            detail.TextWrapping = TextWrapping.Wrap; detail.Foreground = MatrixTheme.Muted; detail.Margin = new Thickness(0, 6, 0, 16); header.Children.Add(detail);
            WrapPanel buttons = new WrapPanel(); header.Children.Add(buttons);
            connect.Content = host ? "Go offline" : "Disconnect"; connect.Padding = new Thickness(14, 8, 14, 8); connect.Margin = new Thickness(0, 0, 8, 15); buttons.Children.Add(connect);
            MatrixTheme.Primary(connect);
            stop.Content = "Stop sequence"; stop.Padding = new Thickness(14, 8, 14, 8); stop.Margin = new Thickness(0, 0, 8, 15); stop.IsEnabled = false; buttons.Children.Add(stop);
            report.IsReadOnly = true; report.TextWrapping = TextWrapping.Wrap; report.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            report.Background = MatrixTheme.Panel; report.Foreground = MatrixTheme.Ink;
            report.BorderThickness = new Thickness(0); report.Padding = new Thickness(0, 12, 0, 0); report.Margin = new Thickness(0);
            report.FontFamily = MatrixTheme.Mono; report.FontSize = 13;
            TextBlock footer = new TextBlock { Text = "MSI · MAGIX MEDIA WORKFLOWS", Foreground = MatrixTheme.Muted, FontFamily = MatrixTheme.Mono, FontSize = 10, Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            DockPanel activity = new DockPanel();
            TextBlock activityLabel = MatrixTheme.Eyebrow("SEQUENCE // ACTIVITY");
            DockPanel.SetDock(activityLabel, Dock.Top); activity.Children.Add(activityLabel); activity.Children.Add(report);
            root.Children.Add(new Border { Child = activity, Background = MatrixTheme.Panel, BorderBrush = MatrixTheme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(16) });
            connect.Click += async delegate { await Toggle(); };
            stop.Click += async delegate { await Stop(); };
            Loaded += async delegate
            {
                try { if (host) StartHost(); else { client = new PortalRemoteClient(FindClient()); connectionGeneration++; } await Refresh(); timer.Start(); }
                catch (Exception ex) { ShowError(ex); }
            };
            timer.Interval = TimeSpan.FromSeconds(3); timer.Tick += async delegate { await Refresh(); };
            Closed += delegate { closed = true; connectionGeneration++; timer.Stop(); if (client != null) client.Dispose(); if (server != null) server.Dispose(); if (coordinator != null) coordinator.Dispose(); };
        }

        private void StartHost()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ED-IT Buddy", "portal");
            string state = Path.Combine(root, "state"); Directory.CreateDirectory(state);
            string sid = WindowsIdentity.GetCurrent().User.Value;
            int session = Process.GetCurrentProcess().SessionId;
            coordinator = new PortalCoordinator(Path.Combine(state, "jobs"), Path.Combine(root, "workspaces"), new MagixEditingRunner());
            string pipe = PortalPipeServer.PipeNameForSession(sid, session);
            server = new PortalPipeServer(coordinator, pipe, sid); server.Start();
            coordinator.SetOnline(true);
            using (Process p = Process.GetCurrentProcess())
                File.WriteAllText(Path.Combine(state, "endpoint.json"), json.Serialize(new Dictionary<string, object> {
                    {"host_name", Environment.MachineName}, {"user_sid", sid}, {"session_id", session}, {"process_id", p.Id},
                    {"process_start_ticks", p.StartTime.ToUniversalTime().Ticks}, {"pipe_name", pipe}, {"executable", p.MainModule.FileName}
                }), new UTF8Encoding(false));
        }

        private async Task<object> Call(string method, object parameters)
        {
            if (!host) { if (client == null) throw new InvalidOperationException("Disconnected from MSI."); return await client.Call(method, parameters); }
            string requestedJob = null;
            if (method == "stop_job")
            {
                var values = parameters as Dictionary<string, object>; object value;
                if (values == null || !values.TryGetValue("job_id", out value) || !(value is string) || String.IsNullOrEmpty((string)value))
                    throw new InvalidOperationException("Choose the sequence to stop.");
                requestedJob = (string)value;
            }
            return await Task.Run<object>(delegate
            {
                if (method == "get_state") return coordinator.GetState();
                if (method == "stop_job") return coordinator.StopJob(requestedJob);
                throw new InvalidOperationException("Unsupported dashboard request.");
            });
        }

        private async Task Refresh()
        {
            if (refreshing || closed || (!host && client == null)) return;
            refreshing = true;
            int generation = connectionGeneration;
            try
            {
                object result = await Call("get_state", new Dictionary<string, object>());
                if (closed || generation != connectionGeneration) return;
                string raw = json.Serialize(result);
                var state = json.DeserializeObject(raw) as Dictionary<string, object>;
                report.Text = StatusText(state);
                activeJob = FindActiveJob(state);
                bool online = state != null && state.ContainsKey("online") && Convert.ToBoolean(state["online"]);
                connection.Text = online ? "Connected to MSI" : "MSI host is offline";
                detail.Text = activeJob == null ? "No sequence running. Give editing instructions in your Codex chat." : "Sequence " + activeJob + " · Stop prevents further steps. Completed edits remain in MAGIX.";
                stop.IsEnabled = activeJob != null;
                connect.Content = host ? (online ? "Go offline" : "Go online") : "Disconnect";
            }
            catch (Exception ex) { if (!closed && generation == connectionGeneration) ShowError(ex); }
            finally { refreshing = false; }
        }

        private async Task Stop()
        {
            if (String.IsNullOrEmpty(activeJob)) return;
            string requestedJob = activeJob;
            try { await Call("stop_job", new Dictionary<string, object>{{"job_id",requestedJob}}); await Refresh(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task Toggle()
        {
            if (host)
            {
                coordinator.SetOnline(String.Equals(Convert.ToString(connect.Content), "Go online", StringComparison.Ordinal));
                connectionGeneration++;
                await Refresh();
            }
            else if (client != null)
            {
                // Disconnect is explicitly global for the displayed job, even if another chat submitted it.
                if (activeJob != null) { try { await Call("stop_job", new Dictionary<string,object>{{"job_id",activeJob}}); } catch (Exception ex) { ShowError(ex); return; } }
                connectionGeneration++; client.Dispose(); client = null; activeJob = null; connect.Content = "Connect to MSI"; stop.IsEnabled = false; connection.Text = "Disconnected";
                detail.Text = "No new requests are sent from this window.";
            }
            else { try { client = new PortalRemoteClient(FindClient()); connectionGeneration++; await Refresh(); } catch (Exception ex) { ShowError(ex); } }
        }

        private void ShowError(Exception ex)
        {
            connection.Text = "Connection needs attention"; detail.Text = ex.Message;
            // Snapshot failures must not remove the independent Stop path for a known job.
            stop.IsEnabled = !closed && !String.IsNullOrEmpty(activeJob) && (host ? coordinator != null : client != null);
        }
        private string FindActiveJob(Dictionary<string, object> state)
        {
            if (state == null) return null;
            object value;
            if (state.TryGetValue("current_job", out value))
            {
                var job = value as Dictionary<string,object>;
                if (job != null && job.ContainsKey("job_id") && job.ContainsKey("status") && (Convert.ToString(job["status"]) == "running" || Convert.ToString(job["status"]) == "queued")) return Convert.ToString(job["job_id"]);
            }
            return null;
        }
        private static string FindClient()
        {
            string supplied = Environment.GetEnvironmentVariable("EDIT_BUDDY_PORTAL_CLIENT");
            if (!String.IsNullOrEmpty(supplied) && File.Exists(supplied)) return Path.GetFullPath(supplied);
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null) { string path = Path.Combine(dir.FullName,"portal","client.mjs"); if (File.Exists(path)) return path; dir = dir.Parent; }
            throw new FileNotFoundException("The portal client was not found. Keep the portal folder beside the application package.");
        }
        private static string StatusText(Dictionary<string,object> state)
        {
            if(state==null)return "No status was returned.";
            StringBuilder text=new StringBuilder(); object value;
            if(state.TryGetValue("magix",out value)) {
                var editor=value as Dictionary<string,object>;
                if(editor!=null) {
                    text.AppendLine("MAGIX").AppendLine(Field(editor,"reason"));
                    if(editor.ContainsKey("version"))text.AppendLine("Version "+Field(editor,"version"));
                    text.AppendLine();
                }
            }
            if(state.TryGetValue("current_job",out value) && value is Dictionary<string,object>) {
                var job=(Dictionary<string,object>)value;
                text.AppendLine("Latest sequence · "+Field(job,"status")).AppendLine(Field(job,"message")).AppendLine();
                object steps;
                if(job.TryGetValue("steps",out steps) && steps is object[])foreach(object entry in (object[])steps) {
                    var step=entry as Dictionary<string,object>; if(step==null)continue;
                    text.AppendLine(Field(step,"action").Replace('_',' ')+" · "+Field(step,"status"));
                    object result;
                    if(step.TryGetValue("result",out result) && result is Dictionary<string,object>)text.AppendLine("  "+Field((Dictionary<string,object>)result,"message"));
                }
                text.AppendLine().AppendLine("Workspace").AppendLine(Field(job,"workspace"));
            } else text.AppendLine("Ready for instructions from your Codex chat.\n\nEditing commands become available after they pass a live MAGIX check. Unknown controls stop the sequence for review.");
            return text.ToString();
        }
        private static string Field(Dictionary<string,object> value,string key){object item;return value.TryGetValue(key,out item)?Convert.ToString(item):"";}
        private static string Pretty(string raw)
        {
            StringBuilder b = new StringBuilder(); bool quote=false,escape=false; int depth=0;
            foreach(char c in raw) {
                if(quote) {b.Append(c); if(escape)escape=false; else if(c=='\\')escape=true; else if(c=='\"')quote=false; continue;}
                if(c=='\"'){quote=true;b.Append(c);} else if(c=='{'||c=='['){b.Append(c).AppendLine();depth++;b.Append(' ',depth*2);}
                else if(c=='}'||c==']'){depth--;b.AppendLine().Append(' ',Math.Max(0,depth*2)).Append(c);} else if(c==','){b.Append(c).AppendLine().Append(' ',depth*2);} else if(c==':')b.Append(": "); else b.Append(c);
            } return b.ToString();
        }
    }

    internal sealed class PortalRemoteClient : IDisposable
    {
        private readonly Process process;
        private readonly object gate = new object();
        private readonly Dictionary<string,TaskCompletionSource<object>> pending = new Dictionary<string,TaskCompletionSource<object>>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
        private int next; private bool disposed;
        internal PortalRemoteClient(string client)
        {
            string node = Environment.GetEnvironmentVariable("EDIT_BUDDY_NODE");
            if(String.IsNullOrEmpty(node)) node=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".cache","codex-runtimes","codex-primary-runtime","dependencies","node","bin","node.exe");
            if(!File.Exists(node)) throw new FileNotFoundException("Set EDIT_BUDDY_NODE to the installed Node executable.");
            process = new Process { StartInfo = new ProcessStartInfo { FileName=node, Arguments=Quote(client)+" --rpc", WorkingDirectory=Path.GetDirectoryName(client), UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8 } };
            process.Start();
            Task.Run(delegate { ReadOutput(); });
            Task.Run(delegate { while(!process.StandardError.EndOfStream) process.StandardError.ReadLine(); });
        }
        internal async Task<object> Call(string method, object parameters)
        {
            string id=Interlocked.Increment(ref next).ToString(); var completion=new TaskCompletionSource<object>();
            lock(gate) {
                if(disposed)throw new ObjectDisposedException("PortalRemoteClient");
                pending[id]=completion;
                process.StandardInput.WriteLine(json.Serialize(new {id=id,method=method,@params=parameters})); process.StandardInput.Flush();
            }
            Task winner=await Task.WhenAny(completion.Task,Task.Delay(15000));
            if(winner!=completion.Task){lock(gate)pending.Remove(id);throw new TimeoutException("MSI did not answer within 15 seconds. No request was retried.");}
            return await completion.Task;
        }
        private void ReadOutput()
        {
            try {
                string line;
                while((line=process.StandardOutput.ReadLine())!=null) {
                    var response=json.DeserializeObject(line) as Dictionary<string,object>;
                    if(response==null||!response.ContainsKey("id"))continue;
                    string id=Convert.ToString(response["id"]); TaskCompletionSource<object> item;
                    lock(gate){if(!pending.TryGetValue(id,out item))continue;pending.Remove(id);}
                    object error;
                    if(response.TryGetValue("error",out error)&&error!=null)item.TrySetException(new InvalidOperationException(json.Serialize(error)));
                    else item.TrySetResult(response.ContainsKey("result")?response["result"]:null);
                }
            } catch(Exception ex){FailAll(ex);return;}
            FailAll(new IOException("The MSI transport closed. Check that MSI is awake and connected."));
        }
        private void FailAll(Exception ex){lock(gate){foreach(var item in pending.Values)item.TrySetException(ex);pending.Clear();}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;try{process.StandardInput.Close();}catch{} } FailAll(new ObjectDisposedException("PortalRemoteClient"));}
        internal static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
    }
}
