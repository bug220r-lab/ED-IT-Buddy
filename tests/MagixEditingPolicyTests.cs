using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EditBuddy;

internal static class MagixEditingPolicyTests
{
    private static int passed, failed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    public static int Main(string[] commandLine)
    {
        if (commandLine.Length == 1 && commandLine[0] == "--fixture-wait") { Console.WriteLine("ready"); System.Threading.Thread.Sleep(30000); return 0; }
        string root = Path.Combine(Path.GetTempPath(), "buddy-edit-policy-" + Guid.NewGuid().ToString("N"));
        Test("Only live-verified save and export operations are advertised", delegate { Check(EditingCommandPolicy.Capabilities().Count == 7 && EditingCommandPolicy.Capabilities().Where(c => c.supported).Select(c => c.action).OrderBy(x => x).SequenceEqual(new[] { "export_video", "save_project" }), "Unverified timeline operations must remain gated."); });
        Test("Unknown command and injected input arguments are rejected", delegate {
            Check(EditingCommandPolicy.Validate("type_text", new Dictionary<string, object>(), root) != null, "Raw input command accepted.");
            Check(EditingCommandPolicy.Validate("save_project", new Dictionary<string, object> { { "project_path", Path.Combine(root, "Demo.MVP") }, { "keys", "CTRL+A" } }, root) != null, "Extra argument accepted.");
        });
        Test("Ordinary workspace path accepted; sibling and parent escape rejected", delegate {
            Check(EditingCommandPolicy.IsWithin(root, Path.Combine(root, "Demo.MVP")), "Valid path rejected.");
            Check(!EditingCommandPolicy.IsWithin(root, Path.Combine(root + "-other", "Demo.MVP")), "Prefix collision accepted.");
            Check(!EditingCommandPolicy.IsWithin(root, Path.Combine(root, "..", "Demo.MVP")), "Traversal accepted.");
            Check(!EditingCommandPolicy.IsWithin(root, Path.Combine(root, "Demo.MVP:stream")), "Alternate data stream accepted.");
            Check(!EditingCommandPolicy.IsWithin(root, "\\\\MSI\\share\\Demo.MVP"), "UNC path accepted.");
        });
        Test("Frame ranges preserve integer and half-open semantics", delegate {
            var args = new Dictionary<string, object> { { "clip_id", "B" }, { "source_in_frame", 30 }, { "source_out_frame", 120 } };
            Check(EditingCommandPolicy.Validate("trim_clip", args, root) == null, "Valid frame trim rejected.");
            args["source_out_frame"] = 30; Check(EditingCommandPolicy.Validate("trim_clip", args, root) != null, "Empty range accepted.");
            args["source_out_frame"] = 29; Check(EditingCommandPolicy.Validate("trim_clip", args, root) != null, "Reverse range accepted.");
            args["source_out_frame"] = 90.5; Check(EditingCommandPolicy.Validate("trim_clip", args, root) != null, "Fractional frame accepted.");
            args["source_out_frame"] = "120"; Check(EditingCommandPolicy.Validate("trim_clip", args, root) != null, "String numeric coercion accepted.");
        });
        Test("Frame to timecode conversion does not round through seconds", delegate {
            Check(EditingCommandPolicy.Timecode(59, 30) == "00:00:01:29", "Frame boundary rounded.");
            Check(EditingCommandPolicy.Timecode(60, 30) == "00:00:02:00", "Second boundary incorrect.");
            Check(EditingCommandPolicy.Timecode(108001, 30) == "01:00:00:01", "Hour boundary incorrect.");
        });
        Test("Worker lifetime ends only the owned disposable process", delegate {
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            Process owned = null, unrelated = null;
            try
            {
                owned = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = "--fixture-wait", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                unrelated = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = "--fixture-wait", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                using (var lifetime = new EditingWorkerLifetime())
                {
                    lifetime.Attach(owned.Handle);
                    Check(owned.StandardOutput.ReadLine() == "ready" && unrelated.StandardOutput.ReadLine() == "ready", "Fixture failed to start.");
                }
                Check(owned.WaitForExit(3000), "Owned worker survived job handle closure.");
                Check(!unrelated.HasExited, "Unrelated process was terminated.");
            }
            finally
            {
                foreach (Process process in new[] { owned, unrelated }) if (process != null) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } finally { process.Dispose(); } }
            }
        });
        Console.WriteLine("Results: " + passed + " passed, " + failed + " failed. No Windows UI APIs were called.");
        return failed == 0 ? 0 : 1;
    }
}
