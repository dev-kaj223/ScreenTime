using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TimeGuard.Helpers;
using TimeGuard.Models;
using TimeGuard.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace TimeGuard.UI;

public partial class SettingsWindow : Window
{
    private readonly DatabaseService _db;
    private readonly RuntimeOptions _runtime;
    private readonly NotificationPreferenceStore _preferences;
    private readonly Action? _requestSettings;
    private readonly Action? _settingsChanged;
    private readonly DashboardWindow _today;
    private NotificationPreferences _savedPreferences;
    internal bool IsSettingsUnlocked { get; private set; }
    private bool _loadingStartup = true;
    private ObservableCollection<AppRule> _rules = [];

    private record UsageRow(string ProcessName, string UsageMinutesDisplay, bool Blocked);

    internal SettingsWindow(DatabaseService db, RuntimeOptions? runtime = null,
        Action? requestSettings = null, Action? settingsChanged = null, Func<StatusSnapshot?>? readStatus = null)
    {
        InitializeComponent();
        _db = db;
        _requestSettings = requestSettings;
        _settingsChanged = settingsChanged;
        _runtime = runtime ?? RuntimeOptions.Development();
        _preferences = new NotificationPreferenceStore(_runtime.Paths);
        LoadRules();
        LoadUsage();

        _savedPreferences = _preferences.Load();
        NotificationEditor.Load(_savedPreferences);
        _loadingStartup = true;
        try
        {
            StartupCheckBox.IsEnabled = _runtime.AllowsStartup;
            StartupCheckBox.IsChecked = StartupHelper.IsRegistered(_runtime);
        }
        finally { _loadingStartup = false; }
        _today = new DashboardWindow(db, readStatus);
        TodayHost.Content = _today;
        Closing += OnMainClosing;
        SourceInitialized += (_, _) => MainWindowPlacement.Center(this, System.Windows.Forms.Cursor.Position);
    }

    internal void EnterSettings()
    {
        IsSettingsUnlocked = true;
        TodayHost.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Visible;
        SettingsButton.Content = "Settings 🔓";
    }

    internal void EnterToday()
    {
        if (SettingsContent.Visibility == Visibility.Visible && !TryLeaveSettings()) return;
        SettingsContent.Visibility = Visibility.Collapsed;
        TodayHost.Visibility = Visibility.Visible;
    }

    private void OnViewSettings(object sender, RoutedEventArgs e)
    {
        if (IsSettingsUnlocked) EnterSettings();
        else _requestSettings?.Invoke();
    }

    private bool HasUnsavedChanges()
    {
        if (NewPasswordBox.Password.Length > 0 || ConfirmPasswordBox.Password.Length > 0) return true;
        try { return NotificationEditor.Read() != _savedPreferences; }
        catch (ArgumentException) { return true; }
    }

    private bool TryLeaveSettings()
    {
        if (!HasUnsavedChanges()) return true;
        var answer = WpfMessageBox.Show(this, "Save changes to Settings before leaving?", "Unsaved Settings",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return TrySavePreferences();
        NotificationEditor.Load(_savedPreferences);
        NewPasswordBox.Clear(); ConfirmPasswordBox.Clear();
        return true;
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (SettingsContent.Visibility == Visibility.Visible && !TryLeaveSettings()) e.Cancel = true;
        if (!e.Cancel) IsSettingsUnlocked = false;
    }

    // ── Rules Tab ─────────────────────────────────────────────────────────────

    private void LoadRules()
    {
        _rules = new ObservableCollection<AppRule>(_db.GetRules());
        RulesGrid.ItemsSource = _rules;

        BuildRecentPanel(_db.GetRecentlySeenProcesses(7));
    }

    private void BuildRecentPanel(IReadOnlyList<string> recent)
    {
        RecentPanel.Children.Clear();

        if (recent.Count == 0)
        {
            RecentPanel.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "No recent applications. Use Add Rule or Pick Process to get started.",
                Foreground = (System.Windows.Media.Brush)FindResource("SubtextBrush"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var proc in recent)
        {
            var row = new System.Windows.Controls.Grid();
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new GridLength(100) });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = proc,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 6, 4, 6)
            };
            System.Windows.Controls.Grid.SetColumn(label, 0);

            var captured = proc; // capture for lambda
            var btn = new System.Windows.Controls.Button
            {
                Content = "Add Rule →",
                Style   = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(8, 4, 8, 4),
                Tag     = proc
            };
            btn.Click += (_, _) =>
            {
                PromoteProcess(captured);
            };
            System.Windows.Controls.Grid.SetColumn(btn, 1);

