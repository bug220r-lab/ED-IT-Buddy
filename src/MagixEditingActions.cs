using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
using System.Text;
using System.Windows.Automation;

namespace EditBuddy
{
    internal static class MagixEditingActions
    {
        internal static PortalActionResult Execute(EditingWorkerRequest request, MagixDesktopState state, Action markInput)
        {
            if (request.action == "create_project") return CreateProject(request, state, markInput);
            if (request.action == "save_project") return SaveProject(request, state, markInput);
            if (request.action == "export_video") return ExportVideo(request, state, markInput);
            // This is deliberately fail-closed until fresh MSI observations establish
            // each operation's selectors and independently observable postconditions.
            return MagixEditingRunner.Failure("unsupported", "This editing operation is not yet live-validated on MSI. No input was sent.", false);
        }
        private static PortalActionResult ExportVideo(EditingWorkerRequest request, MagixDesktopState state, Action markInput)
        {
            string path = (string)request.args["output_path"];
            if (File.Exists(path)) return MagixEditingRunner.Failure("blocked", "The requested export already exists; no overwrite is permitted.", false);
            var ui = new MagixEditingUi(state, markInput); IntPtr editor = new IntPtr(state.editor_hwnd);
            IntPtr dialog = ui.FindWindow("MPEG-4 export", "#32770", true);
            if (dialog == IntPtr.Zero)
            {
                ui.Invoke(editor, "5819", "File", ControlType.Button); IntPtr menu = IntPtr.Zero;
                if (!MagixEditingUi.Wait(delegate { menu = ui.FindWindow(null, "#32768", true); return menu != IntPtr.Zero; }, 2000)) throw new InvalidOperationException("The File menu did not open.");
                ui.Expand(menu, "Item 9"); Thread.Sleep(150); ui.Invoke(menu, "Item 34903", "", ControlType.MenuItem);
                if (!MagixEditingUi.Wait(delegate { dialog = ui.FindWindow("MPEG-4 export", "#32770", true); return dialog != IntPtr.Zero; }, 8000)) throw new InvalidOperationException("The MPEG-4 export dialog did not open.");
            }
            if (ui.Unique(dialog, "108", null, ControlType.Text, false).Current.Name != "640x360" || ui.Unique(dialog, "105", null, ControlType.Text, false).Current.Name != "30" || ui.Unique(dialog, "106", null, ControlType.Text, false).Current.Name != "Progressive") throw new InvalidOperationException("The demonstrated export adapter requires640x360,30fps progressive readback.");
            ui.SetNativeCheck(dialog, "1002", "Overwrite file without a warning message", false);
            ui.SetNativeCheck(dialog, "1010", "Export selected range only", false);
            ui.SetNativeCheck(dialog, "1011", "Shut down PC automatically after successful export", false);
            ui.SetValue(dialog, "1000", null, ControlType.Edit, path);
            if (ui.ReadValue(dialog, "1000", null, ControlType.Edit) != path || File.Exists(path)) throw new InvalidOperationException("The export destination changed or already exists.");
            string movie = Native.Title(editor); var before = MagixEditingWorkerMain.Snapshot(true);
            ui.Invoke(dialog, "1", "OK", ControlType.Button);
            long size = -1; DateTime changed = DateTime.UtcNow; var timer = Stopwatch.StartNew(); bool complete = false;
            while (timer.ElapsedMilliseconds < 120000)
            {
                ui.VerifyProcess();
                if (File.Exists(path))
                {
                    long current = new FileInfo(path).Length;
                    if (current != size) { size = current; changed = DateTime.UtcNow; }
                    if (size > 32 && DateTime.UtcNow - changed > TimeSpan.FromSeconds(2) && !Native.IsWindowVisible(dialog) && Native.IsWindowEnabled(editor)) { complete = true; break; }
                }
                Thread.Sleep(100);
            }
            if (!complete) throw new InvalidOperationException("MAGIX export completion could not be verified; an export already underway may continue.");
            byte[] header = new byte[12]; using (var stream = File.OpenRead(path)) if (stream.Read(header, 0, header.Length) != header.Length || Encoding.ASCII.GetString(header, 4, 4) != "ftyp") throw new InvalidOperationException("The completed file has no MP4 ftyp header.");
            string hash; using (var stream = File.OpenRead(path)) using (var algorithm = SHA256.Create()) hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new PortalActionResult { status = "verified_success", input_sent = true, message = "MAGIX completed the MP4 export at the exact read-back destination. Independent frame and content verification remains required.", artifacts = new List<string> { path }, evidence = new { output_path = path, sha256 = hash, bytes = size, movie_title = movie, format_dialog = before, independent_content_verified = false } };
        }
        private static PortalActionResult SaveProject(EditingWorkerRequest request, MagixDesktopState state, Action markInput)
        {
            string path = (string)request.args["project_path"];
            var ui = new MagixEditingUi(state, markInput); IntPtr editor = new IntPtr(state.editor_hwnd);
            string currentName = ProjectTitleName(Native.Title(editor)); string expectedName = Path.GetFileName(path);
            if (currentName == null) return MagixEditingRunner.Failure("blocked", "The active MAGIX project name is unavailable.", false);
            bool update = File.Exists(path);
            if (update && !String.Equals(currentName, expectedName, StringComparison.OrdinalIgnoreCase)) return MagixEditingRunner.Failure("blocked", "Updating an existing file requires the matching active owned project.", false);
            if (!String.Equals(currentName, expectedName, StringComparison.OrdinalIgnoreCase)) EditingProjectOwnership.ValidateOwnedProject(request.workspace, Path.Combine(request.workspace, currentName), state);
            EditingProjectSaveTicket ticket = update ? EditingProjectOwnership.PrepareUpdate(request.workspace, path, state) : EditingProjectOwnership.PrepareInitial(request.workspace, path, state);
            if (update && Native.Title(editor).IndexOf('*') < 0)
                return new PortalActionResult { status = "verified_success", input_sent = false, message = "The owned project is already saved and its file matches the retained native-save receipt.", artifacts = new List<string> { path }, evidence = EditingProjectOwnership.ValidateOwnedProject(request.workspace, path, state) };
            IntPtr dialog = ui.FindWindow("Save project...", "#32770", true);
            if (dialog == IntPtr.Zero)
            {
                ui.Invoke(editor, "5819", "File", ControlType.Button);
                IntPtr menu = IntPtr.Zero;
                if (!MagixEditingUi.Wait(delegate { menu = ui.FindWindow(null, "#32768", true); return menu != IntPtr.Zero; }, 2000)) throw new InvalidOperationException("The File menu did not open.");
                if (update) ui.Invoke(menu, "Item 36792", "", ControlType.MenuItem, delegate { EditingProjectOwnership.RecheckBeforeSave(ticket, state); });
                else
                {
                    ui.Invoke(menu, "Item 36793", "", ControlType.MenuItem);
                    if (!MagixEditingUi.Wait(delegate { dialog = ui.FindWindow("Save project...", "#32770", true); return dialog != IntPtr.Zero; }, 8000)) throw new InvalidOperationException("The Save project dialog did not open.");
                }
            }
            if (!update)
            {
                ui.SetValue(dialog, "1001", "File name:", ControlType.Edit, path);
                if (ui.ReadValue(dialog, "1001", "File name:", ControlType.Edit) != path) throw new InvalidOperationException("Save destination readback changed.");
                ui.Invoke(dialog, "1", "Save", ControlType.Button, delegate { EditingProjectOwnership.RecheckBeforeSave(ticket, state); });
            }
            if (!MagixEditingUi.Wait(delegate { return File.Exists(path) && new FileInfo(path).Length > 0 && (dialog == IntPtr.Zero || !Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog)) && ProjectTitleName(Native.Title(editor)) == expectedName && Native.Title(editor).IndexOf('*') < 0; }, 10000)) throw new InvalidOperationException("The project file and clean editor title were not verified after Save.");
            EditingProjectReceipt receipt = EditingProjectOwnership.CommitVerifiedSave(ticket, state);
            return new PortalActionResult { status = "verified_success", input_sent = true, message = "MAGIX saved the owned project and retained a hash-bound ownership receipt. Reopen verification remains separate.", artifacts = new List<string> { path }, evidence = new { receipt = receipt, editor_title = Native.Title(editor), reopen_verified = false } };
        }
        private static string ProjectTitleName(string title)
        {
            const string prefix = "Movie Edit Pro 2021 Plus Steam Edition - ";
            if (title == null || !title.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string name = title.Substring(prefix.Length); int suffix = name.IndexOf(" ( ", StringComparison.Ordinal);
            if (suffix >= 0) name = name.Substring(0, suffix);
            name = name.TrimEnd('*');
            return Path.GetFileName(name) == name && Path.GetExtension(name).Equals(".MVP", StringComparison.OrdinalIgnoreCase) ? name : null;
        }

        private static PortalActionResult CreateProject(EditingWorkerRequest request, MagixDesktopState state, Action markInput)
        {
            string path = (string)request.args["project_path"];
            if (File.Exists(path)) return MagixEditingRunner.Failure("blocked", "The project path already exists. The demonstration will not overwrite it.", false);
            var ui = new MagixEditingUi(state, markInput);
            IntPtr dialog = ui.FindWindow("MAGIX Movie Edit Pro 2021 Plus Steam Edition", "#32770", true);
            if (dialog == IntPtr.Zero) return MagixEditingRunner.Failure("blocked", "A fresh MAGIX startup dialog is required. An existing project will not be replaced.", false);
            if (!ui.Exists(dialog, null, "Create project", ControlType.Button))
            {
                ui.Invoke(dialog, null, "New project", ControlType.TabItem);
                if (!MagixEditingUi.Wait(delegate { return ui.Exists(dialog, null, "Create project", ControlType.Button); }, 1200))
                    ui.ClickElement(dialog, null, "New project", ControlType.TabItem);
            }
            bool opened = MagixEditingUi.Wait(delegate { return ui.Exists(dialog, null, "Create project", ControlType.Button); }, 1500);
            if (!opened) return new PortalActionResult { status = "uncertain_after_input", input_sent = true, message = "New project activation did not produce the expected Create project button. No create command was issued.", evidence = MagixEditingWorkerMain.Snapshot(true) };
            string name = Path.GetFileNameWithoutExtension(path);
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            ui.EnsureStartupFolderEnabled(dialog);
            ui.SetStartupValue(dialog, false, name);
            ui.SetStartupValue(dialog, true, folder);
            ui.SetToggle(dialog, "Open movie settings", true);
            if (ui.StartupValue(dialog, false) != name || ui.StartupValue(dialog, true) != folder) throw new InvalidOperationException("New project destination readback changed before creation.");
            ui.ClickElement(dialog, null, "Create project", ControlType.Button);
            bool closed = MagixEditingUi.Wait(delegate { return !Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog); }, 4000);
            return new PortalActionResult {
                status = closed ? "blocked" : "uncertain_after_input", input_sent = true,
                message = closed ? "The requested project name and folder were read back, then Create project was activated. Waiting for a validated movie-settings adapter before declaring the requested project format complete." : "Create project was activated after destination readback, but the startup dialog remains. Inspect the result before further input.",
                evidence = MagixEditingWorkerMain.Snapshot(true)
            };
        }
    }
}
