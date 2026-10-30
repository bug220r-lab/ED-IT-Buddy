using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace EditBuddy
{
    public sealed class ActionSpec
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public bool ChangesProject { get; set; }
    }
    public sealed class ActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
    public sealed class ConnectionReport
    {
        public bool Connected { get; set; }
        public bool Blocked { get; set; }
        public string WindowTitle { get; set; }
        public string Version { get; set; }
        public string Status { get; set; }
        public int ProcessId { get; set; }
        public int ControlCount { get; set; }
        public string ControlsText { get; set; }
        public long WindowHandle { get; set; }
        public long ProcessStartUtcTicks { get; set; }
    }
    internal sealed class ProbeReport
    {
        public int Count { get; set; }
        public bool Truncated { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public List<ControlRecord> Controls { get; set; }
    }
    public sealed class MagixController
    {
        private readonly object gate = new object();
        public string InstallPath { get; private set; }
        public List<ActionSpec> Actions { get; private set; }
        public MagixController()
        {
            InstallPath = FindInstall();
            Actions = new List<ActionSpec> {
                new ActionSpec { Id="focus_magix", Label="Bring MAGIX forward", Description="Activate MAGIX or its current dialog and verify foreground focus.", ChangesProject=false },
                new ActionSpec { Id="inspect_controls", Label="Inspect accessible controls", Description="Read the current window's accessible controls. Does not change the project.", ChangesProject=false }
            };
        }
        public ConnectionReport Inspect()
        {
            lock (gate)
            {
                ConnectionReport r = Detect();
                if (!r.Connected) return r;
                ProbeReport p = ReadProbe(r.WindowHandle, r.ProcessId);
                r.ControlCount = p.Count;
                r.ControlsText = Header(r) + Environment.NewLine + ControlEvidence.Summarize(p.Controls) + Environment.NewLine + Environment.NewLine + (p.Text ?? "");
                if (!String.IsNullOrEmpty(p.Error))
                    r.Status += " Accessibility inspection: " + p.Error;
                else
                    r.Status += " Read " + p.Count + " accessible elements" + (p.Truncated ? " (bounded snapshot)." : ".");
                SaveSnapshot(r);
                return r;
            }
        }
        public string CaptureControlsText() { return Inspect().ControlsText ?? "MAGIX is not connected."; }
        public ActionResult Execute(string actionId)
        {
            lock (gate)
            {
                if (!ActionPolicy.IsSupported(actionId))
                    return Fail("This action is not supported or has not passed live validation. No input was sent.");
                if (actionId == "inspect_controls")
                {
                    ConnectionReport r = Inspect();
                    return new ActionResult { Success=r.Connected && r.ControlCount > 0, Message=r.Status };
                }
                if (actionId != "focus_magix") return Fail("This action is not supported. No input was sent.");
                ConnectionReport target = Detect();
                if (!target.Connected) return Fail(target.Status);
                IntPtr window = new IntPtr(target.WindowHandle);
                if (target.Blocked)
                {
                    IntPtr popup=Native.GetLastActivePopup(window);
                    if (popup!=IntPtr.Zero && Native.IsWindowVisible(popup)) window=popup;
                }
                if (!Native.IsWindow(window)) return Fail("The MAGIX window closed. Reconnect before trying again.");
                uint livePid; Native.GetWindowThreadProcessId(window, out livePid);
                if (livePid != target.ProcessId) return Fail("The target window changed. No input was sent.");
                if (!IsSameProcess(target)) return Fail("The MAGIX process changed. Reconnect before trying again.");
                if (window.ToInt64() != target.WindowHandle && Native.GetAncestor(window,3).ToInt64() != target.WindowHandle)
                    return Fail("The active dialog no longer belongs to the selected editor. No input was sent.");
                if (!Native.IsWindowEnabled(window)) return Fail("MAGIX is still busy with a dialog. Select that dialog in MAGIX; no input was sent.");
                if (Native.IsIconic(window)) Native.ShowWindow(window, 9);
                Native.SetForegroundWindow(window);
                for (int i=0; i<8; i++)
                {
                    if (Native.GetForegroundWindow() == window)
                        return new ActionResult { Success=true, Message=target.Blocked ? "Verified: MAGIX's active dialog is in front. Finish that dialog before editing. No project content was changed." : "Verified: MAGIX is the foreground window. No project content was changed." };
                    Thread.Sleep(60);
                }
                return Fail("Windows did not grant foreground focus. Click MAGIX once, then retry. No editing input was sent.");
            }
        }
        public void Launch()
        {
            lock (gate)
            {
                if (Detect().Connected) throw new InvalidOperationException("MAGIX is already open. Use Bring MAGIX forward.");
                string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");
                if (!File.Exists(steam)) throw new FileNotFoundException("Steam was not found. Launch MAGIX from your Steam library.");
                Process.Start(new ProcessStartInfo { FileName=steam, Arguments="-applaunch 1363250", WorkingDirectory=Path.GetDirectoryName(steam), UseShellExecute=true });
            }
        }
        private ConnectionReport Detect()
        {
            if (String.IsNullOrEmpty(InstallPath) || !File.Exists(InstallPath)) InstallPath = FindInstall();
            var r = new ConnectionReport { WindowTitle="", Version=Version(), Status="MAGIX is not open. Launch it from Steam, finish first-run setup, then refresh.", ControlsText="No editor window connected." };
            if (String.IsNullOrEmpty(InstallPath)) { r.Status="MAGIX Steam edition was not found in the known Steam libraries."; return r; }
            List<Process> matching = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("Videodeluxe"))
            {
                try
                {
                    if (String.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase)) matching.Add(p);
                    else p.Dispose();
                }
                catch { p.Dispose(); }
            }
            try
            {
                if (matching.Count > 1) { r.Blocked=true; r.Status="Multiple MAGIX instances are running. Close the extra instance before connecting."; return r; }
                if (matching.Count == 0) return r;
                Process active=matching[0]; r.ProcessId=active.Id;
                r.ProcessStartUtcTicks=active.StartTime.ToUniversalTime().Ticks;
                List<WindowRecord> windows = Native.WindowsFor(active.Id).Select(w => new WindowRecord {
                    Handle=w.ToInt64(), ProcessId=active.Id, ClassName=Native.ClassName(w), Visible=Native.IsWindowVisible(w), Enabled=Native.IsWindowEnabled(w),
                    Owner=Native.GetWindow(w,4).ToInt64(), RootOwner=Native.GetAncestor(w,3).ToInt64()
                }).ToList();
                WindowDecision decision=EditorWindowPolicy.Select(windows,active.Id);
                r.Connected=decision.Connected; r.Blocked=decision.Blocked; r.Status=decision.Reason;
                if (!r.Connected) return r;
                r.WindowHandle=decision.Handle; r.WindowTitle=Native.Title(new IntPtr(r.WindowHandle));
                return r;
            }
            finally { foreach(Process p in matching) p.Dispose(); }
        }
        private string Version()
        {
            try { return FileVersionInfo.GetVersionInfo(InstallPath).FileVersion; } catch { return "Unknown"; }
        }
        private bool IsSameProcess(ConnectionReport report)
        {
            try
            {
                using(Process process=Process.GetProcessById(report.ProcessId))
                    return process.StartTime.ToUniversalTime().Ticks == report.ProcessStartUtcTicks &&
                        String.Equals(Path.GetFullPath(process.MainModule.FileName),Path.GetFullPath(InstallPath),StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }
        private static string Header(ConnectionReport r)
        {
            return "ED-IT Buddy accessibility snapshot\r\nCaptured: " + DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture) + "\r\nMAGIX version: " + r.Version + "\r\nWindow: " + r.WindowTitle + "\r\nProcess: " + r.ProcessId + "\r\nDialog/secondary window: " + r.Blocked + "\r\nAccessible patterns show potential routes, not validated editing commands.\r\n";
        }
        private static ProbeReport ReadProbe(long hwnd, int pid)
        {
            string probe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ED-IT Buddy.Probe.exe");
            if (!File.Exists(probe)) return new ProbeReport { Error="Probe executable is missing. Rebuild the companion." };
            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo=new ProcessStartInfo { FileName=probe, Arguments=hwnd.ToString(CultureInfo.InvariantCulture)+" "+pid.ToString(CultureInfo.InvariantCulture), UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8, WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory };
                    p.Start();
                    var stdout=p.StandardOutput.ReadToEndAsync();
                    var stderr=p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch {} return new ProbeReport { Error="The editor did not answer within 5 seconds. The probe was stopped; the companion remains usable." }; }
                    if (!stdout.Wait(500) || !stderr.Wait(500)) return new ProbeReport {Error="The probe output was incomplete."};
                    if (p.ExitCode!=0) return new ProbeReport {Error="Probe failed: "+stderr.Result};
                    ProbeReport result=new JavaScriptSerializer().Deserialize<ProbeReport>(stdout.Result);
                    return result ?? new ProbeReport {Error="The probe returned no result."};
                }
            }
            catch(Exception e) { return new ProbeReport { Error=e.Message }; }
        }
        private static string FindInstall()
        {
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
            var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {standard};
            try
            {
                string libraries=Path.Combine(standard,"steamapps","libraryfolders.vdf");
                if(File.Exists(libraries)) foreach(Match m in Regex.Matches(File.ReadAllText(libraries),"\"path\"\\s+\"([^\"]+)\"")) roots.Add(m.Groups[1].Value.Replace("\\\\","\\"));
            } catch {}
            foreach(string root in roots)
            {
                try
                {
                    string manifest=Path.Combine(root,"steamapps","appmanifest_1363250.acf");
                    if(!File.Exists(manifest))continue;
                    Match m=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s+\"([^\"]+)\"");
                    if(!m.Success)continue;
                    string common=Path.GetFullPath(Path.Combine(root,"steamapps","common"))+Path.DirectorySeparatorChar;
                    string folder=Path.GetFullPath(Path.Combine(common,m.Groups[1].Value));
                    if(!folder.StartsWith(common,StringComparison.OrdinalIgnoreCase))continue;
                    string exe=Path.Combine(folder,"Video deluxe 2021 Plus Steam Edition","Videodeluxe.exe");
                    if(File.Exists(exe))return exe;
                }catch{}
            }
            return null;
        }
        private static ActionResult Fail(string message) { return new ActionResult { Success=false, Message=message }; }
        private static void SaveSnapshot(ConnectionReport report)
        {
            try
            {
                string folder=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","user-data"));
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"latest-controls.txt"),report.ControlsText??report.Status,new UTF8Encoding(false));
            } catch { /* Diagnostics must not prevent connecting to the editor. */ }
        }
    }
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback,IntPtr param);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window,uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int size);
        internal static string Title(IntPtr window) { var b=new StringBuilder(512);GetWindowText(window,b,b.Capacity);return b.ToString(); }
        internal static string ClassName(IntPtr window) { var b=new StringBuilder(256);GetClassName(window,b,b.Capacity);return b.ToString(); }
        internal static List<IntPtr> WindowsFor(int pid)
        {
            var result=new List<IntPtr>();
            EnumWindows(delegate(IntPtr h,IntPtr _) { uint process;GetWindowThreadProcessId(h,out process);if(process==pid && IsWindowVisible(h))result.Add(h);return true;},IntPtr.Zero);
            return result;
        }
    }
    internal static class AccessibilityProbe
    {
        internal static ProbeReport Read(IntPtr window,int expectedPid)
        {
            uint pid; Native.GetWindowThreadProcessId(window,out pid);
            if(pid!=expectedPid || !Native.IsWindow(window))throw new InvalidOperationException("The target window no longer belongs to the expected process.");
            if(Native.ClassName(window)!=EditorWindowPolicy.EditorClass)throw new InvalidOperationException("The target is no longer the MAGIX editor window.");
            var text=new StringBuilder();var clock=Stopwatch.StartNew();int count=0;bool truncated=false;
            var controls=new List<ControlRecord>();
            var queue=new Queue<Tuple<AutomationElement,int>>();
            queue.Enqueue(Tuple.Create(AutomationElement.FromHandle(window),0));
            var walker=TreeWalker.RawViewWalker;
            while(queue.Count>0)
            {
                if(count>=400 || clock.ElapsedMilliseconds>3500){truncated=true;break;}
                var item=queue.Dequeue();var element=item.Item1;int depth=item.Item2;
                try
                {
                    var info=element.Current;
                    if(info.ProcessId!=expectedPid)continue;
                    var patterns=element.GetSupportedPatterns().Select(p=>p.ProgrammaticName.Replace("PatternIdentifiers.Pattern","")).ToArray();
                    controls.Add(new ControlRecord {Type=info.ControlType.ProgrammaticName.Replace("ControlType.",""), Name=info.IsPassword?"[password control]":Safe(info.Name), Id=Safe(info.AutomationId), Enabled=info.IsEnabled, Offscreen=info.IsOffscreen, Patterns=patterns});
                    text.Append(' ',depth*2).Append(count).Append(" | ").Append(info.ControlType.ProgrammaticName.Replace("ControlType.",""));
                    text.Append(" | ").Append(info.IsPassword?"[password control]":Safe(info.Name));
                    text.Append(" | id=").Append(Safe(info.AutomationId)).Append(" | class=").Append(Safe(info.ClassName));
                    text.Append(" | enabled=").Append(info.IsEnabled).Append(" | offscreen=").Append(info.IsOffscreen);
                    text.Append(" | patterns=").Append(String.Join(",",patterns)).AppendLine();count++;
                    if(depth>=10) {truncated=true;continue;}
                    var child=walker.GetFirstChild(element);int siblings=0;
                    while(child!=null && siblings<400 && queue.Count<500 && clock.ElapsedMilliseconds<3500)
                    {queue.Enqueue(Tuple.Create(child,depth+1));child=walker.GetNextSibling(child);siblings++;}
                    if(child!=null)truncated=true;
                }
                catch(ElementNotAvailableException){text.AppendLine("[element changed during capture]");}
                catch(InvalidOperationException){text.AppendLine("[element unavailable]");}
            }
            return new ProbeReport {Count=count,Truncated=truncated,Text=text.ToString(),Controls=controls};
        }
        private static string Safe(string input) { if(input==null)return ""; input=input.Replace("\r"," ").Replace("\n"," ");return input.Length>240?input.Substring(0,240)+"…":input; }
    }
}
