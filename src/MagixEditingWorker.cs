using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace EditBuddy
{
    public static class EditingCommandPolicy
    {
        private static readonly string[] Actions = { "create_project", "import_media", "trim_clip", "append_clip", "add_title", "save_project", "export_video" };
        public static IList<PortalCapability> Capabilities()
        {
            return Actions.Select(a => new PortalCapability {
                action = a, supported = a == "save_project" || a == "export_video", execution_mode = "single",
                evidence = a == "save_project" ? "MSI MAGIX20.0.1.80 native Save As: exact destination readback,108852-byte MVP, clean title; calibration2026-10-30." : a == "export_video" ? "MSI MAGIX20.0.1.80 native MP4 export: exact destination/options readback, file completion and SHA256; independent H.264640x36030 decode verified2026-10-30." : "",
                reason = a == "save_project" ? "Creates a new .MVP in the dedicated workspace; existing paths are blocked unless ownership is verified." : a == "export_video" ? "Exports a new640x36030fps progressive MP4. Timeline length/content are verified separately; no overwrites." : "The semantic adapter has not yet passed a live MAGIX validation for this operation."
            }).ToList();
        }
        public static string Validate(string action, IDictionary<string, object> args, string workspace)
        {
            if (!Actions.Contains(action)) return "Unsupported editing action.";
            if (args == null) return "Action arguments are required.";
            try
            {
                if (String.IsNullOrWhiteSpace(workspace) || !Path.IsPathRooted(workspace)) return "An absolute test workspace is required.";
                string[] fields;
                switch (action)
                {
                    case "create_project": fields = new[] { "project_path", "width", "height", "fps" }; break;
                    case "import_media": fields = new[] { "media_path", "clip_id" }; break;
                    case "trim_clip": fields = new[] { "clip_id", "source_in_frame", "source_out_frame" }; break;
                    case "append_clip": fields = new[] { "clip_id", "track" }; break;
                    case "add_title": fields = new[] { "text", "timeline_frame", "duration_frames", "track" }; break;
                    case "save_project": fields = new[] { "project_path" }; break;
                    default: fields = new[] { "output_path" }; break;
                }
                if (args.Keys.Any(k => !fields.Contains(k)) || fields.Any(k => !args.ContainsKey(k))) return "Arguments do not match the fixed action schema.";
                foreach (string field in fields.Where(f => f.EndsWith("_path", StringComparison.Ordinal)))
                {
                    string value = args[field] as string;
                    if (!IsWithin(workspace, value)) return field + " must be an absolute path inside the test workspace without reparse-point traversal.";
                }
                if (args.ContainsKey("project_path") && !String.Equals(Path.GetExtension((string)args["project_path"]), ".MVP", StringComparison.OrdinalIgnoreCase)) return "Project paths must use .MVP.";
                if (args.ContainsKey("output_path") && !String.Equals(Path.GetExtension((string)args["output_path"]), ".mp4", StringComparison.OrdinalIgnoreCase)) return "The first export adapter supports .mp4 only.";
                if (args.ContainsKey("clip_id") && (!(args["clip_id"] is string) || !System.Text.RegularExpressions.Regex.IsMatch((string)args["clip_id"], "^[A-Za-z0-9_-]{1,64}$"))) return "clip_id must be a short stable identifier.";
                int v;
                foreach (string field in fields.Where(f => f.EndsWith("_frame", StringComparison.Ordinal) || f == "duration_frames" || f == "track" || f == "width" || f == "height" || f == "fps"))
                {
                    if (!TryInteger(args[field], out v) || v < 0) return field + " must be a non-negative integer.";
                    if ((field == "duration_frames" || field == "track" || field == "width" || field == "height" || field == "fps") && v == 0) return field + " must be greater than zero.";
                }
                if (action == "trim_clip" && Number(args, "source_out_frame") <= Number(args, "source_in_frame")) return "Trim uses [source_in_frame, source_out_frame); the end must follow the start.";
                if (action == "create_project" && (Number(args, "width") > 16384 || Number(args, "height") > 16384 || Number(args, "fps") > 240)) return "Project format exceeds the supported bounds.";
                if (action == "add_title" && (!(args["text"] is string) || String.IsNullOrWhiteSpace((string)args["text"]) || ((string)args["text"]).Length > 1024)) return "Title text must contain 1–1024 characters.";
                if (args.ContainsKey("track") && Number(args, "track") > 99) return "Track exceeds the supported bound.";
                return null;
            }
            catch (Exception) { return "Invalid action arguments or workspace path."; }
        }
        public static bool IsWithin(string workspace, string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith("\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0) return false;
            string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            string cursor = full;
            while (!String.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
                cursor = Path.GetDirectoryName(cursor);
            }
            return true;
        }
        public static bool TryInteger(object value, out int result)
        {
            result = 0;
            if (value == null || value is bool || value is string) return false;
            decimal number;
            if (!Decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out number) || number != Decimal.Truncate(number) || number > Int32.MaxValue || number < Int32.MinValue) return false;
            result = (int)number; return true;
        }
        public static int Number(IDictionary<string, object> args, string key) { int value; if (!TryInteger(args[key], out value)) throw new ArgumentException("Integer required: " + key); return value; }
        public static string Timecode(int frames, int fps)
        {
            if (frames < 0 || fps <= 0 || fps > 240) throw new ArgumentOutOfRangeException();
            return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}:{3:00}", frames / (fps * 3600), (frames / (fps * 60)) % 60, (frames / fps) % 60, frames % fps);
        }
    }

    public sealed class MagixDesktopState
    {
        public string host { get; set; }
        public string user { get; set; }
        public int session_id { get; set; }
        public uint active_console_session_id { get; set; }
        public string input_desktop { get; set; }
        public bool interactive_ready { get; set; }
        public bool ready { get; set; }
        public string reason { get; set; }
        public string executable { get; set; }
        public string version { get; set; }
        public int pid { get; set; }
        public long process_start_utc_ticks { get; set; }
        public long editor_hwnd { get; set; }
        public long foreground_hwnd { get; set; }
        public List<MagixWindowSnapshot> windows { get; set; }
    }
    public sealed class MagixWindowSnapshot
    {
        public long hwnd { get; set; }
        public long owner { get; set; }
        public long root_owner { get; set; }
        public string title { get; set; }
        public string class_name { get; set; }
        public bool enabled { get; set; }
        public List<MagixElementSnapshot> elements { get; set; }
        public bool truncated { get; set; }
        public string inspection_error { get; set; }
    }
    public sealed class MagixElementSnapshot
    {
        public int index { get; set; }
        public int parent_index { get; set; }
        public int depth { get; set; }
        public string type { get; set; }
        public string name { get; set; }
        public string automation_id { get; set; }
        public string class_name { get; set; }
        public long hwnd { get; set; }
        public string accelerator_key { get; set; }
        public string access_key { get; set; }
        public bool enabled { get; set; }
        public bool offscreen { get; set; }
        public bool focused { get; set; }
        public string value { get; set; }
        public string legacy_value { get; set; }
        public string[] patterns { get; set; }
        public double[] bounds { get; set; }
    }

    public static class MagixEditingWorkerMain
    {
        private static bool sent;
        private static string outputPath;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        [MTAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Mutex controller = null; bool controllerOwned = false;
            try
            {
                EditingWorkerRequest request;
                if (args.Length == 2 && args[0] == "--capture") { string capture = MagixEditingCapture.Capture(Path.GetFullPath(args[1])); Reply(new WorkerReply { kind = "result", state = new { capture_path = capture } }); return 0; }
                if (args.Length == 2 && args[0] == "--inspect") { outputPath = Path.GetFullPath(args[1]); request = new EditingWorkerRequest { method = "inspect" }; }
                else if (args.Length == 4 && args[0] == "--request" && args[2] == "--output") { outputPath = Path.GetFullPath(args[3]); request = Serializer.Deserialize<EditingWorkerRequest>(File.ReadAllText(args[1], Encoding.UTF8)); }
                else if (args.Length == 0) { string line = Console.ReadLine(); if (line == null || line.Length > 1024 * 1024) throw new InvalidDataException("Missing or oversized worker request."); request = Serializer.Deserialize<EditingWorkerRequest>(line); }
                else throw new ArgumentException("Use stdin JSON, --inspect output.json, or --request input.json --output result.json.");
                if (request == null) throw new InvalidDataException("Missing worker request.");
                if (request.method != "state" && request.method != "inspect" && request.method != "capture" && request.method != "capture_scene")
                {
                    controller = new Mutex(false, "Local\\ED-IT Buddy MAGIX input session " + Process.GetCurrentProcess().SessionId);
                    try { controllerOwned = controller.WaitOne(0); } catch (AbandonedMutexException) { controllerOwned = true; }
                    if (!controllerOwned) throw new InvalidOperationException("Another Buddy worker already controls this MAGIX session.");
                }
                if (request.method == "capture" && outputPath != null) { string capture = MagixEditingCapture.Capture(Path.ChangeExtension(outputPath, ".png")); Reply(new WorkerReply { kind = "result", state = new { capture_path = capture } }); return 0; }
                if (request.method == "capture_scene" && outputPath != null) { string capture = MagixEditingCapture.CaptureScene(Path.ChangeExtension(outputPath, ".png")); Reply(new WorkerReply { kind = "result", state = new { capture_path = capture } }); return 0; }
                if ((request.method == "focus_capture_scene" || request.method == "focus_capture_timeline") && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    ui.Focus(new IntPtr(observed.editor_hwnd)); Thread.Sleep(250);
                    string capture = MagixEditingCapture.CaptureScene(Path.ChangeExtension(outputPath, ".png"), request.method == "focus_capture_timeline");
                    Reply(new WorkerReply { kind = "result", state = new { capture_path = capture } }); return 0;
                }
                if (request.method == "state" || request.method == "inspect") { Reply(new WorkerReply { kind = "result", state = Snapshot(request.method == "inspect") }); return 0; }
                if (request.method == "adopt_verified_save" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); new MagixEditingUi(observed, MarkInput).VerifyProcess();
                    if (request.args == null || request.args.Count != 2 || !request.args.ContainsKey("project_path") || !request.args.ContainsKey("sha256")) throw new InvalidOperationException("Explicit project path and native-save SHA256 evidence are required.");
                    EditingProjectReceipt receipt = EditingProjectOwnership.AdoptVerified(request.workspace, (string)request.args["project_path"], observed, (string)request.args["sha256"]);
                    Reply(new WorkerReply { kind = "result", state = receipt }); return 0;
                }
                if (request.method == "decline_proxy_prompt" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("The expected disposable project is not active.");
                    ui.DeclineKnownProxyPrompt(); Reply(new WorkerReply { kind = "result", state = Snapshot(false) }); return 0;
                }
                if (request.method == "inspect_new_project_destination" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("The expected disposable project is not active.");
                    IntPtr settings = ui.FindWindow("Project/movie settings", "#32770", true);
                    ui.ClickElement(settings, "4847", "Project settings", ControlType.Button);
                    Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                }
                if (request.method == "configure_new_project" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("The expected disposable project is not active.");
                    IntPtr settings = ui.FindWindow("Project/movie settings", "#32770", true);
                    ui.ClickElement(settings, "4845", "Movie settings", ControlType.Button);
                    ui.SetNativeCheck(settings, "889", "Use settings as presets for new movies", false);
                    ui.SetValue(settings, "2206", null, ControlType.Edit, "640");
                    ui.SetValue(settings, "2207", null, ControlType.Edit, "360");
                    ui.SetValue(settings, "2208", null, ControlType.Edit, "30");
                    if (ui.ReadValue(settings, "2206", null, ControlType.Edit) != "640" || ui.ReadValue(settings, "2207", null, ControlType.Edit) != "360" || ui.ReadValue(settings, "2208", null, ControlType.Edit) != "30") throw new InvalidOperationException("Project format readback failed.");
                    var evidence = Snapshot(true);
                    ui.ClickElement(settings, "1", "OK", ControlType.Button);
                    bool closed = MagixEditingUi.Wait(delegate { return !Native.IsWindow(settings) || !Native.IsWindowVisible(settings); }, 3000);
                    Reply(new WorkerReply { kind = "result", result = new PortalActionResult { status = closed ? "blocked" : "uncertain_after_input", input_sent = true, message = "Disposable project format fields read back 640x360 at30fps; native settings confirmation attempted. Save and independent reopen remain unverified.", evidence = new { before_confirm = evidence, after_confirm = Snapshot(true) } } }); return 0;
                }
                if ((request.method == "inspect_file_menu" || request.method == "inspect_edit_menu" || request.method == "inspect_effects_menu") && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    IntPtr editor = new IntPtr(observed.editor_hwnd);
                    if (!IsDemoTitle(Native.Title(editor))) throw new InvalidOperationException("The expected disposable project is not active.");
                    string menuId = request.method == "inspect_edit_menu" ? "5820" : request.method == "inspect_effects_menu" ? "5821" : "5819";
                    string menuName = request.method == "inspect_edit_menu" ? "Edit" : request.method == "inspect_effects_menu" ? "Effects" : "File";
                    ui.Invoke(editor, menuId, menuName, ControlType.Button); Thread.Sleep(300);
                    var menuState = Snapshot(true);
                    string menuCapture = MagixEditingCapture.Capture(Path.ChangeExtension(outputPath, ".png"));
                    Reply(new WorkerReply { kind = "result", state = new { snapshot = menuState, capture_path = menuCapture } }); return 0;
                }
                if (request.method == "inspect_save_as" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    IntPtr editor = new IntPtr(observed.editor_hwnd);
                    if (Native.Title(editor).TrimEnd('*') != "Movie Edit Pro 2021 Plus Steam Edition - Buddy Portal Demo.MVP") throw new InvalidOperationException("The expected disposable project is not active.");
                    ui.Invoke(editor, "5819", "File", ControlType.Button);
                    IntPtr menu = IntPtr.Zero;
                    if (!MagixEditingUi.Wait(delegate { menu = ui.FindWindow(null, "#32768", true); return menu != IntPtr.Zero; }, 1500)) throw new InvalidOperationException("The File menu did not open.");
                    ui.Invoke(menu, "Item 36793", "", ControlType.MenuItem);
                    Thread.Sleep(300);
                    Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                }
                if ((request.method == "inspect_manage_project" || request.method == "inspect_import_movie" || request.method == "inspect_movie_import_dialog" || request.method == "inspect_movie_export" || request.method == "inspect_settings_menu" || request.method == "inspect_current_movie_settings" || request.method == "inspect_export_video" || request.method == "inspect_export_video_dialog") && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    IntPtr editor = new IntPtr(observed.editor_hwnd);
                    if (!IsDemoTitle(Native.Title(editor))) throw new InvalidOperationException("The expected disposable project is not active.");
                    if (request.method == "inspect_export_video" || request.method == "inspect_export_video_dialog") ui.DeclineKnownProxyPrompt();
                    ui.Invoke(editor, "5819", "File", ControlType.Button);
                    IntPtr menu = IntPtr.Zero;
                    if (!MagixEditingUi.Wait(delegate { menu = ui.FindWindow(null, "#32768", true); return menu != IntPtr.Zero; }, 1500)) throw new InvalidOperationException("The File menu did not open.");
                    ui.Expand(menu, request.method == "inspect_settings_menu" || request.method == "inspect_current_movie_settings" ? "Item 24" : request.method == "inspect_export_video" || request.method == "inspect_export_video_dialog" ? "Item 9" : "Item 8"); Thread.Sleep(250);
                    if (request.method == "inspect_export_video_dialog")
                    {
                        ui.Invoke(menu, "Item 34903", "", ControlType.MenuItem);
                        MagixEditingUi.Wait(delegate { return !Native.IsWindowEnabled(editor) && Native.WindowsFor(observed.pid).Any(w => Native.IsWindowEnabled(w) && Native.ClassName(w) == "#32770"); }, 8000);
                        Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                    }
                    if (request.method == "inspect_current_movie_settings")
                    {
                        ui.Invoke(menu, "Item 33310", "", ControlType.MenuItem);
                        IntPtr settings = IntPtr.Zero;
                        if (!MagixEditingUi.Wait(delegate { settings = ui.FindWindow("Project/movie settings", "#32770", true); return settings != IntPtr.Zero; }, 4000)) throw new InvalidOperationException("The movie settings dialog did not open.");
                        Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                    }
                    if (request.method == "inspect_movie_export") { ui.Expand(menu, "Item 7"); Thread.Sleep(250); }
                    if (request.method == "inspect_import_movie" || request.method == "inspect_movie_import_dialog") { ui.Expand(menu, "Item 6"); Thread.Sleep(250); }
                    if (request.method == "inspect_movie_import_dialog")
                    {
                        ui.Invoke(menu, "Item 36795", "", ControlType.MenuItem);
                        MagixEditingUi.Wait(delegate { return !Native.IsWindowEnabled(editor) && Native.WindowsFor(observed.pid).Any(w => Native.IsWindowEnabled(w) && Native.ClassName(w) == "#32770"); }, 8000);
                        Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                    }
                    var menuState = Snapshot(true);
                    string menuCapture = MagixEditingCapture.Capture(Path.ChangeExtension(outputPath, ".png"));
                    Reply(new WorkerReply { kind = "result", state = new { snapshot = menuState, capture_path = menuCapture } }); return 0;
                }
                if ((request.method == "import_demo_edl" || request.method == "import_demo_edl_video") && outputPath != null)
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ED-IT Buddy", "portal", "workspaces", "demo-20261030");
                    bool tagged = request.method == "import_demo_edl_video";
                    string source = Path.Combine(root, tagged ? "buddy-demo-video-tagged.edl" : "buddy-demo-original-clips.edl");
                    if (!File.Exists(source)) throw new InvalidOperationException("The generated demonstration EDL is missing.");
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("The saved empty disposable project is not active.");
                    IntPtr dialog = ui.FindWindow("Load movie...", "#32770", true);
                    if (tagged && dialog == IntPtr.Zero)
                    {
                        ui.Invoke(new IntPtr(observed.editor_hwnd), "5819", "File", ControlType.Button); IntPtr menu = IntPtr.Zero;
                        if (!MagixEditingUi.Wait(delegate { menu = ui.FindWindow(null, "#32768", true); return menu != IntPtr.Zero; }, 2000)) throw new InvalidOperationException("File menu did not open.");
                        ui.Expand(menu, "Item 8"); Thread.Sleep(150); ui.Expand(menu, "Item 6"); Thread.Sleep(150); ui.Invoke(menu, "Item 36795", "", ControlType.MenuItem);
                        if (!MagixEditingUi.Wait(delegate { dialog = ui.FindWindow("Load movie...", "#32770", true); return dialog != IntPtr.Zero; }, 8000)) throw new InvalidOperationException("Movie import dialog did not open.");
                    }
                    if (dialog == IntPtr.Zero) throw new InvalidOperationException("The validated movie import dialog is not open.");
                    ui.SetValue(dialog, "1148", "File name:", ControlType.Edit, source);
                    if (ui.ReadValue(dialog, "1148", "File name:", ControlType.Edit) != source) throw new InvalidOperationException("The movie import destination readback changed.");
                    using (var attempt = new FileStream(Path.Combine(root, tagged ? ".buddy-edl-video-import-attempt.json" : ".buddy-edl-import-attempt.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { byte[] bytes = Encoding.UTF8.GetBytes("{\"attempt_utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}"); attempt.Write(bytes, 0, bytes.Length); }
                    ui.Invoke(dialog, "1", "Open", ControlType.Button); Thread.Sleep(1500);
                    var resultState = Snapshot(true); string capture = MagixEditingCapture.Capture(Path.ChangeExtension(outputPath, ".png"));
                    Reply(new WorkerReply { kind = "result", result = new PortalActionResult { status = "uncertain_after_input", input_sent = true, message = "Submitted generated EDL to the native movie importer once; inspect native outcome before any further action.", evidence = new { snapshot = resultState, capture_path = capture } } }); return 0;
                }
                if (request.method == "acknowledge_demo_import" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    IntPtr dialog = ui.FindWindow("MAGIX Movie Edit Pro", "#32770", true);
                    string information = ui.Unique(dialog, "371", null, ControlType.Text).Current.Name;
                    if (information != "Your movie file has been successfully loaded.\r\nYou can continue to work normally with the movie; a new project file will be \r\nadded during saving because the program supports a different save format.\r\nYour old movie file will remain unaffected.") throw new InvalidOperationException("The import information dialog changed; it was not dismissed.");
                    ui.Invoke(dialog, "197", "OK", ControlType.Button);
                    if (!MagixEditingUi.Wait(delegate { return !Native.IsWindow(dialog) && Native.IsWindowEnabled(new IntPtr(observed.editor_hwnd)); }, 3000)) throw new InvalidOperationException("The import information dialog did not close.");
                    Thread.Sleep(300); var after = Snapshot(true); string capture = MagixEditingCapture.Capture(Path.ChangeExtension(outputPath, ".png"));
                    Reply(new WorkerReply { kind = "result", state = new { snapshot = after, capture_path = capture } }); return 0;
                }
                if (request.method == "create_variant_project" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("Only the known disposable project may be replaced by this calibration variant.");
                    IntPtr dialog = ui.FindWindow("MAGIX Movie Edit Pro", "#32770", true);
                    if (ui.Unique(dialog, "371", null, ControlType.Text).Current.Name != "A project is still open. How would you like\r\nto handle the EDL files?") throw new InvalidOperationException("The EDL import choice prompt changed.");
                    ui.Invoke(dialog, "197", "Create new project", ControlType.Button); Thread.Sleep(500);
                    Reply(new WorkerReply { kind = "result", state = Snapshot(true) }); return 0;
                }
                if (request.method == "acknowledge_variant_write_error" && outputPath != null)
                {
                    MagixDesktopState observed = Snapshot(false); var ui = new MagixEditingUi(observed, MarkInput);
                    if (!IsDemoTitle(Native.Title(new IntPtr(observed.editor_hwnd)))) throw new InvalidOperationException("The known disposable project is not active.");
                    IntPtr dialog = ui.FindWindow("MAGIX Movie Edit Pro", "#32770", true);
                    if (ui.Unique(dialog, "371", null, ControlType.Text).Current.Name != "An error occurred while\r\nwriting the project file.") throw new InvalidOperationException("The expected project-write error is not present.");
                    ui.Invoke(dialog, "197", "OK", ControlType.Button); Thread.Sleep(200);
                    Reply(new WorkerReply { kind = "result", state = Snapshot(false) }); return 0;
                }
                if (request.method != "execute") throw new ArgumentException("Unknown worker method.");
                string error = EditingCommandPolicy.Validate(request.action, request.args, request.workspace);
                if (error != null) { Reply(new WorkerReply { kind = "result", result = MagixEditingRunner.Failure("failed_before_input", error, false) }); return 0; }
                MagixDesktopState state = Snapshot(false);
                if (!String.Equals(Environment.MachineName, "MSI", StringComparison.OrdinalIgnoreCase))
                    Reply(new WorkerReply { kind = "result", result = MagixEditingRunner.Failure("blocked", "Editing input is restricted to the approved MSI host.", false) });
                else if (!state.interactive_ready || state.pid == 0)
                    Reply(new WorkerReply { kind = "result", result = MagixEditingRunner.Failure("blocked", state.reason, false) });
                else
                    Reply(new WorkerReply { kind = "result", result = MagixEditingActions.Execute(request, state, MarkInput) });
                return 0;
            }
            catch (Exception e)
            {
                var failure = MagixEditingRunner.Failure(sent ? "uncertain_after_input" : "failed_before_input", e.Message, sent);
                if (outputPath != null && String.Equals(Environment.MachineName, "MSI", StringComparison.OrdinalIgnoreCase))
                    try { failure.evidence = Snapshot(true); } catch { }
                Reply(new WorkerReply { kind = "result", result = failure });
                return 0;
            }
            finally { if (controller != null) { if (controllerOwned) controller.ReleaseMutex(); controller.Dispose(); } }
        }
        private static void MarkInput()
        {
            if (sent) return;
            if (outputPath == null)
            {
                Console.WriteLine(Serializer.Serialize(new WorkerReply { kind = "before_input" })); Console.Out.Flush();
                if (Console.ReadLine() != "permit_input") throw new InvalidOperationException("The coordinator did not grant a durable input permit.");
            }
            sent = true;
        }
        private static bool IsDemoTitle(string title)
        {
            string prefix = "Movie Edit Pro 2021 Plus Steam Edition - Buddy Portal Demo.MVP";
            if (title == prefix || title == prefix + "*") return true;
            foreach (string movie in new[] { "buddy-demo-original-clips", "buddy-demo-video-tagged" })
                foreach (string projectDirty in new[] { "", "*" }) foreach (string movieDirty in new[] { "", "*" })
                    if (title == prefix + projectDirty + " ( " + movie + movieDirty + " )") return true;
            return false;
        }
        private static void Reply(WorkerReply reply)
        {
            if (reply.result != null) reply.result.input_sent = reply.result.input_sent || sent;
            string json = Serializer.Serialize(reply);
            if (outputPath == null) Console.WriteLine(json);
            else { using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json); }
        }
        public static MagixDesktopState Snapshot(bool includeControls)
        {
            var state = new MagixDesktopState { host = Environment.MachineName, user = Environment.UserDomainName + "\\" + Environment.UserName, session_id = Process.GetCurrentProcess().SessionId, active_console_session_id = EditingNative.WTSGetActiveConsoleSessionId(), input_desktop = EditingNative.InputDesktop(), foreground_hwnd = Native.GetForegroundWindow().ToInt64(), windows = new List<MagixWindowSnapshot>() };
            state.interactive_ready = state.session_id > 0 && state.session_id == state.active_console_session_id && String.Equals(state.input_desktop, "Default", StringComparison.OrdinalIgnoreCase);
            state.reason = state.interactive_ready ? "No installed MAGIX process is open." : "Buddy must run in the unlocked MSI interactive desktop; SSH session 0 cannot edit windows.";
            state.executable = new MagixController().InstallPath;
            if (state.executable == null) { state.reason = "The installed Steam MAGIX executable was not found."; return state; }
            state.version = FileVersionInfo.GetVersionInfo(state.executable).FileVersion;
            var matches = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("Videodeluxe"))
            {
                try { if (String.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(state.executable), StringComparison.OrdinalIgnoreCase) && p.SessionId == state.session_id) matches.Add(p); else p.Dispose(); }
                catch { p.Dispose(); }
            }
            try
            {
                if (matches.Count != 1) { if (matches.Count > 1) state.reason = "Multiple installed MAGIX instances are present in this session."; return state; }
                state.pid = matches[0].Id; state.process_start_utc_ticks = matches[0].StartTime.ToUniversalTime().Ticks;
                var snapshotClock = Stopwatch.StartNew();
                foreach (IntPtr hwnd in Native.WindowsFor(state.pid).OrderBy(w => Native.IsWindowEnabled(w) ? 0 : 1).ThenBy(w => Native.ClassName(w) == EditorWindowPolicy.EditorClass ? 0 : 1))
                {
                    var window = new MagixWindowSnapshot { hwnd = hwnd.ToInt64(), owner = Native.GetWindow(hwnd, 4).ToInt64(), root_owner = Native.GetAncestor(hwnd, 3).ToInt64(), title = Native.Title(hwnd), class_name = Native.ClassName(hwnd), enabled = Native.IsWindowEnabled(hwnd), elements = new List<MagixElementSnapshot>() };
                    if (includeControls && snapshotClock.ElapsedMilliseconds < 4500) ReadElements(window, state.pid, Math.Min(3000, 4500 - (int)snapshotClock.ElapsedMilliseconds));
                    else if (includeControls) { window.truncated = true; window.inspection_error = "Global snapshot time budget reached."; }
                    state.windows.Add(window);
                }
                var editors = state.windows.Where(w => w.class_name == EditorWindowPolicy.EditorClass).ToList();
                state.editor_hwnd = editors.Count == 1 ? editors[0].hwnd : 0;
                state.ready = state.interactive_ready && editors.Count == 1 && editors[0].enabled;
                state.reason = state.ready ? "Installed MAGIX editor is present in Buddy's interactive session." : "MAGIX is starting or a dialog blocks its editor; inspect the owned windows.";
                return state;
            }
            finally { foreach (Process p in matches) p.Dispose(); }
        }
        private static void ReadElements(MagixWindowSnapshot window, int pid, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var cache = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.None };
                AutomationProperty[] properties = { AutomationElement.ProcessIdProperty, AutomationElement.ControlTypeProperty, AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ClassNameProperty, AutomationElement.NativeWindowHandleProperty, AutomationElement.AcceleratorKeyProperty, AutomationElement.AccessKeyProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.HasKeyboardFocusProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsPasswordProperty, ValuePattern.ValueProperty };
                foreach (AutomationProperty property in properties) cache.Add(property);
                var patternProperties = new Dictionary<string, AutomationProperty> {
                    { "Invoke", AutomationElement.IsInvokePatternAvailableProperty }, { "Value", AutomationElement.IsValuePatternAvailableProperty },
                    { "Toggle", AutomationElement.IsTogglePatternAvailableProperty }, { "Selection", AutomationElement.IsSelectionPatternAvailableProperty },
                    { "SelectionItem", AutomationElement.IsSelectionItemPatternAvailableProperty }, { "ExpandCollapse", AutomationElement.IsExpandCollapsePatternAvailableProperty },
                    { "RangeValue", AutomationElement.IsRangeValuePatternAvailableProperty }, { "Text", AutomationElement.IsTextPatternAvailableProperty },
                    { "Window", AutomationElement.IsWindowPatternAvailableProperty }, { "Transform", AutomationElement.IsTransformPatternAvailableProperty },
                    { "Scroll", AutomationElement.IsScrollPatternAvailableProperty }, { "ScrollItem", AutomationElement.IsScrollItemPatternAvailableProperty }
                };
                foreach (AutomationProperty property in patternProperties.Values) cache.Add(property);
                // One provider-side subtree request replaces hundreds of cross-process
                // Current-property calls. The parent worker process still enforces the
                // hard deadline if a provider does not answer.
                AutomationElement root = AutomationElement.FromHandle(new IntPtr(window.hwnd)).GetUpdatedCache(cache);
                var queue = new Queue<Tuple<AutomationElement, int, int>>();
                queue.Enqueue(Tuple.Create(root, 0, -1));
                while (queue.Count > 0 && window.elements.Count < 500)
                {
                    var entry = queue.Dequeue(); AutomationElement element = entry.Item1; var info = element.Cached;
                    if (info.ProcessId != pid) continue;
                    int index = window.elements.Count; var bounds = info.BoundingRectangle;
                    var patterns = patternProperties.Where(p => Object.Equals(element.GetCachedPropertyValue(p.Value, true), true)).Select(p => p.Key).ToArray();
                    string value = info.IsPassword ? null : element.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
                    window.elements.Add(new MagixElementSnapshot { index = index, parent_index = entry.Item3, depth = entry.Item2, type = info.ControlType.ProgrammaticName.Replace("ControlType.", ""), name = info.IsPassword ? "[password]" : Limit(info.Name), automation_id = Limit(info.AutomationId), class_name = Limit(info.ClassName), hwnd = info.NativeWindowHandle, accelerator_key = Limit(info.AcceleratorKey), access_key = Limit(info.AccessKey), enabled = info.IsEnabled, offscreen = info.IsOffscreen, focused = info.HasKeyboardFocus, value = Limit(value), patterns = patterns, bounds = bounds.IsEmpty ? null : new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height } });
                    if (entry.Item2 >= 18) { window.truncated = true; continue; }
                    foreach (AutomationElement child in element.CachedChildren) queue.Enqueue(Tuple.Create(child, entry.Item2 + 1, index));
                }
                if (queue.Count > 0) window.truncated = true;
                if (clock.ElapsedMilliseconds > milliseconds) window.inspection_error = "Provider snapshot exceeded the requested soft time budget; cached fields are complete within the element bound.";
            }
            catch (Exception e) { window.inspection_error = e.Message; }
        }        private static string Limit(string value) { if (value == null) return null; return value.Length > 1024 ? value.Substring(0, 1024) : value; }
    }

    internal static class EditingNative
    {
        [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
        internal static string InputDesktop()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 0x0001);
            if (desktop == IntPtr.Zero) return "Unavailable";
            try { var value = new StringBuilder(256); int needed; return GetUserObjectInformation(desktop, 2, value, value.Capacity * 2, out needed) ? value.ToString() : "Unavailable"; }
            finally { CloseDesktop(desktop); }
        }
    }
}


