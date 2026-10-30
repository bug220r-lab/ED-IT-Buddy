using System;
using System.IO;
using System.Security.Cryptography;
using EditBuddy;

internal static class MagixOwnershipTests
{
    private static int assertions;
    private static string root;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }
    private static MagixDesktopState Owner() { return new MagixDesktopState { host = "MSI", pid = 427, process_start_utc_ticks = 638500000000000000, session_id = 1, executable = Path.Combine(root, "normal-magix", "Videodeluxe.exe") }; }
    private static string Workspace(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static string Hash(string path) { using (var file = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
    private static EditingProjectSaveTicket Initial(string workspace)
    {
        var ticket = EditingProjectOwnership.PrepareInitial(workspace, Path.Combine(workspace, "demo.MVP"), Owner());
        File.WriteAllText(ticket.project_path, "Native save fixture one."); EditingProjectOwnership.CommitVerifiedSave(ticket, Owner()); return ticket;
    }
    public static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            string first = Workspace("initial"), project = Path.Combine(first, "demo.MVP");
            var initial = EditingProjectOwnership.PrepareInitial(first, project, Owner());
            Check(!File.Exists(initial.receipt_path), "Preparation claimed a saved project.");
            EditingProjectOwnership.RecheckBeforeSave(initial, Owner());
            Reject(() => EditingProjectOwnership.CommitVerifiedSave(initial, Owner()), "Missing native save accepted.");
            File.WriteAllText(project, ""); Reject(() => EditingProjectOwnership.CommitVerifiedSave(initial, Owner()), "Empty native save accepted.");
            File.WriteAllText(project, "Native save fixture one.");
            var receipt = EditingProjectOwnership.CommitVerifiedSave(initial, Owner());
            Check(receipt.sha256 == Hash(project) && receipt.generation == 1 && File.Exists(initial.receipt_path), "Initial receipt did not bind the saved bytes.");
            Check(receipt.magix_pid == Owner().pid && receipt.session_id == 1 && receipt.project_path == project, "Receipt lost process/path identity.");
            string initialReceipt = File.ReadAllText(initial.receipt_path);
            Check(EditingProjectOwnership.ValidateOwnedProject(first, project, Owner()).sha256 == receipt.sha256, "Read-only validation did not return exact ownership.");
            Check(File.ReadAllText(initial.receipt_path) == initialReceipt && !Directory.Exists(Path.Combine(first, ".buddy", "project-backups")), "Read-only validation created a backup or changed provenance.");
            Reject(() => EditingProjectOwnership.CommitVerifiedSave(initial, Owner()), "Committed ticket reused.");
            Reject(() => EditingProjectOwnership.PrepareInitial(first, project, Owner()), "Initial save accepted existing project.");
            var update = EditingProjectOwnership.PrepareUpdate(first, project, Owner());
            Check(File.Exists(update.backup_path) && Hash(update.backup_path) == receipt.sha256, "No exact backup before overwrite authorization.");
            EditingProjectOwnership.RecheckBeforeSave(update, Owner()); File.WriteAllText(project, "Native save fixture two with changed timeline.");
            var updated = EditingProjectOwnership.CommitVerifiedSave(update, Owner());
            Check(updated.generation == 2 && updated.sha256 == Hash(project) && updated.backup_path == update.backup_path, "Updated receipt did not bind new file and backup.");
            var repeated = EditingProjectOwnership.PrepareUpdate(first, project, Owner());
            Check(repeated.backup_path != update.backup_path && Hash(repeated.backup_path) == updated.sha256, "Repeated save replaced an earlier backup.");

            string external = Workspace("external-change"); var externalInitial = Initial(external); string receiptBefore = File.ReadAllText(externalInitial.receipt_path);
            File.WriteAllText(externalInitial.project_path, "An external edit must be preserved.");
            Reject(() => EditingProjectOwnership.ValidateOwnedProject(external, externalInitial.project_path, Owner()), "Read-only validation accepted external changes.");
            Reject(() => EditingProjectOwnership.PrepareUpdate(external, externalInitial.project_path, Owner()), "Externally changed project authorized.");
            Check(File.ReadAllText(externalInitial.receipt_path) == receiptBefore, "Failed preparation changed provenance.");
            File.Delete(externalInitial.project_path); Reject(() => EditingProjectOwnership.PrepareUpdate(external, externalInitial.project_path, Owner()), "Removed project authorized.");
            string unowned = Workspace("unowned"), unownedPath = Path.Combine(unowned, "user-project.MVP"); File.WriteAllText(unownedPath, "Unrelated project.");
            Reject(() => EditingProjectOwnership.PrepareUpdate(unowned, unownedPath, Owner()), "Unowned file authorized.");
            Check(File.ReadAllText(unownedPath) == "Unrelated project.", "Unowned content changed.");

            string identities = Workspace("identity"); var bound = Initial(identities);
            foreach (string field in new[] { "pid", "start", "session", "executable", "host" })
            {
                var other = Owner();
                if (field == "pid") other.pid++; else if (field == "start") other.process_start_utc_ticks++;
                else if (field == "session") other.session_id++; else if (field == "executable") other.executable += ".other"; else other.host = "THIS-MACHINE";
                Reject(() => EditingProjectOwnership.PrepareUpdate(identities, bound.project_path, other), "Changed identity accepted: " + field);
            }
            string copiedPath = Path.Combine(identities, "another.MVP"); File.Copy(bound.project_path, copiedPath);
            Reject(() => EditingProjectOwnership.PrepareUpdate(identities, copiedPath, Owner()), "Matching bytes at another path inherited ownership.");
            string late = Workspace("late-change"); var lateInitial = Initial(late); var pending = EditingProjectOwnership.PrepareUpdate(late, lateInitial.project_path, Owner());
            File.WriteAllText(pending.project_path, "Modified while native dialog was open.");
            Reject(() => EditingProjectOwnership.RecheckBeforeSave(pending, Owner()), "Change during dialog work passed final check.");
            Check(Hash(pending.backup_path) == pending.prior_sha256, "Blocked update lost original backup.");
            var fresh = EditingProjectOwnership.PrepareInitial(late, Path.Combine(late, "fresh.MVP"), Owner()); File.WriteAllText(fresh.project_path, "Appeared during dialog work.");
            Reject(() => EditingProjectOwnership.RecheckBeforeSave(fresh, Owner()), "Appeared initial destination authorized.");
            string competing = Workspace("competing"); var source = Initial(competing);
            var a = EditingProjectOwnership.PrepareUpdate(competing, source.project_path, Owner()); var b = EditingProjectOwnership.PrepareUpdate(competing, source.project_path, Owner());
            File.WriteAllText(source.project_path, "First successful concurrent ticket."); EditingProjectOwnership.CommitVerifiedSave(a, Owner());
            Reject(() => EditingProjectOwnership.RecheckBeforeSave(b, Owner()), "Stale ticket authorized overwrite.");
            Reject(() => EditingProjectOwnership.CommitVerifiedSave(b, Owner()), "Stale ticket replaced newer provenance.");
            string modifiedReceipt = Workspace("modified-receipt"); var receiptSource = Initial(modifiedReceipt);
            var receiptPending = EditingProjectOwnership.PrepareUpdate(modifiedReceipt, receiptSource.project_path, Owner());
            File.AppendAllText(receiptSource.receipt_path, " ");
            Reject(() => EditingProjectOwnership.RecheckBeforeSave(receiptPending, Owner()), "Changed receipt passed final save check.");
            Reject(() => EditingProjectOwnership.CommitVerifiedSave(receiptPending, Owner()), "Changed receipt permitted stale provenance commit.");
            string wrongPath = Workspace("receipt-path"); var pathSource = Initial(wrongPath);
            File.WriteAllText(pathSource.receipt_path, File.ReadAllText(pathSource.receipt_path).Replace("demo.MVP", "other.MVP"));
            Reject(() => EditingProjectOwnership.ValidateOwnedProject(wrongPath, pathSource.project_path, Owner()), "Receipt for another path was accepted.");
            string backups = Workspace("backup-change"); var backupSource = Initial(backups); var tampered = EditingProjectOwnership.PrepareUpdate(backups, backupSource.project_path, Owner());
            File.WriteAllText(tampered.backup_path, "Changed backup.");
            Reject(() => EditingProjectOwnership.RecheckBeforeSave(tampered, Owner()), "Changed backup permitted overwrite.");
            Reject(() => EditingProjectOwnership.CommitVerifiedSave(tampered, Owner()), "Changed backup permitted commit.");

            string adoption = Workspace("adoption"), calibration = Path.Combine(adoption, "calibration.MVP"); File.WriteAllText(calibration, "Independently verified native calibration save.");
            Reject(() => EditingProjectOwnership.AdoptVerified(adoption, calibration, Owner(), new string('0', 64)), "Wrong hash adopted.");
            Reject(() => EditingProjectOwnership.AdoptVerified(adoption, calibration, Owner(), "short-hash"), "Incomplete hash adopted.");
            string proof = Hash(calibration); var adopted = EditingProjectOwnership.AdoptVerified(adoption, calibration, Owner(), proof);
            Check(adopted.sha256 == proof && adopted.provenance == "explicit_verified_calibration_adoption", "Adoption lost hash provenance.");
            var adoptedUpdate = EditingProjectOwnership.PrepareUpdate(adoption, calibration, Owner()); Check(Hash(adoptedUpdate.backup_path) == proof, "Adopted file could not authorize backed-up update.");
            Reject(() => EditingProjectOwnership.AdoptVerified(adoption, calibration, Owner(), proof), "Adoption replaced existing receipt.");
            string paths = Workspace("paths");
            Reject(() => EditingProjectOwnership.PrepareInitial(paths, Path.Combine(root, "escape.MVP"), Owner()), "Sibling path escaped workspace.");
            Reject(() => EditingProjectOwnership.PrepareInitial(paths, Path.Combine(paths, "..", "escape.MVP"), Owner()), "Traversal escaped workspace.");
            Reject(() => EditingProjectOwnership.PrepareInitial(paths, Path.Combine(paths, "demo.MVP:stream"), Owner()), "Alternate data stream accepted.");
            Reject(() => EditingProjectOwnership.PrepareInitial(paths, "\\\\MSI\\share\\demo.MVP", Owner()), "UNC path accepted.");
            Check((File.GetAttributes(Path.Combine(root, "reparse", "link")) & FileAttributes.ReparsePoint) != 0, "Actual junction fixture missing.");
            Reject(() => EditingProjectOwnership.PrepareInitial(Path.Combine(root, "reparse"), Path.Combine(root, "reparse", "link", "demo.MVP"), Owner()), "Reparse-point path accepted.");
            Console.WriteLine("PASS: " + assertions + " project ownership assertions. No MAGIX or MSI access."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
