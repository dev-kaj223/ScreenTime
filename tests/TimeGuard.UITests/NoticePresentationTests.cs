using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
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
                var branding = new ResourceDictionary { Source = new Uri("/ScreenTime;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
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
    [InlineData(NotificationKind.QuotaTenMinutes, 540, "TIME REMAINING", "{0} has about 9 minutes of daily time remaining.")]
    [InlineData(NotificationKind.QuotaFiveMinutes, 300, "5 MINUTES LEFT", "{0} has about 5 minutes of daily time remaining.")]
    [InlineData(NotificationKind.GraceStarted, 1020, "FINISH YOUR SESSION", "{0} has reached its daily limit. Finish your current session. This session will end in 17 minutes at {1}. New sessions are not allowed.")]
    [InlineData(NotificationKind.GraceFiveMinutes, 300, "FINAL WARNING", "{0} has 5 minutes left in the current session. This session will end at {1}. New sessions are not allowed.")]
    [InlineData(NotificationKind.GraceFinalMinute, 60, "FINAL MINUTE", "{0} has less than a minute left in the current session. This session will end at {1}.")]
    [InlineData(NotificationKind.Blocked, 0, "TIME EXPIRED", "{0} has reached its daily limit and cannot be opened again today.")]
    public void Urgency_UsesApprovedSentenceCopy_DynamicAppName_AndLocalTime(NotificationKind kind, int seconds, string heading, string template)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "en-GB" })
            foreach (var name in new[] { "Apex Legends", "Example Editor" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var request = Request(kind, new DateTimeOffset(2026, 9, 24, 20, 0, 23, TimeSpan.Zero), seconds) with
                    { DisplayName = name, NextAvailabilityUtc = DateTimeOffset.UtcNow.AddDays(1) };
                Assert.Equal(heading, NoticePresentation.Style(request).Heading);
                var body = NoticePresentation.Body(request, request.CreatedAtUtc);
                Assert.Equal(string.Format(template, name, request.GraceDeadlineUtc!.Value.ToLocalTime().ToString("T")), body);
                Assert.DoesNotContain(name + ":", body);
                Assert.DoesNotContain("game", body);
                Assert.NotEqual(body.ToUpperInvariant(), body);
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DowntimeCopy_UsesAppName_AndOnlyKnownFriendlyLocalAvailability(bool knownAvailability)
    {
        var request = Request(NotificationKind.Blocked, DateTimeOffset.UtcNow) with
        {
            DisplayName = "Example Editor", Reason = PolicyReason.Downtime | PolicyReason.DailyQuotaExhausted,
            NextAvailabilityUtc = knownAvailability ? new DateTimeOffset(2026, 9, 25, 17, 30, 0, TimeSpan.Zero) : null
        };
        Assert.Equal("APP BLOCKED", NoticePresentation.Style(request).Heading);
        Assert.Equal("Example Editor is unavailable during downtime." +
            (knownAvailability ? $" Next availability is {request.NextAvailabilityUtc!.Value.ToLocalTime():f}." : ""),
            NoticePresentation.Body(request, request.CreatedAtUtc));
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
