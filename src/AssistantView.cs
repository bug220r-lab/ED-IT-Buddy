using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EditBuddy
{
    internal sealed class AssistantChoice
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }

    internal sealed class AssistantTaskRow
    {
        internal Dictionary<string, object> Value;
        public string Id { get { return AssistantView.Text(Value, "task_id"); } }
        public string Label { get { string delivery = AssistantView.Text(Value, "delivery_status"); string reviewed = AssistantView.Text(Value, "reviewed_by_user_at"); string kind = AssistantView.Text(Value, "scope") == "library" ? "Library index" : AssistantView.Text(Value, "kind"); return kind + " · " + AssistantView.Text(Value, "status") + (delivery.Length > 0 ? " · delivery " + delivery : "") + (reviewed.Length > 0 ? " · reviewed by you" : "") + "\n" + AssistantView.Text(Value, "message"); } }
    }

    internal sealed class AssistantAssetRow
    {
        internal Dictionary<string, object> Value;
        public string Id { get { return AssistantView.Text(Value, "asset_id"); } }
        public string Label { get; set; }
        public string Detail { get; set; }
        public ImageSource Thumbnail { get; set; }
    }

    // The manager is deliberately independent of MAGIX's legacy offline/focus controls.
    internal sealed class AssistantView : UserControl, IDisposable
    {
        private readonly string root;
        private AssistantProcessClient client;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly ComboBox projects = new ComboBox();
        private readonly ComboBox sources = new ComboBox();
        private readonly TextBlock serviceState = new TextBlock();
        private readonly TextBlock message = new TextBlock();
        private readonly TextBlock selectedTask = new TextBlock();
        private readonly TextBlock attachmentLabel = new TextBlock();
        private readonly TextBox sourcePath = new TextBox();
        private readonly TextBox downloadUrls = new TextBox();
        private readonly TextBox searchQuery = new TextBox();
        private readonly TextBox clipStart = new TextBox();
        private readonly TextBox clipEnd = new TextBox();
        private readonly TextBox chatHistory = new TextBox();
        private readonly TextBox chatInput = new TextBox();
        private readonly TextBox reportView = new TextBox();
        private readonly ListBox taskList = new ListBox();
        private readonly ListBox assetList = new ListBox();
        private readonly ComboBox organization = new ComboBox();
        private readonly TextBox organizationTag = new TextBox();
        private readonly ProgressBar taskProgress = new ProgressBar();
        private readonly IndexProgressPanel indexProgress = new IndexProgressPanel();
        private readonly Button connectButton;
        private readonly Button sendButton;
        private bool rendering;
        private bool refreshing;
        private bool changingProject;
        private bool sendingChat;
        private bool chatStreamed;
        private bool disposed;
        private int resultGeneration;
        private string pendingTaskId = "";
        private string loadedResultsTaskId = "";
        private string loadingResultsTaskId = "";
        private string projectId = "";
        private string attachmentPath = "";

        internal AssistantView(string workspaceRoot)
        {
            root = Path.GetFullPath(workspaceRoot);
            Grid layout = new Grid { Margin = new Thickness(4, 12, 4, 4) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;

            StackPanel heading = new StackPanel(); layout.Children.Add(heading);
            heading.Children.Add(Heading("Editing assistant", 22));
            heading.Children.Add(Description("Give Buddy the busy work. Media jobs run on MSI; your edit stays on THIS-MACHINE."));
            DockPanel connection = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; heading.Children.Add(connection);
            connectButton = Button("Connect / refresh", Connect, true); DockPanel.SetDock(connectButton, Dock.Right); connection.Children.Add(connectButton);
            serviceState.Text = "Not connected · Connect to load your projects, cached tasks, and MSI status.";
            serviceState.TextWrapping = TextWrapping.Wrap; serviceState.Foreground = MatrixTheme.Muted; serviceState.VerticalAlignment = VerticalAlignment.Center; connection.Children.Add(serviceState);
            AutomationProperties.SetAutomationId(connectButton, "AssistantConnect");
            DockPanel projectLine = new DockPanel { Margin = new Thickness(0, 4, 0, 10) }; heading.Children.Add(projectLine);
            TextBlock projectCaption = Description("INTERLINKED project"); projectCaption.Width = 190; projectCaption.VerticalAlignment = VerticalAlignment.Center; projectLine.Children.Add(projectCaption);
            projects.DisplayMemberPath = "Label"; projects.MinWidth = 200; projects.Margin = new Thickness(0, 0, 8, 0); projects.SelectionChanged += ProjectChanged;
            AutomationProperties.SetName(projects, "INTERLINKED project for Buddy tasks"); AutomationProperties.SetAutomationId(projects, "AssistantProject"); projectLine.Children.Add(projects);
            heading.Children.Add(indexProgress);

            Grid columns = new Grid(); Grid.SetRow(columns, 1); layout.Children.Add(columns);
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 360 });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 310 });
            TabControl work = new TabControl { BorderThickness = new Thickness(0) }; columns.Children.Add(work);
            work.Items.Add(WorkTab("Media", BuildMedia())); work.Items.Add(WorkTab("Tasks", BuildTasks())); work.Items.Add(WorkTab("Report", BuildReport()));

            Grid chat = new Grid(); Grid.SetColumn(chat, 2); columns.Children.Add(chat);
            chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chat.Children.Add(Heading("Buddy chat", 17));
            chatHistory.IsReadOnly = true; chatHistory.AcceptsReturn = true; chatHistory.TextWrapping = TextWrapping.Wrap;
            chatHistory.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; chatHistory.MinHeight = 240; chatHistory.Margin = new Thickness(0, 9, 0, 8);
            chatHistory.Text = "Choose a project, then ask Buddy to download links, find a visual moment, organize copies, or explain project work.\n\nFootage search stays on MSI. Only a frame you attach is shared with Codex chat.\n";
            Grid.SetRow(chatHistory, 1); chat.Children.Add(chatHistory); AutomationProperties.SetName(chatHistory, "Buddy conversation");
            chatInput.AcceptsReturn = true; chatInput.TextWrapping = TextWrapping.Wrap; chatInput.Height = 95; chatInput.MaxLength = 16000;
            chatInput.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Run(SendChat); } };
            Grid.SetRow(chatInput, 2); chat.Children.Add(chatInput); AutomationProperties.SetName(chatInput, "Message Buddy; Control Enter sends");
            attachmentLabel.TextWrapping = TextWrapping.Wrap; attachmentLabel.FontSize = 11; attachmentLabel.Foreground = MatrixTheme.Muted;
            attachmentLabel.Text = "No frame attached"; Grid.SetRow(attachmentLabel, 3); chat.Children.Add(attachmentLabel);
            WrapPanel chatActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(chatActions, 4); chat.Children.Add(chatActions);
            sendButton = Button("Send", SendChat, true); chatActions.Children.Add(sendButton);
            chatActions.Children.Add(Button("Sign in to Codex", SignInChat, false));
            chatActions.Children.Add(Button("Attach frame…", AttachFrame, false));
            chatActions.Children.Add(Button("Clear frame", delegate { attachmentPath = ""; attachmentLabel.Text = "No frame attached"; return Task.FromResult(0); }, false));
            message.TextWrapping = TextWrapping.Wrap; message.Foreground = MatrixTheme.Muted; message.Margin = new Thickness(0, 12, 0, 4);
            message.Text = "Connect explicitly to start the assistant. Existing MAGIX review controls remain in the other tabs."; Grid.SetRow(message, 2); layout.Children.Add(message);
            timer.Interval = TimeSpan.FromSeconds(3); timer.Tick += async delegate { if (client != null && client.IsRunning && !refreshing && !disposed) await RefreshTasks(false); };
            RenderSources(new List<Dictionary<string, object>>());
        }

        private UIElement BuildMedia()
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(Heading("Footage on MSI", 17));
            panel.Children.Add(Description("Register only the MSI folders you want indexed. Source files stay in place."));
            sources.DisplayMemberPath = "Label"; sources.Margin = new Thickness(0, 6, 0, 8); AutomationProperties.SetName(sources, "MSI footage source"); panel.Children.Add(sources);
            sourcePath.MaxLength = 1000; sourcePath.ToolTip = "An absolute folder path on MSI, for example C:\\Users\\jlewi\\Documents\\Band Footage";
            AutomationProperties.SetName(sourcePath, "Folder path on MSI"); panel.Children.Add(sourcePath);
            WrapPanel sourceActions = new WrapPanel(); panel.Children.Add(sourceActions);
            sourceActions.Children.Add(Button("Register folder", RegisterSource, false)); sourceActions.Children.Add(Button("Index selected source", IndexSource, true));
            panel.Children.Add(Heading("Find a visual moment", 17));
            searchQuery.MaxLength = 1000; searchQuery.ToolTip = "For example: drummer close-up, crowd shot, wide stage view"; AutomationProperties.SetName(searchQuery, "Visual footage search");
            searchQuery.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Run(Search); } }; panel.Children.Add(searchQuery);
            panel.Children.Add(Button("Search footage", Search, true));
            assetList.SelectionMode = SelectionMode.Extended; assetList.MinHeight = 185; assetList.Height = 230;
            assetList.ItemTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Margin='5'><Grid.ColumnDefinitions><ColumnDefinition Width='112'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Image Source='{Binding Thumbnail}' Width='104' Height='65' Stretch='Uniform'/><StackPanel Grid.Column='1' Margin='7,0,0,0'><TextBlock Text='{Binding Label}' TextWrapping='Wrap'/><TextBlock Text='{Binding Detail}' TextWrapping='Wrap' Opacity='0.7' FontSize='11'/></StackPanel></Grid></DataTemplate>");
            assetList.SelectionChanged += AssetChanged; assetList.MouseDoubleClick += delegate { Run(PreviewAsset); }; AutomationProperties.SetName(assetList, "Footage matches and output assets"); panel.Children.Add(assetList);
            Grid range = new Grid { Margin = new Thickness(0, 6, 0, 0) }; range.ColumnDefinitions.Add(new ColumnDefinition()); range.ColumnDefinitions.Add(new ColumnDefinition()); panel.Children.Add(range);
            StackPanel start = new StackPanel { Margin = new Thickness(0, 0, 5, 0) }; range.Children.Add(start); start.Children.Add(Description("Source start · seconds")); clipStart.Text = "0"; start.Children.Add(clipStart);
            StackPanel end = new StackPanel { Margin = new Thickness(5, 0, 0, 0) }; Grid.SetColumn(end, 1); range.Children.Add(end); end.Children.Add(Description("Source end · seconds")); clipEnd.Text = "10"; end.Children.Add(clipEnd);
            AutomationProperties.SetName(clipStart, "Clip source start in seconds"); AutomationProperties.SetName(clipEnd, "Clip source end in seconds");
            WrapPanel resultActions = new WrapPanel(); panel.Children.Add(resultActions); resultActions.Children.Add(Button("Preview", PreviewAsset, false)); resultActions.Children.Add(Button("Extract clip to project", ExtractAsset, true));
            organization.ItemsSource = new[] { "date", "source", "tag" }; organization.SelectedIndex = 0; organization.Margin = new Thickness(0, 0, 6, 7); organization.MinWidth = 130; AutomationProperties.SetName(organization, "Organize copies by");
            WrapPanel organizeActions = new WrapPanel(); panel.Children.Add(organizeActions); organizeActions.Children.Add(organization); organizeActions.Children.Add(Button("Organize selected copies", OrganizeAssets, false));
            organizationTag.MaxLength = 80; organizationTag.ToolTip = "Tag to use when grouping copies by tag"; organizationTag.Visibility = Visibility.Collapsed; AutomationProperties.SetName(organizationTag, "Tag for organized copies"); panel.Children.Add(organizationTag);
            organization.SelectionChanged += delegate { organizationTag.Visibility = Convert.ToString(organization.SelectedItem, CultureInfo.InvariantCulture) == "tag" ? Visibility.Visible : Visibility.Collapsed; };
            panel.Children.Add(Description("Organization creates new copies and retains existing paths and originals."));
            panel.Children.Add(Heading("Download supplied links", 17));
            downloadUrls.AcceptsReturn = true; downloadUrls.TextWrapping = TextWrapping.Wrap; downloadUrls.Height = 100; downloadUrls.MaxLength = 32000;
            downloadUrls.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; AutomationProperties.SetName(downloadUrls, "One video URL per line"); panel.Children.Add(downloadUrls);
            panel.Children.Add(Button("Queue downloads", Download, true));
            return panel;
        }

        private UIElement BuildTasks()
        {
            StackPanel panel = new StackPanel(); panel.Children.Add(Heading("Project work", 17));
            panel.Children.Add(Description("Tasks stay visible when MSI is offline. Completed media jobs and verified project delivery are separate stages."));
            taskList.DisplayMemberPath = "Label"; taskList.MinHeight = 220; taskList.Height = 300; taskList.Margin = new Thickness(0, 8, 0, 8);
            taskList.SelectionChanged += async delegate { if (!rendering) await LoadTask(); }; AutomationProperties.SetName(taskList, "Buddy task queue"); panel.Children.Add(taskList);
            taskProgress.Minimum = 0; taskProgress.Maximum = 100; taskProgress.Height = 7; taskProgress.Margin = new Thickness(0, 2, 0, 9); panel.Children.Add(taskProgress);
            selectedTask.TextWrapping = TextWrapping.Wrap; selectedTask.Foreground = MatrixTheme.Muted; selectedTask.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(selectedTask);
            WrapPanel actions = new WrapPanel(); panel.Children.Add(actions);
            actions.Children.Add(Button("Refresh tasks", delegate { return RefreshTasks(true); }, false));
            actions.Children.Add(Button("Pause", delegate { return TaskAction("pause"); }, false));
            actions.Children.Add(Button("Resume", delegate { return TaskAction("resume"); }, false));
            actions.Children.Add(Button("Cancel", delegate { return TaskAction("cancel"); }, false));
            actions.Children.Add(Button("Mark reviewed", delegate { return TaskAction("mark_reviewed"); }, false));
            actions.Children.Add(Button("Deliver outputs", IngestTask, true));
            panel.Children.Add(Description("Cancel stops owned media work; it does not undo delivered files or stop MAGIX. Mark reviewed records your review, not a worker verification."));
            return panel;
        }

        private UIElement BuildReport()
        {
            StackPanel panel = new StackPanel(); panel.Children.Add(Heading("Project report", 17));
            panel.Children.Add(Description("Completed work, remaining tasks, blockers, and output references for the selected INTERLINKED project."));
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(actions);
            actions.Children.Add(Button("Refresh report", RefreshReport, true));
            actions.Children.Add(Button("Export Markdown", delegate { return ExportReport("markdown"); }, false));
            actions.Children.Add(Button("Export JSON", delegate { return ExportReport("json"); }, false));
            reportView.IsReadOnly = true; reportView.AcceptsReturn = true; reportView.TextWrapping = TextWrapping.Wrap; reportView.MinHeight = 360; reportView.Height = 480;
            reportView.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; AutomationProperties.SetName(reportView, "Project work report"); panel.Children.Add(reportView);
            return panel;
        }

        private async Task Connect()
        {
            connectButton.IsEnabled = false;
            try
            {
                if (client == null || !client.IsRunning)
                {
                    if (client != null) client.Dispose();
                    client = new AssistantProcessClient(root); client.Notification += OnNotification;
                    client.Disconnected += delegate(string detail) { Dispatch(delegate { timer.Stop(); indexProgress.MarkDisconnected(detail); serviceState.Text = "Task broker disconnected"; Show(detail, true); }); };
                }
                // Bootstrap provides the local queue even if the remote connection is unavailable.
                await Bootstrap(); timer.Start();
                try { Dictionary<string, object> state = Map(await client.RequestAsync("reconnect", new { })); RenderServiceState(state); }
                catch (Exception error) { Show("Local tasks are available. " + error.Message, true); }
                await Bootstrap();
            }
            finally { connectButton.IsEnabled = true; }
        }

        private async Task Bootstrap()
        {
            Dictionary<string, object> state = Map(await RequireClient().RequestAsync("bootstrap", new { }));
            indexProgress.Update(state);
            string selected = Text(state, "selected_project_id"); if (!String.IsNullOrEmpty(selected)) projectId = selected;
            RenderProjects(List(state, "projects")); RenderSources(List(state, "sources")); RenderTasks(List(state, "tasks"));
            if (taskList.SelectedItem == null)
            {
                AssistantTaskRow recentSearch = taskList.Items.Cast<AssistantTaskRow>().FirstOrDefault(delegate(AssistantTaskRow row) { return Text(row.Value, "kind") == "search"; });
                if (recentSearch != null)
                {
                    rendering = true; try { taskList.SelectedItem = recentSearch; } finally { rendering = false; }
                    await LoadTask();
                }
            }
            object status; if (state.TryGetValue("status", out status)) RenderServiceState(Map(status));
            Show("Ready. Give Buddy a request or use the media controls.", false);
        }

        private async void ProjectChanged(object sender, SelectionChangedEventArgs e)
        {
            if (rendering || changingProject || disposed) return;
            AssistantChoice choice = projects.SelectedItem as AssistantChoice;
            if (choice == null || choice.Id == projectId) return;
            changingProject = true;
            try
            {
                await RequireClient().RequestAsync("select_project", new { project_id = choice.Id }); projectId = choice.Id;
                ++resultGeneration;
                pendingTaskId = ""; loadedResultsTaskId = "";
                assetList.ItemsSource = null; reportView.Clear(); chatHistory.Text = "Project: " + choice.Label + "\n\n";
                await RefreshTasks(true);
            }
            catch (Exception error) { Show(error.Message, true); }
            finally { changingProject = false; }
        }

        private async Task RegisterSource()
        {
            string path = sourcePath.Text.Trim(); if (String.IsNullOrEmpty(path)) throw new InvalidOperationException("Enter the folder's absolute path on MSI.");
            await RequireClient().RequestAsync("register_source", new { path = path });
            Dictionary<string, object> response = Map(await client.RequestAsync("sources", new { })); RenderSources(List(response, "sources"));
            Show("MSI source registered. Select it and start indexing when ready.", false);
        }

        private async Task IndexSource()
        {
            AssistantChoice source = sources.SelectedItem as AssistantChoice;
            if (source == null || String.IsNullOrEmpty(source.Id)) throw new InvalidOperationException("Select one registered MSI source to index.");
            await Submit("index", new { source_id = source.Id });
        }

        private async Task Download()
        {
            string[] urls = downloadUrls.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(delegate(string x) { return x.Trim(); }).Where(delegate(string x) { return x.Length > 0; }).Distinct().ToArray();
            if (urls.Length == 0) throw new InvalidOperationException("Paste at least one individual video URL.");
            foreach (string value in urls) { Uri uri; if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) throw new InvalidOperationException("Use one complete http:// or https:// video URL per line."); }
            await Submit("download", new { urls = urls });
        }

        private async Task Submit(string kind, object parameters)
        {
            RequireProject(); object result = await RequireClient().RequestAsync("submit", new { kind = kind, @params = parameters });
            Show("Requested " + kind + ". See Tasks for progress and delivery status.", false); await RefreshTasks(false); SelectReturnedTask(result);
        }

        private async Task Search()
        {
            RequireProject(); if (String.IsNullOrWhiteSpace(searchQuery.Text)) throw new InvalidOperationException("Describe the visual moment you want to find.");
            AssistantChoice source = sources.SelectedItem as AssistantChoice;
            string[] ids = source == null || String.IsNullOrEmpty(source.Id) ? new string[0] : new[] { source.Id };
            object response = await RequireClient().RequestAsync("search", new { query = searchQuery.Text.Trim(), source_ids = ids });
            List<Dictionary<string, object>> found = ResultItems(response); if (found.Count > 0) RenderAssets(found);
            await RefreshTasks(false); SelectReturnedTask(response); Show("Search requested. Results appear when MSI completes the task.", false);
        }

        private async Task RefreshTasks(bool showMessage)
        {
            if (refreshing) return; refreshing = true;
            try
            {
                AssistantTaskRow before = taskList.SelectedItem as AssistantTaskRow;
                string previousStatus = before == null ? "" : Text(before.Value, "status");
                Dictionary<string, object> data = Map(await RequireClient().RequestAsync("tasks", new { }, 20000));
                indexProgress.Update(data);
                RenderTasks(List(data, "tasks")); object status; if (data.TryGetValue("status", out status)) RenderServiceState(Map(status));
                AssistantTaskRow after = taskList.SelectedItem as AssistantTaskRow;
                if (after != null && (Text(after.Value, "status") != previousStatus ||
                    (Text(after.Value, "status") == "completed" && loadedResultsTaskId != after.Id && loadingResultsTaskId != after.Id))) await LoadTask();
                if (showMessage) Show("Task observations refreshed. Offline observations remain marked in the task state.", false);
            }
            catch (Exception error) { if (showMessage) Show(error.Message, true); }
            finally { refreshing = false; }
        }

        private async Task LoadTask()
        {
            AssistantTaskRow task = taskList.SelectedItem as AssistantTaskRow; if (task == null) return;
            if (loadingResultsTaskId == task.Id) return;
            loadingResultsTaskId = task.Id;
            int generation = ++resultGeneration;
            selectedTask.Text = task.Label; double progress = Number(task.Value, "progress", 0); taskProgress.Value = progress <= 1 ? progress * 100 : progress;
            try
            {
                object result = await RequireClient().RequestAsync("results", new { task_id = task.Id }, 120000);
                if (generation != resultGeneration || disposed) return;
                List<Dictionary<string, object>> assets = ResultItems(result); if (assets.Count > 0) RenderAssets(assets);
                if (Text(task.Value, "status") == "completed")
                {
                    loadedResultsTaskId = task.Id;
                    if (Text(task.Value, "kind") == "search") Show(assets.Count == 0 ? "Search completed with no candidate matches." : "Search completed: " + assets.Count + " candidate matches. Select a match to preview it.", false);
                }
                Dictionary<string, object> resultMap = Map(result); string path = Text(resultMap, "preview_path");
                if (!String.IsNullOrEmpty(path)) Show("Preview ready. Select its asset in Media and choose Preview.", false);
            }
            catch (Exception error) { if (generation == resultGeneration) Show(error.Message, true); }
            finally { if (loadingResultsTaskId == task.Id) loadingResultsTaskId = ""; }
        }

        private async Task TaskAction(string action)
        {
            AssistantTaskRow task = taskList.SelectedItem as AssistantTaskRow; if (task == null) throw new InvalidOperationException("Select a task first.");
            await RequireClient().RequestAsync("task_action", new { task_id = task.Id, action = action }); await RefreshTasks(false);
            Show(action == "mark_reviewed" ? "Your review was recorded separately from worker verification." : "Requested " + action + ". Refreshing the task receipt will show its outcome.", false);
        }

        private async Task IngestTask()
        {
            RequireProject(); AssistantTaskRow task = taskList.SelectedItem as AssistantTaskRow; if (task == null) throw new InvalidOperationException("Select a task with completed outputs first.");
            await RequireClient().RequestAsync("ingest", new { task_id = task.Id }); await RefreshTasks(false); Show("Delivery requested through INTERLINKED. Save-back remains a separate operation.", false);
        }

        private void AssetChanged(object sender, SelectionChangedEventArgs e)
        {
            AssistantAssetRow row = assetList.SelectedItem as AssistantAssetRow; if (row == null) return;
            double start = Number(row.Value, "start_seconds", 0); double end = Number(row.Value, "end_seconds", start + 10);
            clipStart.Text = start.ToString("0.###", CultureInfo.InvariantCulture); clipEnd.Text = end.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private async Task PreviewAsset()
        {
            AssistantAssetRow row = SelectedAsset(); double start, end; ReadRange(out start, out end);
            string cached = Text(row.Value, "preview_path");
            if (!String.IsNullOrEmpty(cached) && File.Exists(cached)) { PlayPreview(cached); return; }
            object response = await RequireClient().RequestAsync("preview", new { asset_id = row.Id, start_seconds = start, end_seconds = end }, 150000);
            Dictionary<string, object> result = Map(response); string path = Text(result, "preview_path"); if (String.IsNullOrEmpty(path)) path = Text(result, "path");
            if (!String.IsNullOrEmpty(path) && File.Exists(path)) PlayPreview(path);
            else { await RefreshTasks(false); SelectReturnedTask(response); Show("MSI is preparing a preview. Select the completed preview task to retrieve it.", false); }
        }

        private async Task ExtractAsset()
        {
            RequireProject(); AssistantAssetRow row = SelectedAsset(); double start, end; ReadRange(out start, out end);
            object response = await RequireClient().RequestAsync("extract", new { asset_id = row.Id, start_seconds = start, end_seconds = end });
            await RefreshTasks(false); SelectReturnedTask(response); Show("Clip extraction requested. The source stays in place.", false);
        }

        private async Task OrganizeAssets()
        {
            RequireProject(); string[] ids = assetList.SelectedItems.Cast<AssistantAssetRow>().Select(delegate(AssistantAssetRow row) { return row.Id; }).Where(delegate(string id) { return !String.IsNullOrEmpty(id); }).ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("Select one or more source assets in the results list.");
            string group = Convert.ToString(organization.SelectedItem, CultureInfo.InvariantCulture);
            Dictionary<string, object> parameters = new Dictionary<string, object> { { "asset_ids", ids }, { "group_by", group } };
            if (group == "tag") { if (String.IsNullOrWhiteSpace(organizationTag.Text)) throw new InvalidOperationException("Enter a tag for the organized copies."); parameters["tag"] = organizationTag.Text.Trim(); }
            object response = await RequireClient().RequestAsync("organize", parameters);
            await RefreshTasks(false); SelectReturnedTask(response); Show("Copy-first organization requested. Existing files will retain their paths.", false);
        }

        private Task AttachFrame()
        {
            OpenFileDialog picker = new OpenFileDialog { Title = "Attach a frame to the next Codex message", Filter = "Frame images|*.png;*.jpg;*.jpeg", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(Window.GetWindow(this)) == true)
            {
                FileInfo file = new FileInfo(picker.FileName); if (file.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Choose a frame no larger than 8 MiB.");
                attachmentPath = file.FullName; attachmentLabel.Text = "Will share with Codex: " + file.Name;
            }
            return Task.FromResult(0);
        }

        private async Task SendChat()
        {
            if (sendingChat) return; RequireProject(); string text = chatInput.Text.Trim();
            if (text.Length == 0) throw new InvalidOperationException("Write a message to send with the optional frame.");
            sendingChat = true; sendButton.IsEnabled = false; projects.IsEnabled = false; chatStreamed = false;
            string frame = attachmentPath; AppendChat("\nYou: " + text + (frame.Length == 0 ? "" : "\n[Attached frame: " + Path.GetFileName(frame) + "]") + "\n");
            chatInput.Clear(); attachmentPath = ""; attachmentLabel.Text = "No frame attached";
            try
            {
                object result = await RequireClient().RequestAsync("send_chat", new { text = text, attachment_path = frame.Length == 0 ? null : frame }, 360000);
                Dictionary<string, object> reply = Map(result); string response = Text(reply, "text"); if (response.Length == 0) response = Text(reply, "message");
                if (!chatStreamed && response.Length > 0) AppendChat("Buddy: " + response);
                AppendChat("\n"); await RefreshTasks(false);
            }
            catch (Exception error) { AppendChat("\nBuddy could not complete this turn: " + error.Message + "\n"); throw; }
            finally { sendingChat = false; sendButton.IsEnabled = true; projects.IsEnabled = true; }
        }

        private async Task SignInChat()
        {
            Dictionary<string, object> response = Map(await RequireClient().RequestAsync("login_chat", new { }, 30000));
            string address = Text(response, "auth_url"); if (address.Length == 0) address = Text(response, "authUrl"); if (address.Length == 0) address = Text(response, "verification_url");
            if (address.Length > 0)
            {
                Uri uri;
                if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !(uri.DnsSafeHost == "auth.openai.com" || uri.DnsSafeHost == "auth0.openai.com" || uri.DnsSafeHost == "chatgpt.com"))
                    throw new InvalidOperationException("Codex returned an unexpected sign-in address. No browser was opened.");
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
                string code = Text(response, "user_code"); Show("Finish signing in through the browser" + (code.Length == 0 ? "." : " using code " + code + ".") + " Then use Connect / refresh.", false);
            }
            else { string detail = Text(response, "message"); Show(detail.Length == 0 ? "Codex sign-in status updated. Use Connect / refresh to check it." : detail, false); }
        }

        private async Task RefreshReport()
        {
            RequireProject(); object response = await RequireClient().RequestAsync("report", new { format = "markdown" }); reportView.Text = ReportText(response);
            ShowReportOutcome(response, "Project report refreshed from task observations.");
        }

        private async Task ExportReport(string format)
        {
            RequireProject(); object response = await RequireClient().RequestAsync("report", new { format = format }); string content = ReportText(response);
            SaveFileDialog picker = new SaveFileDialog { Title = "Export Buddy project report", Filter = format == "json" ? "JSON report|*.json" : "Markdown report|*.md", FileName = "buddy-project-report." + (format == "json" ? "json" : "md"), AddExtension = true };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            await Task.Run(delegate { File.WriteAllText(picker.FileName, content, new UTF8Encoding(false)); }); ShowReportOutcome(response, "Report exported to " + picker.FileName + ".");
        }

        private void ShowReportOutcome(object response, string localOutcome)
        {
            object value; Dictionary<string, object> map = Map(response);
            Dictionary<string, object> publication = map.TryGetValue("publication", out value) ? Map(value) : new Dictionary<string, object>();
            string status = Text(publication, "status");
            if (status == "failed") Show(localOutcome + " INTERLINKED publication failed: " + Text(publication, "message"), true);
            else if (status == "published") Show(localOutcome + " Published to INTERLINKED.", false);
            else Show(localOutcome + " INTERLINKED publication is unconfirmed.", false);
        }

        private static string ReportText(object value)
        {
            if (value is string) return (string)value;
            Dictionary<string, object> map = Map(value); foreach (string key in new[] { "content", "markdown", "text" }) { string text = Text(map, key); if (text.Length > 0) return text; }
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Serialize(value);
        }

        private void OnNotification(Dictionary<string, object> value)
        {
            Dispatch(delegate {
                if (Text(value, "event") == "chat_delta")
                {
                    if (!sendingChat) return;
                    string delta = Text(value, "delta"); if (delta.Length == 0) delta = Text(value, "text");
                    if (!chatStreamed) { AppendChat("Buddy: "); chatStreamed = true; } AppendChat(delta);
                }
                else if (Text(value, "event") == "state_changed" && !refreshing) Run(delegate { return RefreshTasks(false); });
            });
        }

        private void RenderProjects(List<Dictionary<string, object>> data)
        {
            rendering = true;
            try
            {
                List<AssistantChoice> rows = data.Select(delegate(Dictionary<string, object> item) { string id = Text(item, "project_id"); if (id.Length == 0) id = Text(item, "originalId"); string name = Text(item, "name"); return new AssistantChoice { Id = id, Label = name.Length == 0 ? id : name }; }).Where(delegate(AssistantChoice item) { return item.Id.Length > 0; }).ToList();
                projects.ItemsSource = rows; projects.SelectedItem = rows.FirstOrDefault(delegate(AssistantChoice item) { return item.Id == projectId; });
            }
            finally { rendering = false; }
        }

        private void RenderSources(List<Dictionary<string, object>> data)
        {
            string selected = sources.SelectedItem is AssistantChoice ? ((AssistantChoice)sources.SelectedItem).Id : "";
            List<AssistantChoice> rows = new List<AssistantChoice> { new AssistantChoice { Id = "", Label = "All indexed sources (search)" } };
            foreach (Dictionary<string, object> item in data) rows.Add(new AssistantChoice { Id = Text(item, "source_id"), Label = Text(item, "path") });
            sources.ItemsSource = rows; sources.SelectedItem = rows.FirstOrDefault(delegate(AssistantChoice item) { return item.Id == selected; }) ?? rows[0];
        }

        private void RenderTasks(List<Dictionary<string, object>> data)
        {
            string selected = taskList.SelectedItem is AssistantTaskRow ? ((AssistantTaskRow)taskList.SelectedItem).Id : "";
            if (pendingTaskId.Length > 0) selected = pendingTaskId;
            rendering = true;
            try
            {
                List<AssistantTaskRow> rows = data.AsEnumerable().Reverse().Select(delegate(Dictionary<string, object> value) { return new AssistantTaskRow { Value = value }; }).ToList();
                taskList.ItemsSource = rows; AssistantTaskRow row = rows.FirstOrDefault(delegate(AssistantTaskRow item) { return item.Id == selected; }); taskList.SelectedItem = row;
                if (row != null && row.Id == pendingTaskId) pendingTaskId = "";
                if (row != null) { selectedTask.Text = row.Label; double progress = Number(row.Value, "progress", 0); taskProgress.Value = progress <= 1 ? progress * 100 : progress; }
            }
            finally { rendering = false; }
        }

        private void RenderAssets(List<Dictionary<string, object>> assets)
        {
            List<AssistantAssetRow> rows = new List<AssistantAssetRow>();
            foreach (Dictionary<string, object> value in assets)
            {
                string path = Text(value, "source_path"); if (path.Length == 0) path = Text(value, "path");
                double start = Number(value, "start_seconds", 0); double end = Number(value, "end_seconds", start);
                string name = Text(value, "title"); if (name.Length == 0) name = Path.GetFileName(path); if (name.Length == 0) name = Text(value, "asset_id");
                rows.Add(new AssistantAssetRow { Value = value, Label = name + " · " + Time(start) + "–" + Time(end), Detail = path + (value.ContainsKey("score") ? "\nSimilarity " + Number(value, "score", 0).ToString("0.000", CultureInfo.InvariantCulture) + " · candidate for review" : ""), Thumbnail = LoadThumbnail(Text(value, "thumbnail_path")) });
            }
            assetList.ItemsSource = rows; if (rows.Count > 0) assetList.SelectedIndex = 0;
        }

        private void RenderServiceState(Dictionary<string, object> state)
        {
            object nested; if (state.TryGetValue("status", out nested) && nested is Dictionary<string, object>) state = Map(nested);
            if (state.Count == 0) return;
            serviceState.Text = "MSI: " + StatusText(state, "msi") + "   INTERLINKED: " + StatusText(state, "interlinked") + "   Chat: " + StatusText(state, "chat");
        }

        private static string StatusText(Dictionary<string, object> value, string key)
        {
            object status; if (!value.TryGetValue(key, out status) || status == null) return "unknown";
            Dictionary<string, object> map = status as Dictionary<string, object>;
            if (map == null) return Convert.ToString(status, CultureInfo.InvariantCulture);
            string text = Text(map, "status"); if (text.Length == 0) text = Text(map, "message");
            if (text.Length == 0) { object connected; if (map.TryGetValue("connected", out connected) && connected is bool) return (bool)connected ? "connected" : "offline"; }
            return text.Length == 0 ? "unknown" : text;
        }

        private void SelectReturnedTask(object value)
        {
            Dictionary<string, object> map = Map(value); string id = Text(map, "task_id"); object nested; if (id.Length == 0 && map.TryGetValue("task", out nested)) id = Text(Map(nested), "task_id");
            if (id.Length == 0) return;
            pendingTaskId = id;
            foreach (object item in taskList.Items) { AssistantTaskRow row = item as AssistantTaskRow; if (row != null && row.Id == id) { pendingTaskId = ""; taskList.SelectedItem = row; taskList.ScrollIntoView(row); break; } }
        }

        private AssistantProcessClient RequireClient()
        { if (client == null || !client.IsRunning) throw new InvalidOperationException("Use Connect / refresh to start Buddy's local task broker."); return client; }
        private void RequireProject() { if (changingProject) throw new InvalidOperationException("Wait for the project selection to finish."); if (String.IsNullOrEmpty(projectId)) throw new InvalidOperationException("Select an INTERLINKED project first."); }
        private AssistantAssetRow SelectedAsset() { AssistantAssetRow row = assetList.SelectedItem as AssistantAssetRow; if (row == null || String.IsNullOrEmpty(row.Id)) throw new InvalidOperationException("Select a source asset or match first."); return row; }
        private void ReadRange(out double start, out double end)
        {
            if (!Double.TryParse(clipStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out start) || !Double.TryParse(clipEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out end) || Double.IsNaN(start) || Double.IsInfinity(start) || Double.IsNaN(end) || Double.IsInfinity(end) || start < 0 || end <= start)
                throw new InvalidOperationException("Enter a nonnegative start and a later end in source seconds.");
        }

        private void PlayPreview(string path)
        {
            if (!Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path)) throw new InvalidOperationException("The local preview is not available yet.");
            AssistantPreviewWindow player = new AssistantPreviewWindow(path) { Owner = Window.GetWindow(this) }; player.Show();
        }

        private static ImageSource LoadThumbnail(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 5 * 1024 * 1024) return null;
                BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image;
            }
            catch { return null; }
        }

        private void AppendChat(string text) { chatHistory.AppendText(text); chatHistory.ScrollToEnd(); }
        private void Show(string text, bool error) { message.Text = text; message.Foreground = error ? MatrixTheme.Error : MatrixTheme.Muted; }
        private void Dispatch(Action action) { if (disposed || Dispatcher.HasShutdownStarted) return; Dispatcher.BeginInvoke((Action)delegate { if (!disposed) action(); }); }
        private async void Run(Func<Task> action) { try { await action(); } catch (Exception error) { if (!disposed) Show(error.Message, true); } }
        private Button Button(string label, Func<Task> action, bool primary)
        { Button result = new Button { Content = label }; if (primary) MatrixTheme.Primary(result); result.Click += delegate { Run(action); }; AutomationProperties.SetName(result, label); return result; }
        private static TextBlock Heading(string text, double size) { return new TextBlock { Text = text, FontSize = size, Foreground = MatrixTheme.Ink, Margin = new Thickness(0, 9, 0, 6), TextWrapping = TextWrapping.Wrap }; }
        private static TextBlock Description(string text) { return new TextBlock { Text = text, Foreground = MatrixTheme.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap }; }
        private static TabItem WorkTab(string title, UIElement content) { return new TabItem { Header = title, Padding = new Thickness(15, 10, 15, 10), Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 4, 9, 8) } }; }
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static string Text(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        internal static double Number(Dictionary<string, object> map, string key, double fallback) { double result; return Double.TryParse(Text(map, key), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !Double.IsNaN(result) && !Double.IsInfinity(result) ? result : fallback; }
        private static string Time(double value) { return TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture); }
        private static List<Dictionary<string, object>> List(Dictionary<string, object> map, string key) { object value; return map.TryGetValue(key, out value) ? ObjectList(value) : new List<Dictionary<string, object>>(); }
        private static List<Dictionary<string, object>> ObjectList(object value)
        { IEnumerable list = value as IEnumerable; List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>(); if (list != null && !(value is string) && !(value is IDictionary)) foreach (object item in list) { Dictionary<string, object> row = item as Dictionary<string, object>; if (row != null) rows.Add(row); } return rows; }
        private static List<Dictionary<string, object>> ResultItems(object value)
        { List<Dictionary<string, object>> rows = ObjectList(value); if (rows.Count > 0) return rows; Dictionary<string, object> map = Map(value); foreach (string key in new[] { "results", "assets", "items", "outputs" }) { rows = List(map, key); if (rows.Count > 0) return rows; } object result; if (map.TryGetValue("result", out result)) return ResultItems(result); return rows; }

        public void Dispose()
        { if (disposed) return; disposed = true; timer.Stop(); indexProgress.Dispose(); ++resultGeneration; if (client != null) client.Dispose(); }
    }

    internal sealed class AssistantPreviewWindow : Window
    {
        private readonly MediaElement media;
        internal AssistantPreviewWindow(string path)
        {
            Title = "Buddy preview · " + Path.GetFileName(path); Width = 860; Height = 560; MinWidth = 480; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner; MatrixTheme.Apply(this);
            DockPanel layout = new DockPanel(); Content = layout;
            TextBlock state = new TextBlock { Text = "Local preview proxy · " + Path.GetFileName(path), Foreground = MatrixTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) }; DockPanel.SetDock(state, Dock.Top); layout.Children.Add(state);
            WrapPanel actions = new WrapPanel { Margin = new Thickness(12) }; DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
            media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform }; layout.Children.Add(media);
            Button play = new Button { Content = "Play" }; play.Click += delegate { media.Play(); }; actions.Children.Add(play);
            Button pause = new Button { Content = "Pause" }; pause.Click += delegate { media.Pause(); }; actions.Children.Add(pause);
            Button restart = new Button { Content = "Restart" }; restart.Click += delegate { media.Position = TimeSpan.Zero; media.Play(); }; actions.Children.Add(restart);
            media.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e) { state.Text = "Preview playback failed: " + (e.ErrorException == null ? "unsupported media" : e.ErrorException.Message); state.Foreground = MatrixTheme.Error; };
            Loaded += delegate { media.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute); media.Play(); };
            Closed += delegate { media.Stop(); media.Close(); };
        }
    }
}
