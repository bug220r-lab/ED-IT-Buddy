using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace EditBuddy
{
    public sealed class BuddyApplication : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Mutex instance = null;
            bool ownsInstance = false;
            try
            {
                string user = WindowsIdentity.GetCurrent().User.Value;
                instance = new Mutex(true, InstanceMutexName(CompanionWindow.FindRoot(), user), out ownsInstance);
                if (!ownsInstance)
                {
                    MessageBox.Show("ED-IT Buddy is already running for this project. Use the existing companion window.", "ED-IT Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                RunApplication(args != null && args.Any(delegate(string value) { return String.Equals(value, "--offline", StringComparison.OrdinalIgnoreCase); }));
            }
            catch (Exception error) { MessageBox.Show(error.Message, "ED-IT Buddy could not start", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                if (instance != null)
                {
                    if (ownsInstance) instance.ReleaseMutex();
                    instance.Dispose();
                }
            }
        }

        internal static string InstanceMutexName(string root, string user)
        {
            string key = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant() + "|" + user;
            using (SHA256 hash = SHA256.Create())
                return "Local\\ED_IT_Buddy_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
        }

        private static void RunApplication(bool forceOffline)
        {
            BuddyApplication application = new BuddyApplication();
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                try
                {
                    string root = CompanionWindow.FindRoot();
                    Directory.CreateDirectory(Path.Combine(root, "user-data"));
                    File.AppendAllText(Path.Combine(root, "user-data", "buddy.log"), DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine);
                }
                catch { }
                MessageBox.Show("ED-IT Buddy could not complete that operation.\n\n" + e.Exception.Message, "ED-IT Buddy", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            try { application.Run(new CompanionWindow(forceOffline)); }
            catch (Exception error) { MessageBox.Show(error.Message, "ED-IT Buddy could not start", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    public sealed class CompanionWindow : Window
    {
        private MagixController controller;
        private readonly ReviewStore store;
        private ReviewDocument document;
        private ReviewNote selected;
        private int stepIndex;
        private bool loading;
        private bool busy;
        private bool editorDirty;
        private bool workOffline;
        private bool updatingOfflineToggle;
        private ConnectionReport connection;
        private readonly TextBlock connectionLabel = new TextBlock();
        private readonly TextBlock connectionDetail = new TextBlock();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock projectLabel = new TextBlock();
        private readonly Button launchButton;
        private readonly Button refreshButton;
        private readonly CheckBox offlineToggle = new CheckBox();
        private readonly TabControl tabs = new TabControl();
        private readonly AssistantView assistant;
        private readonly ComboBox notePicker = new ComboBox();
        private readonly ListBox noteList = new ListBox();
        private readonly StackPanel guideDetail = new StackPanel();
        private readonly StackPanel actionList = new StackPanel();
        private readonly TextBox controlReport = new TextBox();
        private readonly TextBlock diagnosticSummary = new TextBlock();
        private readonly TextBlock exportPath = new TextBlock();
        private readonly TextBox projectName = new TextBox();
        private readonly TextBox revisionName = new TextBox();
        private readonly TextBox noteTitle = new TextBox();
        private readonly TextBox noteStart = new TextBox();
        private readonly TextBox noteEnd = new TextBox();
        private readonly TextBox noteObservation = new TextBox();
        private readonly TextBox noteSuggestion = new TextBox();
        private readonly TextBlock noteState = new TextBlock();
        private readonly Brush ink = MatrixTheme.Ink;
        private readonly Brush muted = MatrixTheme.Muted;
        private readonly Brush accent = MatrixTheme.Accent;
        private readonly Brush line = MatrixTheme.Line;

        private MagixController Controller
        {
            get { if (controller == null) controller = new MagixController(); return controller; }
        }

        public CompanionWindow() : this(false) { }

        public CompanionWindow(bool forceOffline)
        {
            store = new ReviewStore(FindRoot());
            workOffline = forceOffline || ReadOfflinePreference(store.DataPath);
            string loadWarning = null;
            try { document = store.Load(); }
            catch (Exception error)
            {
                document = ReviewStore.EmptyDocument();
                loadWarning = "Saved notes could not be loaded: " + error.Message;
                if (File.Exists(store.StatePath))
                {
                    string recovery = Path.Combine(store.DataPath, "notes-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                    File.Copy(store.StatePath, recovery);
                    store.AcknowledgeRecoveredState(recovery);
                    loadWarning += " The original was retained in " + Path.GetFileName(recovery) + ".";
                }
                store.Log(error.ToString());
            }
            selected = document.Notes.FirstOrDefault();
            Title = "ED-IT Buddy";
            Width = 1180; Height = 940; MinWidth = 960; MinHeight = 720;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            MatrixTheme.Apply(this);
            DockPanel outer = new DockPanel(); Content = outer;
            StackPanel header = new StackPanel { Margin = new Thickness(20, 17, 20, 14) };
            Border headerFrame = MatrixTheme.Header(header);
            DockPanel.SetDock(headerFrame, Dock.Top); outer.Children.Add(headerFrame);
            header.Children.Add(MatrixTheme.Eyebrow("ED-IT // EDITING ASSISTANT"));
            DockPanel branding = new DockPanel(); header.Children.Add(branding);
            CheckBox pinned = new CheckBox { Content = "Keep on top", VerticalAlignment = VerticalAlignment.Center, Foreground = muted };
            DockPanel.SetDock(pinned, Dock.Right); branding.Children.Add(pinned);
            pinned.Checked += delegate { Topmost = true; };
            pinned.Unchecked += delegate { Topmost = false; };
            branding.Children.Add(new TextBlock { Text = "> ED-IT Buddy", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = accent });
            header.Children.Add(Label("Media, tasks, and project handoffs with INTERLINKED", 12, muted, 0, 3, 0, 14));
            StackPanel legacyConnection = new StackPanel(); header.Children.Add(legacyConnection);
            offlineToggle.Content = "Keep MAGIX review offline"; offlineToggle.IsChecked = workOffline;
            offlineToggle.Foreground = muted; offlineToggle.Margin = new Thickness(0, 0, 0, 10);
            offlineToggle.ToolTip = "Controls only the legacy MAGIX review connection. The Manager connects to MSI and INTERLINKED separately using Connect / refresh.";
            AutomationProperties.SetAutomationId(offlineToggle, "WorkOffline");
            offlineToggle.Checked += delegate { if (!updatingOfflineToggle) SetOfflineMode(true); };
            offlineToggle.Unchecked += delegate { if (!updatingOfflineToggle) SetOfflineMode(false); };
            legacyConnection.Children.Add(offlineToggle);
            connectionLabel.FontWeight = FontWeights.SemiBold; connectionLabel.TextWrapping = TextWrapping.Wrap;
            connectionLabel.Text = "Checking MAGIX…"; legacyConnection.Children.Add(connectionLabel);
            connectionDetail.Foreground = muted; connectionDetail.FontSize = 12; connectionDetail.Margin = new Thickness(0, 3, 0, 0);
            connectionDetail.TextWrapping = TextWrapping.Wrap; connectionDetail.MaxHeight = 72; legacyConnection.Children.Add(connectionDetail);
            WrapPanel headerButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; legacyConnection.Children.Add(headerButtons);
            launchButton = MakeButton("Launch MAGIX", async delegate { await LaunchMagix(); }, false);
            refreshButton = MakeButton("Connect to MAGIX", async delegate { if (workOffline) SetOfflineMode(false); await RefreshConnection(); }, false);
            AutomationProperties.SetAutomationId(launchButton, "LaunchMagix");
            AutomationProperties.SetAutomationId(refreshButton, "RefreshConnection");
            headerButtons.Children.Add(launchButton); headerButtons.Children.Add(refreshButton);
            Border footer = new Border { Background = MatrixTheme.Panel, BorderBrush = line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12) };
            status.TextWrapping = TextWrapping.Wrap; status.FontSize = 12; status.Foreground = muted;
            status.Text = "Notes are stored locally. MAGIX project editing is manual in this first version.";
            footer.Child = status; DockPanel.SetDock(footer, Dock.Bottom); outer.Children.Add(footer);
            tabs.Background = Brushes.Transparent; tabs.BorderThickness = new Thickness(0); tabs.Margin = new Thickness(12, 0, 12, 0);
            outer.Children.Add(tabs);
            assistant = new AssistantView(FindRoot());
            tabs.Items.Add(new TabItem { Header = "Manager", Padding = new Thickness(21, 12, 21, 12), Content = assistant });
            tabs.Items.Add(Tab("Guide", BuildGuide()));
            tabs.Items.Add(Tab("Review", BuildReview()));
            tabs.Items.Add(Tab("Controls", BuildControls()));
            legacyConnection.Visibility = Visibility.Collapsed;
            footer.Visibility = Visibility.Collapsed;
            tabs.SelectionChanged += delegate(object sender, SelectionChangedEventArgs e) { if (e.Source == tabs) { legacyConnection.Visibility = tabs.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible; footer.Visibility = legacyConnection.Visibility; } };
            RefreshNotes(); RenderConnection();
            Loaded += async delegate
            {
                await assistant.StartMonitoring();
                if (!workOffline && tabs.SelectedIndex != 0) await RefreshConnection();
                else SetStatus("Offline mode: review notes and local video are available. Connect explicitly when you want MAGIX access.", false);
                if (loadWarning != null) SetStatus(loadWarning, true);
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (busy) { e.Cancel = true; SetStatus("Wait for the current operation to finish before closing.", false); return; }
                if (editorDirty && !CommitNote()) e.Cancel = true;
            };
            System.Windows.Forms.NotifyIcon tray = new System.Windows.Forms.NotifyIcon();
            tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            tray.Text = "ED-IT Buddy · monitoring while running";
            tray.DoubleClick += delegate { Show(); WindowState = WindowState.Normal; Activate(); };
            StateChanged += delegate { tray.Visible = WindowState == WindowState.Minimized; };
            Closed += delegate { tray.Dispose(); assistant.Dispose(); };
        }

        public static string FindRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 5 && directory != null; i++, directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "data")) || File.Exists(Path.Combine(directory.FullName, "README.md"))) return directory.FullName;
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        internal static bool OfflinePreferenceFromJson(string value)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(value) || value.Length > 4096) return true;
                Dictionary<string, object> settings = new JavaScriptSerializer().DeserializeObject(value) as Dictionary<string, object>;
                object enabled;
                return settings == null || !settings.TryGetValue("workOffline", out enabled) || !(enabled is bool) || (bool)enabled;
            }
            catch { return true; }
        }

        private static bool ReadOfflinePreference(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "preferences.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) return true;
                return OfflinePreferenceFromJson(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return true; }
        }

        private void SetOfflineMode(bool enabled)
        {
            if (busy) return;
            workOffline = enabled;
            document.MappingConfirmed = false;
            connection = null;
            updatingOfflineToggle = true; offlineToggle.IsChecked = enabled; updatingOfflineToggle = false;
            string preferenceError = null;
            try
            {
                string path = Path.Combine(store.DataPath, "preferences.json");
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, enabled ? "{\"workOffline\":true}" : "{\"workOffline\":false}", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch (Exception error) { preferenceError = " The preference could not be saved: " + error.Message; store.Log(error.ToString()); }
            RenderConnection(); SetBusy(false);
            SetStatus((enabled ? "Offline mode enabled. Review notes and local video remain available." : "Ready to connect. Use Connect to MAGIX when you want to inspect its window.") + preferenceError, preferenceError != null);
        }

        internal static bool CanRequestMagix(bool offline, bool operationInProgress)
        { return !offline && !operationInProgress; }

        internal static bool CanUseConnectedAction(bool offline, bool operationInProgress, ConnectionReport report, string actionId)
        {
            return CanRequestMagix(offline, operationInProgress) && report != null && report.Connected && (!report.Blocked || actionId == "inspect_controls" || actionId == "focus_magix");
        }

        private static TextBlock Label(string text, double size, Brush color, double left, double top, double right, double bottom)
        { return new TextBlock { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, top, right, bottom) }; }

        private TabItem Tab(string title, UIElement content)
        {
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content, Padding = new Thickness(9, 18, 9, 16) };
            return new TabItem { Header = title, Padding = new Thickness(21, 12, 21, 12), Content = scroll };
        }

        private Button MakeButton(string text, Action handler, bool primary)
        {
            Button button = new Button { Content = text };
            if (primary) MatrixTheme.Primary(button);
            button.Click += delegate { try { handler(); } catch (Exception error) { ReportError(error); } };
            return button;
        }

        private Border Card(UIElement child)
        { return new Border { Child = child, Background = MatrixTheme.Raised, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(15), Margin = new Thickness(0, 12, 0, 12) }; }

        private StackPanel BuildGuide()
        {
            StackPanel panel = new StackPanel();
            projectLabel.FontSize = 12; projectLabel.Foreground = muted; projectLabel.TextWrapping = TextWrapping.Wrap; panel.Children.Add(projectLabel);
            panel.Children.Add(Label("Choose the next revision", 20, ink, 0, 5, 0, 12));
            notePicker.DisplayMemberPath = "DisplayLabel"; notePicker.Padding = new Thickness(6); notePicker.MaxDropDownHeight = 350;
            AutomationProperties.SetName(notePicker, "Guide review note"); AutomationProperties.SetAutomationId(notePicker, "GuideNotePicker");
            notePicker.SelectionChanged += delegate { if (!loading && notePicker.SelectedItem is ReviewNote) SelectNote((ReviewNote)notePicker.SelectedItem); };
            panel.Children.Add(notePicker); panel.Children.Add(guideDetail);
            return panel;
        }

        private StackPanel BuildReview()
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(Label("Project and review notes", 20, ink, 0, 0, 0, 8));
            panel.Children.Add(Label("Project", 12, muted, 0, 0, 0, 0)); panel.Children.Add(projectName);
            panel.Children.Add(Label("Export revision", 12, muted, 0, 0, 0, 0)); panel.Children.Add(revisionName);
            WrapPanel projectActions = new WrapPanel(); panel.Children.Add(projectActions);
            projectActions.Children.Add(MakeButton("Save project labels", delegate {
                if (String.IsNullOrWhiteSpace(projectName.Text) || String.IsNullOrWhiteSpace(revisionName.Text)) { SetStatus("Enter a project name and revision label.", true); return; }
                document.ProjectName = projectName.Text.Trim(); document.RevisionLabel = revisionName.Text.Trim(); document.MappingConfirmed = false;
                Save(); RenderGuide(); SetStatus("Project labels saved. Confirm the mapping again before assisted steps.", false);
            }, false));
            projectName.MaxLength = 200; revisionName.MaxLength = 200;
            AutomationProperties.SetName(projectName, "Project name"); AutomationProperties.SetName(revisionName, "Export revision");
            exportPath.TextWrapping = TextWrapping.Wrap; exportPath.Foreground = muted; exportPath.FontSize = 12; exportPath.Margin = new Thickness(0, 5, 0, 10); panel.Children.Add(exportPath);
            WrapPanel fileActions = new WrapPanel(); panel.Children.Add(fileActions);
            fileActions.Children.Add(MakeButton("Choose video export", ChooseExport, false));
            fileActions.Children.Add(MakeButton("Preview video", PreviewVideo, false));
            WrapPanel notesActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) }; panel.Children.Add(notesActions);
            notesActions.Children.Add(MakeButton("Import notes", ImportNotes, false));
            notesActions.Children.Add(MakeButton("Export notes", ExportNotes, false));
            notesActions.Children.Add(MakeButton("Add note", AddNote, false));
            noteList.DisplayMemberPath = "DisplayLabel"; noteList.MaxHeight = 150; noteList.MinHeight = 60;
            AutomationProperties.SetName(noteList, "Review notes"); AutomationProperties.SetAutomationId(noteList, "ReviewNoteList");
            noteList.SelectionChanged += delegate { if (!loading && noteList.SelectedItem is ReviewNote) SelectNote((ReviewNote)noteList.SelectedItem); };
            panel.Children.Add(noteList);
            noteState.Foreground = muted; noteState.FontSize = 12; noteState.Margin = new Thickness(0, 8, 0, 9); panel.Children.Add(noteState);
            panel.Children.Add(Label("Title", 12, muted, 0, 0, 0, 0)); panel.Children.Add(noteTitle); noteTitle.MaxLength = 240;
            Grid times = new Grid(); times.ColumnDefinitions.Add(new ColumnDefinition()); times.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel start = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; start.Children.Add(Label("Start in seconds", 12, muted, 0, 0, 0, 0)); start.Children.Add(noteStart);
            StackPanel end = new StackPanel(); end.Children.Add(Label("End in seconds", 12, muted, 0, 0, 0, 0)); end.Children.Add(noteEnd);
            times.Children.Add(start); times.Children.Add(end); Grid.SetColumn(end, 1); panel.Children.Add(times);
            panel.Children.Add(Label("Leave a time blank when its position is unknown.", 12, muted, 0, 0, 0, 8));
            panel.Children.Add(Label("What you noticed", 12, muted, 0, 0, 0, 0)); ConfigureMultiline(noteObservation); panel.Children.Add(noteObservation);
            panel.Children.Add(Label("What to try", 12, muted, 0, 0, 0, 0)); ConfigureMultiline(noteSuggestion); panel.Children.Add(noteSuggestion);
            foreach (TextBox field in new TextBox[] { noteTitle, noteStart, noteEnd, noteObservation, noteSuggestion }) field.TextChanged += delegate { if (!loading) { editorDirty = true; noteState.Text = "Unsaved note changes"; } };
            AutomationProperties.SetName(noteTitle, "Note title"); AutomationProperties.SetName(noteStart, "Start seconds"); AutomationProperties.SetName(noteEnd, "End seconds");
            AutomationProperties.SetName(noteObservation, "Observation"); AutomationProperties.SetName(noteSuggestion, "Suggested change");
            WrapPanel saveActions = new WrapPanel(); panel.Children.Add(saveActions);
            saveActions.Children.Add(MakeButton("Save note", delegate { CommitNote(); }, true));
            saveActions.Children.Add(MakeButton("Mark reviewed", delegate {
                if (selected == null || !CommitNote()) return;
                selected.Status = "reviewed"; Save(); RenderGuide(); PopulateEditor(); SetStatus("Note marked reviewed. This records your review; it does not verify a MAGIX edit.", false);
            }, false));
            return panel;
        }

        private void ConfigureMultiline(TextBox box)
        { box.TextWrapping = TextWrapping.Wrap; box.AcceptsReturn = true; box.MinHeight = 76; box.MaxHeight = 160; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.MaxLength = 10000; }

        private StackPanel BuildControls()
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(Label("MAGIX connection", 20, ink, 0, 0, 0, 10));
            diagnosticSummary.TextWrapping = TextWrapping.Wrap; diagnosticSummary.Foreground = muted; diagnosticSummary.FontSize = 12; panel.Children.Add(diagnosticSummary);
            panel.Children.Add(Label("Available companion actions", 15, ink, 0, 18, 0, 8)); panel.Children.Add(actionList);
            panel.Children.Add(Label("Accessible controls", 15, ink, 0, 17, 0, 7));
            controlReport.IsReadOnly = true; controlReport.AcceptsReturn = true; controlReport.FontFamily = new FontFamily("Consolas"); controlReport.FontSize = 11;
            controlReport.Height = 260; controlReport.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; controlReport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AutomationProperties.SetName(controlReport, "MAGIX accessible control report");
            panel.Children.Add(controlReport);
            panel.Children.Add(MakeButton("Save control report", SaveControlReport, false));
            panel.Children.Add(Label("Commands are limited to the adapter’s built-in actions. Imported notes cannot add commands or send arbitrary keys.", 12, muted, 0, 8, 0, 0));
            return panel;
        }

        private void RefreshNotes()
        {
            loading = true;
            notePicker.ItemsSource = null; noteList.ItemsSource = null;
            notePicker.ItemsSource = document.Notes; noteList.ItemsSource = document.Notes;
            if (selected == null || !document.Notes.Contains(selected)) selected = document.Notes.FirstOrDefault();
            notePicker.SelectedItem = selected; noteList.SelectedItem = selected;
            loading = false;
            PopulateEditor(); RenderGuide();
        }

        private void SelectNote(ReviewNote note)
        {
            if (selected == note) return;
            if (editorDirty && !CommitNote())
            { loading = true; notePicker.SelectedItem = selected; noteList.SelectedItem = selected; loading = false; return; }
            selected = note; stepIndex = 0;
            loading = true; notePicker.SelectedItem = note; noteList.SelectedItem = note; loading = false;
            PopulateEditor(); RenderGuide();
        }

        private void PopulateEditor()
        {
            loading = true;
            projectName.Text = document.ProjectName; revisionName.Text = document.RevisionLabel;
            exportPath.Text = String.IsNullOrWhiteSpace(document.ExportPath) ? "No video export selected." : document.ExportPath;
            noteTitle.Text = selected == null ? "" : selected.Title;
            noteStart.Text = selected == null || !selected.StartSeconds.HasValue ? "" : selected.StartSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture);
            noteEnd.Text = selected == null || !selected.EndSeconds.HasValue ? "" : selected.EndSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture);
            noteObservation.Text = selected == null ? "" : selected.Observation;
            noteSuggestion.Text = selected == null ? "" : selected.Suggestion;
            noteState.Text = selected == null ? "Add a note to begin." : "Status: " + selected.Status.Replace('_', ' ');
            foreach (TextBox field in new TextBox[] { noteTitle, noteStart, noteEnd, noteObservation, noteSuggestion }) field.IsEnabled = selected != null;
            editorDirty = false; loading = false;
        }

        private bool CommitNote(bool persist = true)
        {
            if (selected == null) return true;
            double? start; double? end;
            if (String.IsNullOrWhiteSpace(noteTitle.Text)) { SetStatus("Enter a title for this note.", true); return false; }
            if (!TryOptionalTime(noteStart.Text, out start) || !TryOptionalTime(noteEnd.Text, out end) || (start.HasValue && end.HasValue && end.Value < start.Value))
            { SetStatus("Use seconds between 0 and 604800, with the end at or after the start; leave unknown times blank. Example: 24.2", true); return false; }
            bool suggestionChanged = ApplyNoteEdits(selected, noteTitle.Text.Trim(), start, end, noteObservation.Text, noteSuggestion.Text, editorDirty);
            if (editorDirty)
            {
                document.MappingConfirmed = false;
                if (suggestionChanged) stepIndex = 0;
            }
            if (persist) Save();
            editorDirty = false; RefreshNotes();
            SetStatus(persist ? (suggestionChanged ? "Note saved. The walkthrough now uses your revised suggestion as a manual step." : "Note saved locally.") : "Note edits prepared in memory for export.", false);
            return true;
        }

        internal static bool ApplyNoteEdits(ReviewNote note, string title, double? start, double? end, string observation, string suggestion, bool modified)
        {
            bool timeChanged = note.StartSeconds != start || note.EndSeconds != end;
            bool suggestionChanged = !String.Equals(note.Suggestion, suggestion, StringComparison.Ordinal);
            note.Title = title; note.StartSeconds = start; note.EndSeconds = end;
            if (timeChanged)
                note.TimeLabel = start.HasValue ? ReviewStore.FormatTime(start) + (end.HasValue && end.Value > start.Value ? "–" + ReviewStore.FormatTime(end) : "") : (end.HasValue ? "Until " + ReviewStore.FormatTime(end) : "Time not set");
            note.Observation = observation; note.Suggestion = suggestion;
            if (modified)
            {
                note.Status = "suggested";
                note.Extra["editedByUser"] = true;
                note.Extra["editedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                if (suggestionChanged)
                {
                    note.Steps.Clear();
                    note.Steps.Add(ReviewStore.ManualStep(suggestion));
                    note.Extra["recipeResetByUserEdit"] = true;
                }
                foreach (ReviewStep step in note.Steps) step.Completed = false;
            }
            return suggestionChanged;
        }

        internal static bool TryOptionalTime(string text, out double? result)
        {
            result = null;
            if (String.IsNullOrWhiteSpace(text)) return true;
            double number;
            if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Double.IsNaN(number) || Double.IsInfinity(number) || number < 0 || number > 604800) return false;
            result = number; return true;
        }

        private void RenderGuide()
        {
            guideDetail.Children.Clear(); projectLabel.Text = document.ProjectName + " · " + document.RevisionLabel;
            if (selected == null) { guideDetail.Children.Add(Label("Add or import notes in Review to begin.", 14, muted, 0, 20, 0, 0)); return; }
            guideDetail.Children.Add(Label(selected.Title, 21, ink, 0, 18, 0, 8));
            guideDetail.Children.Add(Label(selected.TimeLabel + " · " + selected.Status.Replace('_', ' '), 12, muted, 0, 0, 0, 12));
            object edited;
            if (selected.Extra.TryGetValue("editedByUser", out edited) && edited is bool && (bool)edited)
                guideDetail.Children.Add(Label("Edited locally · original source metadata retained", 12, muted, 0, 0, 0, 8));
            guideDetail.Children.Add(Label(selected.Observation, 13, muted, 0, 0, 0, 8));
            StackPanel proposal = new StackPanel(); proposal.Children.Add(Label("What to try", 13, accent, 0, 0, 0, 5)); proposal.Children.Add(Label(selected.Suggestion, 13, ink, 0, 0, 0, 0)); guideDetail.Children.Add(Card(proposal));
            CheckBox mapping = new CheckBox { IsChecked = document.MappingConfirmed, Margin = new Thickness(0, 4, 0, 6) };
            mapping.IsEnabled = CanRequestMagix(workOffline, busy) && connection != null && connection.Connected && !connection.Blocked;
            AutomationProperties.SetName(mapping, "I matched this export revision to the current MAGIX project"); AutomationProperties.SetAutomationId(mapping, "ConfirmProjectMapping");
            mapping.Content = new TextBlock { Text = "I matched this export revision to the current MAGIX project.", TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
            mapping.Checked += delegate { document.MappingConfirmed = true; RenderGuide(); };
            mapping.Unchecked += delegate { document.MappingConfirmed = false; RenderGuide(); };
            guideDetail.Children.Add(mapping);
            if (selected.Steps.Count == 0) return;
            stepIndex = Math.Max(0, Math.Min(stepIndex, selected.Steps.Count - 1));
            ReviewStep step = selected.Steps[stepIndex];
            StackPanel stepPanel = new StackPanel();
            stepPanel.Children.Add(Label("Step " + (stepIndex + 1) + " of " + selected.Steps.Count + " · " + (step.Kind == "assisted" ? "Companion action" : "You choose") + (step.Completed ? " · Done" : ""), 12, muted, 0, 0, 0, 10));
            stepPanel.Children.Add(Label(step.Title, 16, ink, 0, 0, 0, 7));
            stepPanel.Children.Add(Label(step.Instruction, 13, muted, 0, 0, 0, 12));
            WrapPanel buttons = new WrapPanel(); stepPanel.Children.Add(buttons);
            Button previous = MakeButton("Back", delegate { stepIndex--; RenderGuide(); }, false); previous.IsEnabled = stepIndex > 0; buttons.Children.Add(previous);
            bool assisted = step.Kind == "assisted";
            Button next = MakeButton(assisted ? "Do this step" : "I did this", async delegate {
                if (assisted)
                {
                    if (!document.MappingConfirmed || (step.Action != "focus_magix" && step.Action != "inspect_controls")) { SetStatus("Confirm the project mapping first. This note only supports built-in companion actions.", true); return; }
                    bool completed = await RunAction(step.Action, true);
                    if (!completed) return;
                }
                step.Completed = true; if (selected.Status != "reviewed") selected.Status = "in_progress";
                Save();
                if (stepIndex < selected.Steps.Count - 1) stepIndex++;
                else SetStatus("Walkthrough complete. Compare your next export before marking the note reviewed.", false);
                RenderGuide();
            }, true);
            next.IsEnabled = !assisted || (CanRequestMagix(workOffline, busy) && document.MappingConfirmed && connection != null && connection.Connected && !connection.Blocked);
            buttons.Children.Add(next);
            if (assisted && workOffline && stepIndex < selected.Steps.Count - 1)
                buttons.Children.Add(MakeButton("Next instruction", delegate { stepIndex++; RenderGuide(); SetStatus("Moved to the next instruction. No MAGIX action was run or marked complete.", false); }, false));
            if (assisted && !next.IsEnabled) stepPanel.Children.Add(Label(workOffline ? "Offline mode pauses MAGIX actions. Connect explicitly to run this step." : (connection == null || !connection.Connected ? "Connect MAGIX to run this step. The instructions remain available." : "Confirm the project mapping and close any blocking dialog first."), 12, muted, 0, 3, 0, 0));
            guideDetail.Children.Add(Card(stepPanel));
            WrapPanel other = new WrapPanel(); guideDetail.Children.Add(other);
            other.Children.Add(MakeButton("Preview export here", PreviewVideo, false));
            other.Children.Add(MakeButton("Restart steps", delegate { stepIndex = 0; foreach (ReviewStep s in selected.Steps) s.Completed = false; selected.Status = "suggested"; Save(); RenderGuide(); }, false));
        }

        private async Task RefreshConnection()
        {
            if (!CanRequestMagix(workOffline, busy)) return;
            SetBusy(true);
            try { ApplyConnection(await Task.Run(delegate { return Controller.Inspect(); })); }
            catch (Exception error) { ReportError(error); }
            finally { SetBusy(false); }
        }

        private async Task LaunchMagix()
        {
            if (!CanRequestMagix(workOffline, busy)) return;
            SetBusy(true);
            try
            {
                SetStatus("Launching MAGIX through its configured installation…", false);
                await Task.Run(delegate { Controller.Launch(); });
                await Task.Delay(1600);
                ApplyConnection(await Task.Run(delegate { return Controller.Inspect(); }));
                SetStatus(connection.Status, !connection.Connected);
            }
            catch (Exception error) { ReportError(error); }
            finally { SetBusy(false); }
        }

        private async Task<bool> RunAction(string id, bool requireMapping = false)
        {
            if (!CanRequestMagix(workOffline, busy)) return false;
            SetBusy(true);
            try
            {
                if (requireMapping)
                {
                    ApplyConnection(await Task.Run(delegate { return Controller.Inspect(); }));
                    if (!document.MappingConfirmed)
                    { SetStatus("The MAGIX project context changed. Match this export revision again before running the step.", true); return false; }
                }
                ActionResult result = await Task.Run(delegate { return Controller.Execute(id); });
                store.Log("Action " + id + ": " + result.Success + " " + result.Message);
                bool mappingCleared = ApplyConnection(await Task.Run(delegate { return Controller.Inspect(); }));
                SetStatus(result.Message + (mappingCleared ? " Project context changed; match the revision again." : ""), !result.Success);
                return result.Success;
            }
            catch (Exception error) { ReportError(error); return false; }
            finally { SetBusy(false); }
        }

        internal static bool ConnectionIdentityChanged(ConnectionReport previous, ConnectionReport next)
        {
            if (previous == null) return false;
            if (next == null) return true;
            return previous.ProcessId != next.ProcessId || previous.ProcessStartUtcTicks != next.ProcessStartUtcTicks || previous.WindowHandle != next.WindowHandle || !String.Equals(previous.WindowTitle, next.WindowTitle, StringComparison.Ordinal) || (previous.Connected && !next.Connected);
        }

        private bool ApplyConnection(ConnectionReport next)
        {
            if (workOffline) { document.MappingConfirmed = false; connection = null; RenderConnection(); return false; }
            bool changed = ConnectionIdentityChanged(connection, next);
            bool cleared = changed && document.MappingConfirmed;
            if (changed) document.MappingConfirmed = false;
            connection = next;
            RenderConnection();
            if (cleared) SetStatus("MAGIX changed project or window. Match this export revision again before assisted steps.", false);
            return cleared;
        }

        private void RenderConnection()
        {
            bool connected = !workOffline && connection != null && connection.Connected;
            connectionLabel.Text = workOffline ? "Working offline" : (connected ? (connection.Blocked ? "MAGIX needs attention" : "Connected to MAGIX") : "MAGIX is not connected");
            connectionLabel.Foreground = connected && !connection.Blocked ? accent : ink;
            connectionDetail.Text = workOffline ? "MAGIX access is paused. Review notes and local video are available." : (connection == null ? "Connect to inspect MAGIX, or launch it first if needed." : connection.Status);
            diagnosticSummary.Text = workOffline ? "Offline mode: no MAGIX inspection or control is performed. Connect explicitly when you are ready." : "Installation: " + (controller == null ? "Discovered when you connect" : controller.InstallPath) + Environment.NewLine + (connection == null ? "Not inspected yet." : "Version: " + connection.Version + Environment.NewLine + "Window: " + connection.WindowTitle + Environment.NewLine + "Process: " + connection.ProcessId + " · Accessible controls: " + connection.ControlCount);
            controlReport.Text = workOffline ? "MAGIX control inspection is disabled while working offline." : (connection == null || String.IsNullOrWhiteSpace(connection.ControlsText) ? "No accessible-control report yet. Connect after MAGIX opens." : connection.ControlsText);
            refreshButton.Content = connected ? "Refresh connection" : "Connect to MAGIX";
            AutomationProperties.SetName(refreshButton, workOffline ? "Connect to MAGIX and leave offline mode" : (connected ? "Refresh MAGIX connection" : "Connect to MAGIX"));
            refreshButton.ToolTip = workOffline ? "Leave offline mode and inspect MAGIX. This does not launch MAGIX." : "Read the current MAGIX connection and accessible controls.";
            launchButton.IsEnabled = CanRequestMagix(workOffline, busy);
            actionList.Children.Clear();
            List<ActionSpec> actions = workOffline || controller == null ? new List<ActionSpec> {
                new ActionSpec { Id = "focus_magix", Label = "Bring MAGIX forward", Description = "Available after connecting to MAGIX." },
                new ActionSpec { Id = "inspect_controls", Label = "Inspect accessible controls", Description = "Available after connecting to MAGIX." }
            } : controller.Actions;
            foreach (ActionSpec action in actions)
            {
                if (action.ChangesProject) continue;
                string actionId = action.Id;
                Button button = MakeButton(action.Label, async delegate { await RunAction(actionId); }, false);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.ToolTip = action.Description;
                button.IsEnabled = CanUseConnectedAction(workOffline, busy, connection, action.Id);
                actionList.Children.Add(button);
                actionList.Children.Add(Label(action.Description, 12, muted, 0, 0, 0, 10));
            }
            RenderGuide();
        }

        private void SetBusy(bool value)
        {
            busy = value; tabs.IsEnabled = !value; launchButton.IsEnabled = CanRequestMagix(workOffline, value); refreshButton.IsEnabled = !value; offlineToggle.IsEnabled = !value;
            if (!value) RenderConnection();
        }

        private void SetStatus(string message, bool error)
        { status.Text = message; status.Foreground = error ? MatrixTheme.Error : muted; }

        private void ReportError(Exception error)
        { store.Log(error.ToString()); SetStatus(error.Message, true); }

        private void Save() { store.Save(document); }

        private void ChooseExport()
        {
            if (editorDirty && !CommitNote()) return;
            OpenFileDialog picker = new OpenFileDialog { Title = "Choose the export this review refers to", Filter = "Video exports|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.m4v|All files|*.*", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            document.ExportPath = picker.FileName; document.MappingConfirmed = false; Save(); PopulateEditor(); RenderGuide();
            SetStatus("Video linked. Confirm the export revision matches your current project before assisted steps.", false);
        }

        private void PreviewVideo()
        {
            if (String.IsNullOrWhiteSpace(document.ExportPath)) { SetStatus("Choose an existing video export in Review first.", true); tabs.SelectedIndex = 2; return; }
            string candidate = document.ExportPath;
            if (candidate.Length < 3 || !Char.IsLetter(candidate[0]) || candidate[1] != ':' || (candidate[2] != '\\' && candidate[2] != '/'))
            { SetStatus("Choose a local video export. Network and web paths are not opened by the preview.", true); return; }
            string fullPath = Path.GetFullPath(candidate);
            if (new DriveInfo(Path.GetPathRoot(fullPath)).DriveType == DriveType.Network)
            { SetStatus("Choose a video stored on a local drive for preview.", true); return; }
            if (!File.Exists(fullPath)) { SetStatus("The linked video was not found. Choose an existing export in Review.", true); tabs.SelectedIndex = 2; return; }
            string extension = Path.GetExtension(document.ExportPath).ToLowerInvariant();
            if (!new string[] { ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v" }.Contains(extension)) { SetStatus("Choose a supported local video file to preview.", true); return; }
            Window player = new VideoPreviewWindow(fullPath, selected == null ? 0 : selected.StartSeconds.GetValueOrDefault()) { Owner = this };
            player.Show();
        }

        private void ImportNotes()
        {
            if (editorDirty && !CommitNote()) return;
            OpenFileDialog picker = new OpenFileDialog { Title = "Import review notes", Filter = "ED-IT Buddy notes|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            ReviewDocument imported = store.Read(picker.FileName);
            if (File.Exists(store.StatePath)) File.Copy(store.StatePath, Path.Combine(store.DataPath, "notes-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"));
            imported.MappingConfirmed = false;
            store.Save(imported);
            document = imported; selected = document.Notes.FirstOrDefault(); stepIndex = 0; RefreshNotes();
            SetStatus("Imported " + document.Notes.Count + " notes. Previous saved notes were retained before import.", false);
        }

        private void ExportNotes()
        {
            SaveFileDialog picker = new SaveFileDialog { Title = "Export review notes", Filter = "ED-IT Buddy notes|*.json", FileName = "edit-buddy-notes.json", AddExtension = true };
            if (picker.ShowDialog(this) != true) return;
            bool pendingEditorChanges = editorDirty;
            if (editorDirty && !CommitNote(false)) return;
            try { store.Write(picker.FileName, document); SetStatus("Notes exported to " + picker.FileName, false); }
            catch
            {
                if (pendingEditorChanges) { editorDirty = true; noteState.Text = "Unsaved note changes"; }
                throw;
            }
        }

        private void AddNote()
        {
            if (editorDirty && !CommitNote()) return;
            if (document.Notes.Count >= 1000) { SetStatus("This document has reached the 1000-note limit.", true); return; }
            ReviewNote note = new ReviewNote { Id = Guid.NewGuid().ToString("N"), Title = "New revision note", StartSeconds = 0, EndSeconds = 0, TimeLabel = "00:00.000", Observation = "", Suggestion = "Describe the change you want to compare.", Status = "suggested" };
            note.Steps.Add(ReviewStore.ManualStep(note.Suggestion)); document.Notes.Add(note); selected = note; stepIndex = 0; Save(); RefreshNotes(); tabs.SelectedIndex = 2; noteTitle.Focus(); noteTitle.SelectAll();
        }

        private void SaveControlReport()
        {
            SaveFileDialog picker = new SaveFileDialog { Title = "Save MAGIX control report", Filter = "Text report|*.txt", FileName = "magix-controls.txt", AddExtension = true };
            if (picker.ShowDialog(this) == true) { File.WriteAllText(picker.FileName, diagnosticSummary.Text + Environment.NewLine + Environment.NewLine + controlReport.Text); SetStatus("Control report saved.", false); }
        }
    }

    internal sealed class VideoPreviewWindow : Window
    {
        private readonly MediaElement media;
        private readonly System.Windows.Threading.DispatcherTimer positionTimer = new System.Windows.Threading.DispatcherTimer();
        public VideoPreviewWindow(string path, double start)
        {
            Title = "Export preview · " + Path.GetFileName(path); Width = 800; Height = 540; MinWidth = 480; MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; MatrixTheme.Apply(this);
            DockPanel root = new DockPanel(); Content = root;
            StackPanel footer = new StackPanel { Margin = new Thickness(12) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            TextBlock position = new TextBlock { Text = "Playback time not available yet", Foreground = MatrixTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }; footer.Children.Add(position);
            AutomationProperties.SetName(position, "Approximate playback position");
            AutomationProperties.SetHelpText(position, "This readout follows Windows media playback time; it is not frame-accurate navigation.");
            WrapPanel controls = new WrapPanel(); footer.Children.Add(controls);
            TextBlock state = new TextBlock { Text = "Loading local video…", Foreground = MatrixTheme.Accent, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(state, Dock.Top); root.Children.Add(state);
            media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform };
            AutomationProperties.SetName(media, "Local video export preview");
            root.Children.Add(media);
            Action updatePosition = delegate { position.Text = ReviewStore.FormatTime(media.Position.TotalSeconds) + " / " + (media.NaturalDuration.HasTimeSpan ? ReviewStore.FormatTime(media.NaturalDuration.TimeSpan.TotalSeconds) : "unknown duration") + " · approximate playback time"; };
            positionTimer.Interval = TimeSpan.FromMilliseconds(250);
            positionTimer.Tick += delegate { updatePosition(); };
            Add(controls, "Play", delegate { media.Play(); }); Add(controls, "Pause", delegate { media.Pause(); });
            Add(controls, "To note", delegate { media.Pause(); double limit = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : start; media.Position = TimeSpan.FromSeconds(Math.Min(start, limit)); updatePosition(); });
            Add(controls, "−1 second", delegate { media.Pause(); media.Position = TimeSpan.FromSeconds(Math.Max(0, media.Position.TotalSeconds - 1)); });
            Add(controls, "+1 second", delegate { media.Pause(); double end = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : Double.MaxValue; media.Position = TimeSpan.FromSeconds(Math.Min(end, media.Position.TotalSeconds + 1)); });
            media.MediaOpened += delegate { media.Pause(); double limit = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : start; media.Position = TimeSpan.FromSeconds(Math.Min(start, limit)); state.Text = "Local preview · " + Path.GetFileName(path) + " · note at " + ReviewStore.FormatTime(start) + (start > limit ? " (beyond this file’s duration)" : ""); updatePosition(); positionTimer.Start(); };
            media.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e) { positionTimer.Stop(); state.Text = "Windows could not play this file: " + (e.ErrorException == null ? "unsupported media" : e.ErrorException.Message) + ". Try a compatible MP4 or WMV export."; };
            Loaded += delegate { media.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute); media.Play(); };
            Closed += delegate { positionTimer.Stop(); media.Stop(); media.Close(); };
        }
        private static void Add(Panel panel, string text, Action action)
        { Button button = new Button { Content = text, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 7) }; AutomationProperties.SetName(button, text == "To note" ? "Seek to selected review note" : text); button.Click += delegate { action(); }; panel.Children.Add(button); }
    }
}