            row.Children.Add(label);
            row.Children.Add(btn);
            RecentPanel.Children.Add(row);
        }

        RecentPanel.Visibility = Visibility.Visible;
    }

    private void PromoteProcess(string processName)
    {
        var rule = new AppRule
        {
            ProcessName = processName,
            DisplayName = processName,
            Enabled     = true
        };
        var dialog = new RuleEditWindow(rule) { Owner = this };
        if (dialog.ShowDialog() == true && IsVisible && dialog.Result is not null)
        {
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditWindow(new AppRule()) { Owner = this };
        if (dialog.ShowDialog() == true && IsVisible && dialog.Result is not null)
        {
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnEditRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not AppRule selected) return;
        var dialog = new RuleEditWindow(selected) { Owner = this };
        if (dialog.ShowDialog() == true && IsVisible && dialog.Result is not null)
        {
            dialog.Result.Id = selected.Id;
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not AppRule selected) return;
        if (WpfMessageBox.Show(this, $"Remove rule for \'{selected.DisplayName}\'?", "Confirm",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes && IsVisible)
        {
            _db.DeleteRule(selected.Id);
            LoadRules();
            _settingsChanged?.Invoke();
        }
    }

    private void OnPickProcess(object sender, RoutedEventArgs e)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { names.Add(process.ProcessName.ToLowerInvariant()); }
                catch (InvalidOperationException) { } // Exited while the picker was opening.
                catch (System.ComponentModel.Win32Exception) { } // Not readable by this user.
            }
        }
        var running = names.OrderBy(name => name).ToList();

        var picker = new ProcessPickerWindow(running) { Owner = this };
        if (picker.ShowDialog() == true && IsVisible && picker.SelectedProcess is not null)
        {
            var rule = new AppRule
            {
                ProcessName = picker.SelectedProcess,
                DisplayName = picker.SelectedProcess,
                Enabled = true
            };
            var dialog = new RuleEditWindow(rule) { Owner = this };
            if (dialog.ShowDialog() == true && IsVisible && dialog.Result is not null)
            {
                SaveRuleWithFeedback(dialog.Result);
                LoadRules();
            }
        }
    }

    private void OnViewDashboard(object sender, RoutedEventArgs e)
    {
        EnterToday();
    }

    private void SaveRuleWithFeedback(AppRule rule)
    {
        try { _db.SaveRule(rule); _settingsChanged?.Invoke(); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            WpfMessageBox.Show(this, "An application with this process name already has a rule. Edit the existing rule.",
                "Duplicate application", MessageBoxButton.OK);
        }
        catch (ArgumentException ex)
        {
            WpfMessageBox.Show(this, ex.Message, "Invalid application", MessageBoxButton.OK);
        }
    }

    // ── Usage Tab ─────────────────────────────────────────────────────────────

    private void LoadUsage()
    {
        var evaluator = new DowntimeEvaluator(TimeZoneInfo.Local);
        var engine = new RulesEngine();
        var now = DateTimeOffset.UtcNow;
        var log = _db.LoadLog(evaluator.LocalDate(now));
        var rules = _db.GetRules().ToDictionary(r => ProcessInstance.NormalizeKey(r.ProcessName));
        UsageGrid.ItemsSource = log.Entries
            .Select(e => new UsageRow(e.ProcessName, $"{e.UsageMinutes:F1}",
                rules.TryGetValue(ProcessInstance.NormalizeKey(e.ProcessName), out var rule) &&
                !engine.Evaluate(PolicySnapshot.Capture(rule, log, now, false, evaluator,
                    date => _db.LoadLog(date).Entries.FirstOrDefault(u => u.ProcessName == e.ProcessName)?.QuotaSeconds ?? 0)).MayLaunch))
            .ToList();
    }

    // ── Security Tab ─────────────────────────────────────────────────────────

    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
        {
            PasswordStatusText.Text = "Passwords do not match.";
            PasswordStatusText.Visibility = Visibility.Visible;
            return;
        }
        if (NewPasswordBox.Password.Length < 6)
        {
            PasswordStatusText.Text = "Password must be at least 6 characters.";
            PasswordStatusText.Visibility = Visibility.Visible;
            return;
        }

        var (hash, salt) = PasswordHelper.Hash(NewPasswordBox.Password);
        _db.SavePassword(hash, salt);

        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        PasswordStatusText.Text = "✅ Password changed successfully.";
        PasswordStatusText.Visibility = Visibility.Visible;
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup) return;
        if (StartupCheckBox.IsChecked == true) StartupHelper.Register(_runtime);
        else StartupHelper.Unregister(_runtime);
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (TrySavePreferences()) EnterToday();
    }

    private bool TrySavePreferences()
    {
        if (NewPasswordBox.Password.Length > 0 || ConfirmPasswordBox.Password.Length > 0)
        {
            WpfMessageBox.Show(this, "Use Change Password to apply the password fields.", "Security", MessageBoxButton.OK);
            return false;
        }
        try { var preferences = NotificationEditor.Read(); _preferences.Save(preferences); _savedPreferences = preferences; }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, ex.Message, "Notification preferences", MessageBoxButton.OK);
            return false;
        }
        _settingsChanged?.Invoke();
        return true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        NotificationEditor.Load(_savedPreferences);
        NewPasswordBox.Clear(); ConfirmPasswordBox.Clear();
        EnterToday();
    }
}
