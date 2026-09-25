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
    private ObservableCollection<AppRule> _rules = [];

    private record UsageRow(string ProcessName, string UsageMinutesDisplay, bool Blocked);

    internal SettingsWindow(DatabaseService db, RuntimeOptions? runtime = null)
    {
        InitializeComponent();
        _db = db;
        _runtime = runtime ?? RuntimeOptions.Development();
        _preferences = new NotificationPreferenceStore(_runtime.Paths);
        LoadRules();
        LoadUsage();
        LoadGlobalCap();
        NotificationEditor.Load(_preferences.Load());
        StartupCheckBox.IsEnabled = _runtime.AllowsStartup;
        StartupCheckBox.IsChecked = StartupHelper.IsRegistered(_runtime);
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

        // Always show at least a test entry so the UI can be verified
        var items = recent.Count > 0 ? recent : new[] { "[test] no-recent-sessions" };

        foreach (var proc in items)
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
                if (captured.StartsWith("[test]")) return;
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
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditWindow(new AppRule());
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnEditRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not AppRule selected) return;
        var dialog = new RuleEditWindow(selected);
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            dialog.Result.Id = selected.Id;
            SaveRuleWithFeedback(dialog.Result);
            LoadRules();
        }
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not AppRule selected) return;
        if (WpfMessageBox.Show($"Remove rule for \'{selected.DisplayName}\'?", "Confirm",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _db.DeleteRule(selected.Id);
            LoadRules();
        }
    }

    private void OnPickProcess(object sender, RoutedEventArgs e)
    {
        var running = Process.GetProcesses()
            .Select(p => p.ProcessName.ToLowerInvariant())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var picker = new ProcessPickerWindow(running);
        if (picker.ShowDialog() == true && picker.SelectedProcess is not null)
        {
            var rule = new AppRule
            {
                ProcessName = picker.SelectedProcess,
                DisplayName = picker.SelectedProcess,
                Enabled = true
            };
            var dialog = new RuleEditWindow(rule);
            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                SaveRuleWithFeedback(dialog.Result);
                LoadRules();
            }
        }
    }

    private void OnViewDashboard(object sender, RoutedEventArgs e)
    {
        new DashboardWindow(_db).ShowDialog();
    }

    private void SaveRuleWithFeedback(AppRule rule)
    {
        try { _db.SaveRule(rule); }
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

    // ── Global Cap Tab ────────────────────────────────────────────────────────

    private void LoadGlobalCap()
    {
        OverallCapBox.Text = _db.GetSetting("OverallDailyLimitMinutes") ?? "0";
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
        _db.SetSetting("PasswordHash", hash);
        _db.SetSetting("PasswordSalt", salt);

        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        PasswordStatusText.Text = "✅ Password changed successfully.";
        PasswordStatusText.Visibility = Visibility.Visible;
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        if (StartupCheckBox.IsChecked == true) StartupHelper.Register(_runtime);
        else StartupHelper.Unregister(_runtime);
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try { _preferences.Save(NotificationEditor.Read()); }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, ex.Message, "Notification preferences", MessageBoxButton.OK);
            return;
        }
        if (int.TryParse(OverallCapBox.Text, out var cap) && cap.ToString() != (_db.GetSetting("OverallDailyLimitMinutes") ?? "0"))
            _db.SetSetting("OverallDailyLimitMinutes", cap.ToString());

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
