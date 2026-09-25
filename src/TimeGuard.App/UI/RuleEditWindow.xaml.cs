using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using TimeGuard.Models;
using TimeGuard.ViewModels;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace TimeGuard.UI;

public partial class RuleEditWindow : Window
{
    private readonly RuleEditorDraft _draft;
    private readonly Dictionary<DayOfWeek, CheckBox> _dayBoxes = new();
    private DowntimeGroup? _editing;
    public AppRule? Result { get; private set; }

    public RuleEditWindow(AppRule existing)
    {
        InitializeComponent();
        _draft = new(existing);
        DailyLimits.ItemsSource = _draft.Days;
        DisplayNameBox.Text = existing.DisplayName;
        ProcessNameBox.Text = existing.ProcessName;
        foreach (var day in RuleEditorDraft.Weekdays)
        {
            var box = new CheckBox { Content = day.ToString()[..3], Margin = new Thickness(0, 0, 14, 0),
                IsChecked = day == DayOfWeek.Monday };
            AutomationProperties.SetAutomationId(box, $"Period{day}Box");
            AutomationProperties.SetName(box, day.ToString());
            _dayBoxes.Add(day, box);
            PeriodDaysPanel.Children.Add(box);
        }
        foreach (var box in new[] { PeriodStartHourBox, PeriodEndHourBox }) box.ItemsSource = Enumerable.Range(1, 12).ToArray();
        foreach (var box in new[] { PeriodStartMinuteBox, PeriodEndMinuteBox }) box.ItemsSource = Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        foreach (var box in new[] { PeriodStartMeridiemBox, PeriodEndMeridiemBox }) box.ItemsSource = new[] { "AM", "PM" };
        SetClock(PeriodStartHourBox, PeriodStartMinuteBox, PeriodStartMeridiemBox, 9 * 60);
        SetClock(PeriodEndHourBox, PeriodEndMinuteBox, PeriodEndMeridiemBox, 17 * 60);
        RefreshPeriods();
    }

    private static void SetClock(ComboBox hour, ComboBox minute, ComboBox meridiem, int value)
    {
        var parts = RuleEditorValues.ClockParts(value);
        hour.SelectedItem = parts.Hour;
        minute.SelectedIndex = parts.Minute;
        meridiem.SelectedItem = parts.Meridiem;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_editing is not null) { ShowError("Apply the downtime changes or cancel its edit before saving the rule."); return; }
        if (!_draft.TryBuildRule(DisplayNameBox.Text, ProcessNameBox.Text, out var result, out var error))
        { ShowError(error); return; }
        Result = result;
        DialogResult = true;
    }

    private void OnAddPeriod(object sender, RoutedEventArgs e)
    {
        if (!_draft.TrySetDowntime(_editing, _dayBoxes.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key),
            PeriodStartHourBox.SelectedItem is int sh ? sh : 0, PeriodStartMinuteBox.SelectedIndex, PeriodStartMeridiemBox.SelectedItem as string,
            PeriodEndHourBox.SelectedItem is int eh ? eh : 0, PeriodEndMinuteBox.SelectedIndex, PeriodEndMeridiemBox.SelectedItem as string,
            PeriodNextDayBox.IsChecked == true, PeriodEnabledBox.IsChecked == true, out var error))
        { ShowError(error); return; }
        FinishEdit();
        RefreshPeriods();
    }

    private void OnEditPeriod(object sender, RoutedEventArgs e)
    {
        if (_editing is not null) { ShowError("Apply or cancel the current downtime edit first."); return; }
        if (PeriodsList.SelectedItem is not DowntimeGroup group) { ShowError("Select a downtime row to edit."); return; }
        _editing = group;
        foreach (var (day, box) in _dayBoxes) box.IsChecked = group.Periods.Any(p => p.StartDayOfWeek == day);
        SetClock(PeriodStartHourBox, PeriodStartMinuteBox, PeriodStartMeridiemBox, group.First.StartMinute);
        SetClock(PeriodEndHourBox, PeriodEndMinuteBox, PeriodEndMeridiemBox, group.First.EndMinute);
        PeriodNextDayBox.IsChecked = group.First.EndDayOffset == 1;
        PeriodEnabledBox.IsChecked = group.First.Enabled;
        AddPeriodButton.Content = "Apply downtime changes";
        CancelPeriodEditButton.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        PeriodDaysPanel.BringIntoView();
    }

    private void OnRemovePeriod(object sender, RoutedEventArgs e)
    {
        if (_editing is not null) { ShowError("Apply or cancel the current downtime edit first."); return; }
        if (PeriodsList.SelectedItem is not DowntimeGroup group) { ShowError("Select a downtime row to remove."); return; }
        _draft.Remove(group);
        RefreshPeriods();
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnCancelPeriodEdit(object sender, RoutedEventArgs e) => FinishEdit();
    private void FinishEdit()
    {
        _editing = null;
        AddPeriodButton.Content = "Add downtime";
        CancelPeriodEditButton.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }
    private void RefreshPeriods()
    {
        var groups = _draft.Groups;
        PeriodsList.ItemsSource = groups;
        EmptyPeriodsText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
