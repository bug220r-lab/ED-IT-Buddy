using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EditBuddy
{
    internal sealed class IndexProgressState
    {
        internal string State, Count, File, Detail;
        internal double Percent;
        internal bool Indeterminate, Attention;

        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static object Get(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        internal static string Text(Dictionary<string, object> value, string key) { return Convert.ToString(Get(value, key), CultureInfo.InvariantCulture) ?? ""; }
        internal static double Number(Dictionary<string, object> value, string key, double fallback)
        {
            double number;
            return Double.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) ? number : fallback;
        }
        internal static List<Dictionary<string, object>> Rows(object value)
        {
            IEnumerable values = value as IEnumerable;
            return values == null || value is string ? new List<Dictionary<string, object>>() : values.Cast<object>().Select(Map).ToList();
        }
        internal static List<string> Strings(object value)
        {
            IEnumerable values = value as IEnumerable;
            return values == null || value is string ? new List<string>() : values.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
        }
        internal static string Identity(Dictionary<string, object> task) { return Text(task, "task_id").Length > 0 ? Text(task, "task_id") : Text(Map(Get(task, "remote")), "job_id"); }
        internal static string Source(Dictionary<string, object> task, List<Dictionary<string, object>> sources)
        {
            List<string> ids = Strings(Get(Map(Get(task, "params")), "root_ids"));
            var names = sources.Where(s => ids.Contains(Text(s, "source_id")) || ids.Contains(Text(s, "root_id"))).Select(s => Text(s, "label").Length > 0 ? Text(s, "label") : Text(s, "path")).ToList();
            return names.Count == 0 ? "Footage library" : String.Join(" + ", names.Take(2)) + (names.Count > 2 ? " + " + (names.Count - 2).ToString(CultureInfo.InvariantCulture) + " more" : "");
        }
        internal static int Rank(Dictionary<string, object> task)
        {
            switch (Text(task, "status")) { case "running": return 0; case "queued": return 1; case "paused": return 2; default: return 3; }
        }
        internal static double Updated(Dictionary<string, object> task)
        {
            double remote = Number(Map(Get(task, "remote")), "updated", 0);
            DateTime parsed;
            if (remote > 0) return remote; // Local updated_at is the last poll, not the worker's completion time.
            if (DateTime.TryParse(Text(task, "updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out parsed)) return (parsed.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return remote;
        }
        internal static IndexProgressState From(Dictionary<string, object> task, bool connected, bool stale)
        {
            var state = new IndexProgressState { State = "Ready", Count = "", File = "", Detail = "", Percent = 0 };
            if (task == null) { state.State = connected ? "No indexing tasks" : "Not connected"; state.Detail = connected ? "Register a footage folder to start indexing." : "Connect / refresh to load MSI indexing progress."; return state; }
            string status = Text(task, "status");
            var progress = Map(Get(Map(Get(task, "remote")), "progress"));
            var manifest = Map(Get(Map(Get(task, "results")), "manifest"));
            double total = Math.Max(0, Math.Floor(Number(progress, "total_files", 0)));
            double position = Math.Max(0, Number(progress, "indexed_files", 0));
            double duration = Number(progress, "source_duration_seconds", 0);
            double seconds = Math.Max(0, Number(progress, "indexed_seconds", 0));
            string stage = Text(progress, "stage");
            bool skipped = stage.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase);
            // The worker reports absolute source timestamps, including for a fine range.
            var parameters = Map(Get(task, "params"));
            double start = Math.Max(0, Number(parameters, "start_seconds", 0));
            double end = Math.Min(duration, Number(parameters, "end_seconds", duration));
            double fraction = end > start ? Math.Max(0, Math.Min(1, (seconds - start) / (end - start))) : 0;
            bool finished = status == "completed" || status == "needs_review";
            if (finished && total <= 0 && manifest.Count > 0) total = Rows(Get(manifest, "results")).Count + Rows(Get(manifest, "skipped")).Count + Rows(Get(manifest, "failures")).Count;
            state.Percent = total > 0 ? Math.Max(0, Math.Min(100, (position + (skipped ? 0 : fraction)) / total * 100)) : Math.Max(0, Math.Min(100, Number(task, "progress", 0) * 100));
            if (finished) state.Percent = 100;
            state.Indeterminate = connected && !stale && status == "running" && total <= 0;
            state.State = status == "needs_review" ? "Needs review" : status == "completed" ? "Finished" : status == "running" ? "Indexing on MSI" : status == "paused" ? "Paused" : status == "queued" ? "Queued" : status == "failed" ? "Failed" : status == "cancelled" ? "Cancelled" : "Status unavailable";
            state.Attention = status == "failed" || status == "needs_review" || status == "uncertain";
            if (total > 0)
            {
                double current = Math.Min(total, Math.Max(1, Math.Floor(position) + (skipped ? 0 : 1)));
                // Older workers retain their last stage after a skip. Cursor is still a
                // reliable lower bound; do not call it the current file or add stale seconds.
                state.Count = finished ? total.ToString("0", CultureInfo.InvariantCulture) + " files processed" : skipped ? "Checkpoint: " + Math.Min(position, total).ToString("0", CultureInfo.InvariantCulture) + " of " + total.ToString("0", CultureInfo.InvariantCulture) + " files" : "File " + current.ToString("0", CultureInfo.InvariantCulture) + " of " + total.ToString("0", CultureInfo.InvariantCulture);
                state.Count += " · " + state.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            }
            else state.Count = status == "running" ? "Preparing footage…" : "Waiting for file count";
            state.File = Text(progress, "current_file");
            if (duration > 0 && state.File.Length > 0 && !finished && !skipped) state.File += " · " + Math.Min(seconds, duration).ToString("0.#", CultureInfo.InvariantCulture) + " / " + duration.ToString("0.#", CultureInfo.InvariantCulture) + " seconds";
            if (finished && manifest.Count > 0)
                state.Detail = Rows(Get(manifest, "results")).Count + " indexed · " + Rows(Get(manifest, "skipped")).Count + " skipped · " + Rows(Get(manifest, "failures")).Count + " need review";
            else state.Detail = stage.Length > 0 ? "Last worker stage: " + stage : Text(task, "message");
            if (!connected || stale)
            {
                state.State = !connected ? "MSI offline · last known progress" : "Updates paused · last known progress";
                state.Indeterminate = false;
                state.Detail = (!connected ? "Reconnect to refresh. " : "Waiting for a fresh observation. ") + state.Detail;
            }
            return state;
        }
    }

    internal sealed class IndexProgressPanel : UserControl, IDisposable
    {
        private sealed class Choice
        {
            internal string Id, Label;
            public override string ToString() { return Label; }
        }
        private readonly ComboBox chooser = new ComboBox { MinWidth = 180, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock status = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = MatrixTheme.Accent };
        private readonly TextBlock count = new TextBlock { Foreground = MatrixTheme.Ink, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock file = new TextBlock { Foreground = MatrixTheme.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock detail = new TextBlock { Foreground = MatrixTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        private readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, Foreground = MatrixTheme.Accent, Background = MatrixTheme.Raised, BorderBrush = MatrixTheme.Line };
        private readonly DispatcherTimer staleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private List<Dictionary<string, object>> tasks = new List<Dictionary<string, object>>();
        private List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();
        private DateTime observed = DateTime.MinValue;
        private bool connected, rendering, userSelected, disconnected, disposed;
        private string selectedId = "";

        internal IndexProgressState Current { get; private set; }
        internal string SelectedTaskId { get { return selectedId; } }
        internal IndexProgressPanel()
        {
            var border = new Border { Background = MatrixTheme.Panel, BorderBrush = MatrixTheme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new Grid(); border.Child = grid; Content = border;
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status.Margin = new Thickness(0, 4, 14, 4); heading.Children.Add(status); Grid.SetColumn(chooser, 1); chooser.Margin = new Thickness(0, 0, 14, 4); heading.Children.Add(chooser); Grid.SetColumn(count, 2); count.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(count); grid.Children.Add(heading);
            bar.Margin = new Thickness(0, 3, 0, 5); Grid.SetRow(bar, 1); grid.Children.Add(bar);
            Grid.SetRow(file, 2); grid.Children.Add(file); Grid.SetRow(detail, 3); detail.Margin = new Thickness(0, 3, 0, 0); grid.Children.Add(detail);
            AutomationProperties.SetName(this, "MSI indexing status"); AutomationProperties.SetAutomationId(this, "AssistantIndexStatus");
            AutomationProperties.SetName(chooser, "Indexing task to track"); AutomationProperties.SetAutomationId(chooser, "AssistantIndexTask");
            AutomationProperties.SetName(bar, "Indexing progress"); AutomationProperties.SetAutomationId(bar, "AssistantIndexProgress");
            AutomationProperties.SetAutomationId(status, "AssistantIndexState"); AutomationProperties.SetAutomationId(count, "AssistantIndexCount"); AutomationProperties.SetAutomationId(file, "AssistantIndexFile"); AutomationProperties.SetAutomationId(detail, "AssistantIndexDetail");
            chooser.SelectionChanged += delegate { if (!rendering && chooser.SelectedItem is Choice) { userSelected = true; selectedId = ((Choice)chooser.SelectedItem).Id; Render(); } };
            staleTimer.Tick += delegate { if (!disposed && observed != DateTime.MinValue && DateTime.UtcNow - observed > TimeSpan.FromSeconds(20)) Render(); };
            Loaded += delegate { if (!disposed) staleTimer.Start(); }; Unloaded += delegate { staleTimer.Stop(); };
            Render();
        }

        internal void Update(Dictionary<string, object> snapshot)
        {
            if (disposed) return;
            observed = DateTime.UtcNow; disconnected = false;
            connected = IndexProgressState.Text(IndexProgressState.Map(IndexProgressState.Get(snapshot, "status")), "msi") == "Connected";
            sources = IndexProgressState.Rows(IndexProgressState.Get(snapshot, "sources"));
            tasks = IndexProgressState.Rows(IndexProgressState.Get(snapshot, "tasks")).Where(t => IndexProgressState.Text(t, "kind") == "index").OrderBy(IndexProgressState.Rank).ThenByDescending(IndexProgressState.Updated).ToList();
            if (!userSelected || !tasks.Any(t => IndexProgressState.Identity(t) == selectedId)) selectedId = tasks.Count == 0 ? "" : IndexProgressState.Identity(tasks[0]);
            var choices = tasks.Select(t => new Choice { Id = IndexProgressState.Identity(t), Label = IndexProgressState.Source(t, sources) + " · " + IndexProgressState.Text(t, "status") + " · " + IndexProgressState.Identity(t).Substring(Math.Max(0, IndexProgressState.Identity(t).Length - 8)) }).ToList();
            string old = String.Join("|", chooser.Items.Cast<Choice>().Select(c => c.Id + ":" + c.Label));
            string next = String.Join("|", choices.Select(c => c.Id + ":" + c.Label));
            rendering = true;
            try { if (old != next) chooser.ItemsSource = choices; chooser.SelectedItem = chooser.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selectedId); chooser.IsEnabled = choices.Count > 1; }
            finally { rendering = false; }
            Render();
        }

        internal void MarkDisconnected(string reason)
        {
            disconnected = true; connected = false; Render();
            if (!String.IsNullOrEmpty(reason)) { detail.Text = "Last known observation · " + reason; detail.ToolTip = detail.Text; }
        }

        private void Render()
        {
            var selected = tasks.FirstOrDefault(t => IndexProgressState.Identity(t) == selectedId);
            Current = IndexProgressState.From(selected, connected && !disconnected, observed != DateTime.MinValue && DateTime.UtcNow - observed > TimeSpan.FromSeconds(20));
            status.Text = Current.State; status.Foreground = Current.Attention ? MatrixTheme.Error : MatrixTheme.Accent;
            count.Text = Current.Count; file.Text = Current.File; file.ToolTip = Current.File; detail.Text = Current.Detail; detail.ToolTip = Current.Detail;
            bar.IsIndeterminate = Current.Indeterminate; bar.Value = Current.Percent; bar.Foreground = Current.Attention ? MatrixTheme.Error : MatrixTheme.Accent;
            AutomationProperties.SetItemStatus(bar, Current.State + " " + Current.Count);
        }

        public void Dispose() { disposed = true; staleTimer.Stop(); }
    }
}
