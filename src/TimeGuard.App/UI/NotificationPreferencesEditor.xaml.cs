using System.Windows;
using System.Windows.Controls;
using TimeGuard.Models;
using TimeGuard.Services;

namespace TimeGuard.UI;

public partial class NotificationPreferencesEditor : System.Windows.Controls.UserControl
{
    private bool _loading;
    private readonly NotificationPreviewService _preview = new();
    public NotificationPreferencesEditor()
    {
        InitializeComponent();
        PresetBox.ItemsSource = Enum.GetValues<NotificationPreset>();
        PreviewKind.ItemsSource = Enum.GetValues<NotificationKind>();
        PreviewKind.SelectedIndex = 0;
        foreach (var box in new[] { QuotaTen, QuotaFive, GraceStart, GraceFive, FinalMinute, BlockedNotice })
        { box.Checked += OnCustom; box.Unchecked += OnCustom; box.Foreground = System.Windows.Media.Brushes.White; }
        foreach (var box in new[] { CountdownBox, InfoColor, WarnColor, CriticalColor }) box.TextChanged += OnCustom;
        Unloaded += (_, _) => _preview.Dispose();
        Load(NotificationPreferences.Standard);
    }

    public void Load(NotificationPreferences value)
    {
        _loading = true;
        try
        {
            PresetBox.SelectedItem = value.Preset;
            QuotaTen.IsChecked = value.QuotaTenMinutes; QuotaFive.IsChecked = value.QuotaFiveMinutes;
            GraceStart.IsChecked = value.GraceStarted; GraceFive.IsChecked = value.GraceFiveMinutes;
            FinalMinute.IsChecked = value.GraceFinalMinute; BlockedNotice.IsChecked = value.Blocked;
            CountdownBox.Text = value.CountdownSeconds.ToString();
            InfoColor.Text = value.InformationalColor ?? ""; WarnColor.Text = value.WarningColor ?? "";
            CriticalColor.Text = value.CriticalColor ?? ""; ErrorText.Text = "";
        }
        finally { _loading = false; }
    }

    public NotificationPreferences Read()
    {
        if (!int.TryParse(CountdownBox.Text, out var seconds)) throw new ArgumentException("Enter a countdown duration from 1 to 60 seconds.");
        static string? Color(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var result = new NotificationPreferences
        {
            Preset = (NotificationPreset)PresetBox.SelectedItem, CountdownSeconds = seconds,
            QuotaTenMinutes = QuotaTen.IsChecked == true, QuotaFiveMinutes = QuotaFive.IsChecked == true,
            GraceStarted = GraceStart.IsChecked == true, GraceFiveMinutes = GraceFive.IsChecked == true,
            GraceFinalMinute = FinalMinute.IsChecked == true, Blocked = BlockedNotice.IsChecked == true,
            InformationalColor = Color(InfoColor.Text), WarningColor = Color(WarnColor.Text), CriticalColor = Color(CriticalColor.Text)
        };
        result.Validate();
        return result;
    }

    private void OnCustom(object sender, RoutedEventArgs e) { if (!_loading) PresetBox.SelectedItem = NotificationPreset.Custom; }
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PresetBox.SelectedItem is NotificationPreset.Standard) Load(NotificationPreferences.Standard);
        else if (PresetBox.SelectedItem is NotificationPreset.Minimal) Load(NotificationPreferences.Minimal);
    }
    private void OnReset(object sender, RoutedEventArgs e) => Load(NotificationPreferences.Standard);
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        try { _preview.Show((NotificationKind)PreviewKind.SelectedItem, Read()); ErrorText.Text = ""; }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}
