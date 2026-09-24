using System.Globalization;
using System.Windows;
using TimeGuard.Models;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TimeGuard.UI;

public partial class RuleEditWindow : Window
{
    private readonly bool _enabled;

    private sealed record WeekdayEditor(
        DayOfWeek DayOfWeek,
        string Name,
        WpfTextBox LimitBox);

    public AppRule? Result { get; private set; }

    public RuleEditWindow(AppRule existing)
    {
        InitializeComponent();

        _enabled              = existing.Enabled;
        PeriodDayBox.ItemsSource = Enum.GetValues<DayOfWeek>();
        PeriodDayBox.SelectedItem = DayOfWeek.Monday;
        foreach (var period in existing.BlockedPeriods) PeriodsList.Items.Add(period);
        DisplayNameBox.Text   = existing.DisplayName;
        ProcessNameBox.Text   = existing.ProcessName;
        BreakEveryBox.Text    = existing.BreakEveryMinutes.ToString();
        BreakDurationBox.Text = existing.BreakDurationMinutes.ToString();

        foreach (var schedule in existing.GetWeekSchedule())
        {
            var editor = GetWeekdayEditors().First(x => x.DayOfWeek == schedule.DayOfWeek);
            editor.LimitBox.Text = schedule.DailyLimitMinutes.ToString();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) ||
            string.IsNullOrWhiteSpace(ProcessNameBox.Text))
        {
            ShowError("App name and process name are required.");
            return;
        }

        if (!int.TryParse(BreakEveryBox.Text, out var breakEvery) || breakEvery < 0)
        {
            ShowError("Break interval must be a non-negative number.");
            return;
        }

        if (!int.TryParse(BreakDurationBox.Text, out var breakDur) || breakDur < 0)
        {
            ShowError("Break duration must be a non-negative number.");
            return;
        }

        var schedules = new List<AppRuleDaySchedule>();
        foreach (var editor in GetWeekdayEditors())
        {
            if (!int.TryParse(editor.LimitBox.Text, out var limit) || limit < 0)
            {
                ShowError($"{editor.Name} limit must be a non-negative number.");
                return;
            }

            schedules.Add(new AppRuleDaySchedule
            {
                DayOfWeek          = editor.DayOfWeek,
                DailyLimitMinutes  = limit
            });
        }

        var invalidBreakDays = schedules
            .Where(schedule => schedule.DailyLimitMinutes > 0 && breakEvery > 0 && breakEvery >= schedule.DailyLimitMinutes)
            .Select(schedule => schedule.DayLabel)
            .ToList();

        if (invalidBreakDays.Count > 0)
        {
            ShowError(invalidBreakDays.Count == 1
                ? $"Break interval must be less than the daily limit for {invalidBreakDays[0]}."
                : "Break interval must be less than the daily limit for each limited day.");
            return;
        }

        if (breakEvery > 0 && breakDur > breakEvery)
        {
            ShowError("Break duration must be less than or equal to the break interval.");
            return;
        }

        var rule = new AppRule
        {
            DisplayName          = DisplayNameBox.Text.Trim(),
            ProcessName          = ProcessNameBox.Text.Trim().ToLowerInvariant(),
            BreakEveryMinutes    = breakEvery,
            BreakDurationMinutes = breakDur,
            Enabled              = _enabled
        };
        rule.SetWeekSchedule(schedules);
        rule.BlockedPeriods = PeriodsList.Items.Cast<BlockedPeriod>().ToList();

        Result       = rule;
        DialogResult = true;
        Close();
    }

    private IEnumerable<WeekdayEditor> GetWeekdayEditors()
    {
        yield return new WeekdayEditor(DayOfWeek.Monday, "Monday", MondayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Tuesday, "Tuesday", TuesdayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Wednesday, "Wednesday", WednesdayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Thursday, "Thursday", ThursdayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Friday, "Friday", FridayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Saturday, "Saturday", SaturdayLimitBox);
        yield return new WeekdayEditor(DayOfWeek.Sunday, "Sunday", SundayLimitBox);
    }

    private void OnAddPeriod(object sender, RoutedEventArgs e)
    {
        if (!TimeOnly.TryParseExact(PeriodStartBox.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(PeriodEndBox.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        { ShowError("Downtime times must use HH:mm (e.g. 22:00)."); return; }
        var period = new BlockedPeriod { StartDayOfWeek = (DayOfWeek)PeriodDayBox.SelectedItem,
            StartMinute = start.Hour * 60 + start.Minute, EndMinute = end.Hour * 60 + end.Minute,
            EndDayOffset = PeriodNextDayBox.IsChecked == true ? 1 : 0 };
        try { period.Validate(); }
        catch (ArgumentException ex) { ShowError(ex.Message); return; }
        PeriodsList.Items.Add(period);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnRemovePeriod(object sender, RoutedEventArgs e)
    {
        if (PeriodsList.SelectedItem is { } selected) PeriodsList.Items.Remove(selected);
    }

    private void ShowError(string message)
    {
        ErrorText.Text       = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
