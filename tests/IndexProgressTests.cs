using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EditBuddy;

internal static class IndexProgressTests
{
    private static int checks;
    private static Dictionary<string, object> D(params object[] pairs) { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1]; return result; }
    private static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
    private static Dictionary<string, object> Task(string id, string state, int file, double seconds, double duration, int total)
    {
        return D("task_id", id, "kind", "index", "status", state, "params", D("root_ids", new[] { "root-icloud" }), "remote", D("updated", 1234d, "progress", D("indexed_files", file, "indexed_seconds", seconds, "source_duration_seconds", duration, "total_files", total, "current_file", "MVI_0216.MP4")));
    }
    private static Dictionary<string, object> Snapshot(params Dictionary<string, object>[] tasks) { return D("status", D("msi", "Connected"), "sources", new[] { D("source_id", "root-icloud", "label", "iCloud band footage") }, "tasks", tasks); }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var current = Task("icloud", "running", 10, 70.303567, 70.303567, 421);
            var state = IndexProgressState.From(current, true, false);
            Check(state.Count.StartsWith("File 11 of 421"), "Zero-based worker position must be shown as File11");
            Check(Math.Abs(state.Percent - 11d / 421 * 100) < .000001, "Completed current clip contributes exactly one file");
            Check(!state.Count.Contains("indexed"), "Position is not falsely called verified file coverage");
            Check(state.File.Contains("70.3 / 70.3 seconds"), "Current clip detail retained");
            state = IndexProgressState.From(Task("half", "running", 10, 30, 60, 421), true, false);
            Check(Math.Abs(state.Percent - 10.5d / 421 * 100) < .000001, "Partial current clip contributes a fraction");
            var skip = Task("skip", "running", 11, 70, 70, 421);
            IndexProgressState.Map(IndexProgressState.Get(IndexProgressState.Map(IndexProgressState.Get(skip, "remote")), "progress"))["stage"] = "Skipped MAGIX proxy; original already indexed";
            state = IndexProgressState.From(skip, true, false);
            Check(state.Count.StartsWith("Checkpoint: 11 of 421 files") && Math.Abs(state.Percent - 11d / 421 * 100) < .000001, "Skip count is not advanced twice");
            var range = Task("range", "running", 0, 45, 120, 1);
            range["params"] = D("start_seconds", 30, "end_seconds", 60);
            Check(Math.Abs(IndexProgressState.From(range, true, false).Percent - 50) < .000001, "Fine range uses range-relative progress");
            var staleStage = Task("next-proxy", "running", 11, 30, 60, 421);
            IndexProgressState.Map(IndexProgressState.Get(IndexProgressState.Map(IndexProgressState.Get(staleStage, "remote")), "progress"))["stage"] = "Skipped MAGIX proxy; original already indexed";
            state = IndexProgressState.From(staleStage, true, false);
            Check(state.Count.StartsWith("Checkpoint: 11") && !state.File.Contains("seconds"), "Stale skip stage stays a conservative checkpoint");
            state = IndexProgressState.From(Task("unknown", "running", 0, 0, 0, 0), true, false);
            Check(state.Indeterminate && !state.Count.Contains("%"), "Unknown active total has no invented percentage");
            state = IndexProgressState.From(Task("queue", "queued", 0, 0, 0, 0), true, false);
            Check(!state.Indeterminate && state.State == "Queued", "Queue does not imply processing");
            state = IndexProgressState.From(Task("pause", "paused", 10, 30, 60, 421), true, false);
            Check(state.State == "Paused" && !state.Indeterminate && state.Percent > 0, "Paused work retains determinate progress");
            state = IndexProgressState.From(current, false, false);
            Check(state.State.Contains("offline") && state.State.Contains("last known") && !state.Indeterminate, "Offline cache is explicit and does not animate");
            state = IndexProgressState.From(current, true, true);
            Check(state.State.StartsWith("Updates paused"), "Stale UI observation is not misrepresented as worker failure");
            var review = Task("review", "needs_review", 3, 1, 2, 5);
            review["results"] = D("manifest", D("results", new[] { D(), D() }, "skipped", new[] { D() }, "failures", new[] { D(), D() }));
            state = IndexProgressState.From(review, true, false);
            Check(state.Percent == 100 && state.State == "Needs review" && state.Attention, "Finished processing with failures is still needs review");
            Check(state.Detail == "2 indexed · 1 skipped · 2 need review", "Worker outcomes are separated");
            Check(!state.State.Contains("Completed"), "Needs review does not claim completion verification");
            var cached = Task("cached", "needs_review", 0, 0, 0, 0); cached["results"] = review["results"];
            Check(IndexProgressState.From(cached, true, false).Count.StartsWith("5 files processed"), "Terminal manifest supplies missing total without claiming success");
            state = IndexProgressState.From(Task("bad", "running", -5, Double.NaN, Double.PositiveInfinity, 421), true, false);
            Check(!Double.IsNaN(state.Percent) && state.Percent == 0, "Nonfinite and negative worker values are bounded");
            state = IndexProgressState.From(Task("huge", "running", 999, 500, 10, 2), true, false);
            Check(state.Percent == 100 && state.Count.StartsWith("File 2 of 2"), "Overflow observations remain bounded");
            state = IndexProgressState.From(Task("failed", "failed", 2, 30, 60, 421), true, false);
            Check(state.Attention && state.Percent < 100 && state.State == "Failed", "Failed early is not presented as fully processed");
            Check(IndexProgressState.From(null, false, false).Detail.Contains("Connect / refresh"), "Disconnected empty card explains next action");
            using (var panel = new IndexProgressPanel())
            {
                var finished = Task("finished", "completed", 421, 0, 0, 421);
                panel.Update(Snapshot(finished, current));
                Check(panel.SelectedTaskId == "icloud", "Active job selected ahead of old completed library job");
                var duplicateA = Task("library-11111111", "completed", 1, 0, 0, 1);
                var duplicateB = Task("library-22222222", "completed", 1, 0, 0, 1);
                using (var duplicatePanel = new IndexProgressPanel()) {
                    duplicatePanel.Update(Snapshot(duplicateA, duplicateB));
                    var duplicateChoices = (ComboBox)typeof(IndexProgressPanel).GetField("chooser", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(duplicatePanel);
                    Check(duplicateChoices.Items[0].ToString() != duplicateChoices.Items[1].ToString(), "Discovered repeated library jobs have distinct labels");
                }
                var choices = (ComboBox)typeof(IndexProgressPanel).GetField("chooser", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                Check(choices.SelectedItem.ToString().StartsWith("iCloud band footage · running · icloud"), "Friendly source label displayed");
                choices.SelectedIndex = 1;
                panel.Update(Snapshot(finished, current));
                Check(panel.SelectedTaskId == "finished", "Explicit user selection survives refresh");
                panel.MarkDisconnected("Connection closed");
                Check(panel.Current.State.Contains("last known"), "Broker disconnect freezes observation explicitly");
                panel.Update(Snapshot(current));
                Check(panel.SelectedTaskId == "icloud", "Removed selection recovers to active job");
                Render(panel, args[0], 1000, "running");
                Render(panel, args[0], 700, "running-narrow");
                panel.Update(Snapshot(review)); Render(panel, args[0], 1000, "needs-review");
                panel.MarkDisconnected("MSI unavailable"); Render(panel, args[0], 1000, "offline");
            }
            using (var panel = new IndexProgressPanel())
            {
                var newer = Task("recent", "completed", 421, 0, 0, 421);
                var older = Task("older", "needs_review", 421, 0, 0, 421);
                IndexProgressState.Map(IndexProgressState.Get(newer, "remote"))["updated"] = 5000d;
                IndexProgressState.Map(IndexProgressState.Get(older, "remote"))["updated"] = 4000d;
                older["updated_at"] = "2099-01-01T00:00:00Z";
                panel.Update(Snapshot(older, newer));
                Check(panel.SelectedTaskId == "recent", "Recent terminal choice follows worker time rather than local polling order");
            }
            Console.WriteLine("PASS: " + checks + " indexing status checks; previews " + args[0]); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Render(IndexProgressPanel panel, string root, int width, string name)
    {
        var window = new Window { Content = panel, Width = width, Height = 180 }; MatrixTheme.Apply(window);
        panel.Measure(new Size(width, 170)); panel.Arrange(new Rect(0, 0, width, 170)); panel.UpdateLayout();
        Check(panel.DesiredSize.Width <= width + 1, "Status card fits width " + width);
        var bitmap = new RenderTargetBitmap(width, 170, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, name + ".png"))) encoder.Save(file);
        window.Content = null; window.Close();
    }
}
