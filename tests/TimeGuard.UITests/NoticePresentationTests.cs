using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeGuard.Models;
using TimeGuard.UI;
using Xunit;

namespace TimeGuard.UITests;

public class NoticePresentationTests
{
    private static NotificationRequest Request(NotificationKind kind, DateTimeOffset now, int seconds = 60) =>
        new("test", "helper", "Helper", kind, now, now.AddSeconds(seconds), TimeSpan.FromSeconds(seconds), "original", now.AddSeconds(seconds));

    [Fact]
    public void Countdown_UsesOriginalDeadline_BoundsLateDelivery_AndCannotGrowOnClockRollback()
    {
        var clock = new DisplayClock();
        var request = Request(NotificationKind.GraceFinalMinute, clock.Now);
        clock.Advance(17);
        var lifetime = new NoticeLifetime(request, clock);
        Assert.Equal(TimeSpan.FromSeconds(43), lifetime.Remaining);
        clock.Advance(12);
        Assert.Equal(TimeSpan.FromSeconds(31), lifetime.Remaining);
        clock.Now = clock.Now.AddHours(-1);
        clock.Advance(30);
        Assert.Equal(TimeSpan.FromSeconds(1), lifetime.Remaining);
        clock.Advance(1);
        Assert.Equal(TimeSpan.Zero, lifetime.Remaining);
        Assert.Equal(request.CreatedAtUtc.AddSeconds(60), request.GraceDeadlineUtc);
    }

    [Fact]
    public void Countdown_ForwardJump_Overdue_MissingDeadline_AndMalformedLongDuration_FailClosedVisually()
    {
        var clock = new DisplayClock();
        var request = Request(NotificationKind.GraceFinalMinute, clock.Now);
        var lifetime = new NoticeLifetime(request, clock);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(TimeSpan.Zero, lifetime.Remaining);
        Assert.Equal(TimeSpan.Zero, new NoticeLifetime(request, clock).Remaining);
        Assert.Equal(TimeSpan.Zero, new NoticeLifetime(request with { GraceDeadlineUtc = null }, clock).Remaining);
        Assert.Equal(TimeSpan.FromSeconds(60), new NoticeLifetime(Request(NotificationKind.GraceFinalMinute, clock.Now, 120), clock).Remaining);
        Assert.Equal(TimeSpan.FromSeconds(6), new NoticeLifetime(Request(NotificationKind.QuotaTenMinutes, clock.Now), clock).Remaining);
    }

    [Fact]
    public void CenteredWrappedContent_SizesNaturally_SharedLogoCanBeReplaced()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var shortNotice = new PassiveNoticeWindow(Request(NotificationKind.QuotaTenMinutes, now), () => true, null);
                var longNotice = new PassiveNoticeWindow(Request(NotificationKind.GraceStarted, now, 1200) with
                    { DisplayName = new string('L', 200) }, () => true, null);
                var branding = new ResourceDictionary { Source = new Uri("/TimeGuard;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
                shortNotice.Resources.MergedDictionaries.Add(branding);
                foreach (var notice in new[] { shortNotice, longNotice })
                {
                    var border = (Border)notice.Content;
                    border.Measure(new Size(notice.Width, notice.MaxHeight));
                    Assert.True(double.IsNaN(notice.Height));
                    Assert.InRange(border.DesiredSize.Height, 128, 360);
                    var stack = (StackPanel)((Border)notice.Content).Child;
                    foreach (var text in stack.Children.OfType<TextBlock>()) Assert.Equal(TextAlignment.Center, text.TextAlignment);
                    Assert.Equal(TextWrapping.Wrap, ((TextBlock)stack.Children[^1]).TextWrapping);
                }
                Assert.True(((Border)longNotice.Content).DesiredSize.Height > ((Border)shortNotice.Content).DesiredSize.Height);
                var content = (StackPanel)((Border)shortNotice.Content).Child;
                var logo = (Image)((StackPanel)content.Children[0]).Children[0];
                Assert.Same(branding["ScreenTimeBrandImage"], logo.Source);
                var replacement = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
                shortNotice.Resources["ScreenTimeBrandImage"] = replacement;
                Assert.Same(replacement, logo.Source);
                shortNotice.Close(); longNotice.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(NotificationKind.QuotaTenMinutes, "TIME REMAINING")]
    [InlineData(NotificationKind.QuotaFiveMinutes, "5 MINUTES LEFT")]
    [InlineData(NotificationKind.GraceStarted, "FINISH YOUR SESSION")]
    [InlineData(NotificationKind.GraceFiveMinutes, "FINAL WARNING")]
    [InlineData(NotificationKind.GraceFinalMinute, "FINAL MINUTE")]
    [InlineData(NotificationKind.Blocked, "TIME EXPIRED")]
    public void Urgency_UsesTextAsWellAsColor_AndNormalCaseBody(NotificationKind kind, string heading)
    {
        var request = Request(kind, DateTimeOffset.UtcNow);
        Assert.Equal(heading, NoticePresentation.Style(request).Heading);
        var body = NoticePresentation.Body(request, request.CreatedAtUtc);
        Assert.NotEqual(body.ToUpperInvariant(), body);
        Assert.Contains("Helper", body);
        Assert.Equal("APP BLOCKED", NoticePresentation.Style(request with { Kind = NotificationKind.Blocked, Reason = PolicyReason.Downtime }).Heading);
    }

    private sealed class DisplayClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(int seconds) { Now = Now.AddSeconds(seconds); _ticks += TimeSpan.FromSeconds(seconds).Ticks; }
    }
}
