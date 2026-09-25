using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System.Linq;
using System.Windows;
using TimeGuard.Services;
using TimeGuard.Models;
using TimeGuard.ViewModels;
using System.Windows.Threading;

namespace TimeGuard.UI;

public partial class DashboardWindow : System.Windows.Controls.UserControl
{
    private readonly DatabaseService _db;
    private readonly Func<StatusSnapshot?> _read;
    private readonly StatusViewModel _status = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _lastHistoryRefresh;
    private HashSet<string> _configured = [];
    private IReadOnlyList<(DateOnly Bucket, string ProcessName, double UsageMins)> _chartData = [];
    private HistoryRange _range = HistoryRange.Week;

    private record DrillRow(string ProcessName, string WindowTitle, string Start, string End, string Duration);

    public DashboardWindow(DatabaseService db, Func<StatusSnapshot?>? read = null)
    {
        InitializeComponent();
        _db = db;
        _read = read ?? (() => null);
        DataContext = _status;
        _status.Refresh(_read(), DateTimeOffset.UtcNow);
        BuildChart();
        _timer.Tick += OnRefresh;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _timer.Start(); BuildChart(); }
            else _timer.Stop();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        _status.Refresh(_read(), now);
        if (now - _lastHistoryRefresh >= TimeSpan.FromSeconds(30)) BuildChart();
    }

    private void OnHistoryRange(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string value } ||
            !Enum.TryParse<HistoryRange>(value, out var range)) return;
        _range = range;
        BuildChart();
    }

    private void BuildChart()
    {
        _lastHistoryRefresh = DateTimeOffset.UtcNow;
        var plan = HistoryRangePlan.For(_range, DateOnly.FromDateTime(DateTime.Today));
        _configured = _db.GetRules().Where(r => r.Enabled).Select(r => ProcessInstance.NormalizeKey(r.ProcessName)).ToHashSet();
        _chartData = _db.LoadUsageHistory(plan.Start, plan.End, plan.Monthly)
            .Where(r => _configured.Contains(ProcessInstance.NormalizeKey(r.ProcessName))).ToArray();
        HistoryHeader.Text = _range switch { HistoryRange.Week => "Last 7 Days", HistoryRange.Month => "Last 30 Days", _ => "Last 12 Months" };
        EmptyHistoryDetail.Text = _range switch
        {
            HistoryRange.Week => "ScreenTime will show your last seven days here as usage accumulates.",
            HistoryRange.Month => "ScreenTime will show your last thirty days here as usage accumulates.",
            _ => "ScreenTime will show your last twelve months here as usage accumulates."
        };
        BarChart.Height = _range == HistoryRange.Month ? 610 : _range == HistoryRange.Year ? 330 : 230;
        EmptyHistory.Visibility = _chartData.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BarChart.Visibility = _chartData.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DrilldownHeader.Visibility = _chartData.Count == 0 || plan.Monthly ? Visibility.Collapsed : Visibility.Visible;
        DrilldownGrid.Visibility = _chartData.Count == 0 || plan.Monthly ? Visibility.Collapsed : Visibility.Visible;
        DrilldownHeader.Text = "Click a bar to see that day's breakdown";
        DrilldownGrid.ItemsSource = null;

        var model = new PlotModel
        {
            Background          = OxyColors.Transparent,
            TextColor           = OxyColor.FromRgb(240, 240, 240),
            PlotAreaBorderColor = OxyColor.FromRgb(60, 60, 80)
        };

        model.Legends.Add(new Legend
        {
            LegendTextColor       = OxyColor.FromRgb(210, 210, 225),
            LegendBackground      = OxyColors.Transparent,
            LegendBorderThickness = 0
        });

        // Y axis — dates as categories (BarSeries in OxyPlot 2.x requires CategoryAxis on Left)
        var dates = plan.Buckets;
        var yAxis = new CategoryAxis
        {
            Position           = AxisPosition.Left,
            ItemsSource        = dates.Select(d => d.ToString(plan.Monthly ? "MMM yy" : "MMM d")).ToList(),
            TextColor          = OxyColor.FromRgb(160, 160, 184),
            TicklineColor      = OxyColors.Transparent,
            MajorGridlineStyle = LineStyle.None,
            GapWidth           = 0.3
        };
        model.Axes.Add(yAxis);

        // X axis — minutes
        var xAxis = new LinearAxis
        {
            Position           = AxisPosition.Bottom,
            Title              = "Minutes",
            TitleColor         = OxyColor.FromRgb(160, 160, 184),
            TextColor          = OxyColor.FromRgb(160, 160, 184),
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(60, 60, 80),
            TicklineColor      = OxyColors.Transparent,
            Minimum            = 0
        };
        model.Axes.Add(xAxis);

        // One ColumnSeries per app (vertical bars, categories on X)
        var apps = _chartData.Select(r => r.ProcessName).Distinct().OrderBy(x => x).ToList();
        var palette = new[]
        {
            OxyColor.FromRgb(159, 200, 244),
            OxyColor.FromRgb(86,  156, 214),
            OxyColor.FromRgb(78,  201, 176),
            OxyColor.FromRgb(220, 220, 100),
            OxyColor.FromRgb(180, 100, 220)
        };

        int ruledIdx = 0;
        for (int i = 0; i < apps.Count; i++)
        {
            var app       = apps[i];
            var color     = palette[ruledIdx++ % palette.Length];

            var series = new BarSeries
            {
                Title           = app,
                FillColor       = color,
                StrokeThickness = 0
            };

            foreach (var date in dates)
            {
                var val = _chartData
                    .FirstOrDefault(r => r.Bucket == date && r.ProcessName == app)
                    .UsageMins;
                series.Items.Add(new BarItem(val));
            }

            model.Series.Add(series);
        }

        // Click to drilldown — map click Y position to date index via category axis
        model.MouseDown += (s, e) =>
        {
            if (plan.Monthly || e.ChangedButton != OxyMouseButton.Left) return;
            var catAxis = model.Axes.OfType<CategoryAxis>().FirstOrDefault();
            if (catAxis == null) return;
            var catIdx = (int)Math.Round(catAxis.InverseTransform(e.Position.Y));
            if (catIdx >= 0 && catIdx < dates.Count)
                Dispatcher.Invoke(() => LoadDrilldown(dates[catIdx]));
        };

        BarChart.Model = model;
    }

    private void LoadDrilldown(DateOnly date)
    {
        DrilldownHeader.Text = $"{date:dddd, MMMM d, yyyy}";
        var sessions = _db.LoadSessionsForDay(date);
        DrilldownGrid.ItemsSource = sessions
            .Where(s => _configured.Contains(ProcessInstance.NormalizeKey(s.ProcessName)) && s.IsPassive == 0)
            .Select(s => new DrillRow(s.ProcessName, s.WindowTitle, SessionTime(s.StartTime), SessionTime(s.EndTime), s.DurationDisplay))
            .ToList();
    }
    private static string SessionTime(string value) => DateTimeOffset.TryParse(value, out var instant) ? DisplayTime.Clock(instant) : "Unknown";
}
