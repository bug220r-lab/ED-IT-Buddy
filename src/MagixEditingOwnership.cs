using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace EditBuddy
{
    internal sealed class EditingProjectReceipt
    {
        public int schema { get; set; }
        public string project_path { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
        public string host { get; set; }
        public int magix_pid { get; set; }
        public long magix_start_utc_ticks { get; set; }
        public int session_id { get; set; }
        public string executable { get; set; }
        public int generation { get; set; }
        public string saved_utc { get; set; }
        public string backup_path { get; set; }
        public string provenance { get; set; }
    }

    // A ticket is internal to one verified native save. It never authorizes an
    // arbitrary file overwrite through MCP and cannot be reused after commit.
    internal sealed class EditingProjectSaveTicket
    {
        internal string workspace, receipt_path, receipt_sha256;
        internal EditingProjectReceipt previous, identity;
        internal bool initial, committed;
        internal string project_path { get; set; }
        internal string prior_sha256 { get; set; }
        internal string backup_path { get; set; }
    }

    internal static class EditingProjectOwnership
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 65536 };
        private static readonly object Gate = new object();

        internal static EditingProjectReceipt ValidateOwnedProject(string workspace, string projectPath, MagixDesktopState target)
        {
            lock (Gate)
            {
                EditingProjectSaveTicket ticket = NewTicket(workspace, projectPath, target);
                string receiptHash;
                EditingProjectReceipt receipt = ReadReceipt(ticket.receipt_path, out receiptHash);
                MatchIdentity(receipt, ticket.identity); MatchProject(ticket.project_path, receipt);
                if (HashFile(ticket.receipt_path) != receiptHash) throw new InvalidOperationException("The ownership receipt changed during validation.");
                return receipt;
            }
        }

        internal static EditingProjectSaveTicket PrepareInitial(string workspace, string projectPath, MagixDesktopState target)
        {
            lock (Gate)
            {
                EditingProjectSaveTicket ticket = NewTicket(workspace, projectPath, target);
                ticket.initial = true;
                if (File.Exists(ticket.project_path) || File.Exists(ticket.receipt_path)) throw new InvalidOperationException("An initial save requires a new project path with no ownership receipt.");
                return ticket;
            }
        }

        internal static EditingProjectSaveTicket PrepareUpdate(string workspace, string projectPath, MagixDesktopState target)
        {
            lock (Gate)
            {
                EditingProjectSaveTicket ticket = NewTicket(workspace, projectPath, target);
                string receiptHash;
                ticket.previous = ReadReceipt(ticket.receipt_path, out receiptHash);
                MatchIdentity(ticket.previous, ticket.identity);
                ticket.receipt_sha256 = receiptHash;
                ticket.prior_sha256 = ticket.previous.sha256;
                MatchProject(ticket.project_path, ticket.previous);
                string backupFolder = SafePath(ticket.workspace, Path.Combine(ticket.workspace, ".buddy", "project-backups"), true);
                Directory.CreateDirectory(backupFolder);
                ticket.backup_path = SafePath(ticket.workspace, Path.Combine(backupFolder, Path.GetFileNameWithoutExtension(ticket.project_path) + "." + Guid.NewGuid().ToString("N") + ".MVP"));
                using (var source = new FileStream(ticket.project_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var copy = new FileStream(ticket.backup_path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!String.Equals(HashStream(source), ticket.prior_sha256, StringComparison.Ordinal)) throw new InvalidOperationException("The project changed before its backup could be made.");
                    source.Position = 0; source.CopyTo(copy); copy.Flush(true);
                }
                if (HashFile(ticket.backup_path) != ticket.prior_sha256) throw new InvalidOperationException("The owned-project backup could not be verified.");
                RecheckBeforeSave(ticket, target);
                return ticket;
            }
        }

        // Call immediately before the native Save activation, after dialog work.
        internal static void RecheckBeforeSave(EditingProjectSaveTicket ticket, MagixDesktopState target)
        {
            lock (Gate)
            {
                CheckTicket(ticket, target);
                CheckReceiptUnchanged(ticket);
                if (ticket.initial)
                {
                    if (File.Exists(ticket.project_path)) throw new InvalidOperationException("The initial save destination appeared before Save.");
                }
                else
                {
                    MatchProject(ticket.project_path, ticket.previous);
                    if (HashFile(SafePath(ticket.workspace, ticket.backup_path)) != ticket.prior_sha256) throw new InvalidOperationException("The retained backup changed before Save.");
                }
            }
        }

        // Native success must have been established by the caller. A receipt
        // records that evidence's artifact; it does not itself prove UI success.
        internal static EditingProjectReceipt CommitVerifiedSave(EditingProjectSaveTicket ticket, MagixDesktopState target)
        {
            lock (Gate)
            {
                CheckTicket(ticket, target); CheckReceiptUnchanged(ticket);
                if (!ticket.initial && HashFile(SafePath(ticket.workspace, ticket.backup_path)) != ticket.prior_sha256) throw new InvalidOperationException("The retained backup changed during Save.");
                EditingProjectReceipt next = Identity(target, ticket.project_path);
                next.generation = ticket.initial ? 1 : checked(ticket.previous.generation + 1);
                next.backup_path = ticket.backup_path;
                next.provenance = ticket.initial ? "verified_initial_native_save" : "verified_native_update";
                ReadSavedFile(next);
                WriteReceipt(ticket, next);
                ticket.committed = true;
                return next;
            }
        }

        // Development reconciliation only: the caller supplies the exact hash
        // from independent native-save evidence. Never exposed as an MCP action.
        internal static EditingProjectReceipt AdoptVerified(string workspace, string projectPath, MagixDesktopState target, string expectedSha256)
        {
            lock (Gate)
            {
                if (!ValidHash(expectedSha256)) throw new InvalidOperationException("A complete independently verified SHA256 is required for adoption.");
                EditingProjectSaveTicket ticket = NewTicket(workspace, projectPath, target); ticket.initial = true;
                if (File.Exists(ticket.receipt_path)) throw new InvalidOperationException("A receipt already exists; adoption cannot replace it.");
                EditingProjectReceipt receipt = Identity(target, ticket.project_path);
                ReadSavedFile(receipt);
                if (!String.Equals(receipt.sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The calibration file does not match the supplied native-save evidence.");
                receipt.generation = 1; receipt.provenance = "explicit_verified_calibration_adoption";
                WriteReceipt(ticket, receipt); ticket.committed = true;
                return receipt;
            }
        }

        private static EditingProjectSaveTicket NewTicket(string workspace, string path, MagixDesktopState target)
        {
            if (String.IsNullOrWhiteSpace(workspace) || !Path.IsPathRooted(workspace) || !Directory.Exists(workspace)) throw new InvalidOperationException("An existing absolute Buddy workspace is required.");
            string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string project = SafePath(root, path);
            if (!String.Equals(Path.GetExtension(project), ".MVP", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Ownership receipts apply only to MAGIX MVP files.");
            string key; using (var hash = SHA256.Create()) key = Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(project.ToUpperInvariant())));
            string receipt = SafePath(root, Path.Combine(root, ".buddy", "project-ownership", key + ".json"));
            return new EditingProjectSaveTicket { workspace = root, project_path = project, receipt_path = receipt, identity = Identity(target, project) };
        }

        private static EditingProjectReceipt Identity(MagixDesktopState target, string project)
        {
            if (target == null || !String.Equals(target.host, "MSI", StringComparison.OrdinalIgnoreCase) || target.pid <= 0 || target.process_start_utc_ticks <= 0 || target.session_id <= 0 || String.IsNullOrWhiteSpace(target.executable) || !Path.IsPathRooted(target.executable)) throw new InvalidOperationException("A complete MSI MAGIX process identity is required.");
            return new EditingProjectReceipt { schema = 1, project_path = project, host = target.host, magix_pid = target.pid, magix_start_utc_ticks = target.process_start_utc_ticks, session_id = target.session_id, executable = Path.GetFullPath(target.executable) };
        }

        private static void MatchIdentity(EditingProjectReceipt recorded, EditingProjectReceipt current)
        {
            if (recorded == null || recorded.schema != 1 || recorded.generation < 1 || !ValidHash(recorded.sha256) || recorded.bytes <= 0 || !String.Equals(recorded.project_path, current.project_path, StringComparison.OrdinalIgnoreCase) || !String.Equals(recorded.host, current.host, StringComparison.OrdinalIgnoreCase) || recorded.magix_pid != current.magix_pid || recorded.magix_start_utc_ticks != current.magix_start_utc_ticks || recorded.session_id != current.session_id || !String.Equals(recorded.executable, current.executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The ownership receipt does not match this project and MAGIX process. No overwrite is authorized.");
        }

        private static void CheckTicket(EditingProjectSaveTicket ticket, MagixDesktopState target)
        {
            if (ticket == null || ticket.committed) throw new InvalidOperationException("This save ticket is missing or already committed.");
            SafePath(ticket.workspace, ticket.project_path); SafePath(ticket.workspace, ticket.receipt_path);
            EditingProjectReceipt current = Identity(target, ticket.project_path);
            if (ticket.identity.magix_pid != current.magix_pid || ticket.identity.magix_start_utc_ticks != current.magix_start_utc_ticks || ticket.identity.session_id != current.session_id || !String.Equals(ticket.identity.executable, current.executable, StringComparison.OrdinalIgnoreCase) || !String.Equals(ticket.identity.host, current.host, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The MAGIX identity changed while a save was pending.");
        }

        private static void CheckReceiptUnchanged(EditingProjectSaveTicket ticket)
        {
            if (ticket.initial)
            {
                if (File.Exists(ticket.receipt_path)) throw new InvalidOperationException("Another ownership receipt appeared while the save was pending.");
            }
            else if (HashFile(ticket.receipt_path) != ticket.receipt_sha256) throw new InvalidOperationException("The ownership receipt changed while the save was pending.");
        }

        private static string SafePath(string workspace, string path, bool allowDirectory = false)
        {
            if (!EditingCommandPolicy.IsWithin(workspace, path)) throw new InvalidOperationException("Project ownership paths must stay inside the Buddy workspace without reparse points.");
            string full = Path.GetFullPath(path);
            string cursor = full;
            while (!String.IsNullOrEmpty(cursor))
            {
                try { if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse points are not accepted in project ownership paths."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                cursor = Path.GetDirectoryName(cursor);
            }
            if (!allowDirectory && Directory.Exists(full)) throw new InvalidOperationException("A file path was required for project ownership.");
            return full;
        }

        private static EditingProjectReceipt ReadReceipt(string path, out string hash)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > 65536) throw new InvalidOperationException("A valid prior Buddy ownership receipt is required before updating a project.");
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer); byte[] bytes = buffer.ToArray();
                    using (var algorithm = SHA256.Create()) hash = Hex(algorithm.ComputeHash(bytes));
                    return Json.Deserialize<EditingProjectReceipt>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                }
            }
        }

        private static void MatchProject(string path, EditingProjectReceipt receipt)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != receipt.bytes || HashFile(path) != receipt.sha256) throw new InvalidOperationException("The owned project was removed or changed outside the verified save sequence. No overwrite is authorized.");
        }

        private static void ReadSavedFile(EditingProjectReceipt receipt)
        {
            using (var stream = new FileStream(receipt.project_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0) throw new InvalidOperationException("A verified save must produce a nonempty project file.");
                receipt.bytes = stream.Length; receipt.sha256 = HashStream(stream);
            }
            receipt.saved_utc = DateTime.UtcNow.ToString("o");
        }

        private static void WriteReceipt(EditingProjectSaveTicket ticket, EditingProjectReceipt receipt)
        {
            string folder = Path.GetDirectoryName(ticket.receipt_path);
            // SafePath checks existing ancestors before creation and again before write.
            Directory.CreateDirectory(folder); SafePath(ticket.workspace, ticket.receipt_path);
            string temporary = SafePath(ticket.workspace, Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N") + ".tmp"));
            byte[] content = Encoding.UTF8.GetBytes(Json.Serialize(receipt));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(content, 0, content.Length); stream.Flush(true); }
            CheckReceiptUnchanged(ticket);
            if (ticket.initial) File.Move(temporary, ticket.receipt_path);
            else File.Replace(temporary, ticket.receipt_path, null);
        }

        private static bool ValidHash(string value) { return value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z"); }
        private static string HashFile(string path) { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return HashStream(stream); }
        private static string HashStream(Stream stream) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    }
}
