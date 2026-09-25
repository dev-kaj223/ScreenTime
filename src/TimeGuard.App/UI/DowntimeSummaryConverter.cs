using System.Globalization;
using System.Windows.Data;
using TimeGuard.Models;

namespace TimeGuard.UI;

public sealed class DowntimeSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var periods = (value as IEnumerable<BlockedPeriod>)?.Where(p => p.Enabled).ToArray() ?? [];
        string Clock(int minute) => new TimeOnly(minute / 60, minute % 60).ToString("h:mm tt", CultureInfo.InvariantCulture);
        return periods.Length == 0 ? "No downtime" : string.Join(", ", periods.Select(p =>
            $"{p.StartDayOfWeek}: {Clock(p.StartMinute)}–{Clock(p.EndMinute)}{(p.EndDayOffset == 1 ? " next day" : "")}"));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
