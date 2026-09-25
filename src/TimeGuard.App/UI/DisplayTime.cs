using System.Globalization;

namespace TimeGuard.UI;

/// <summary>Display-only conversions; policy instants and normalized schedules stay unchanged.</summary>
internal static class DisplayTime
{
    public static string Clock(DateTimeOffset instant) => instant.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
    public static string Availability(DateTimeOffset instant, DateTimeOffset now)
    {
        var local = instant.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? Clock(instant) :
            local.ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.InvariantCulture);
    }
    public static string Duration(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        return seconds is > 0 and < 60 ? "<1 min" : minutes < 60 ? $"{minutes} min" :
            minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr {minutes % 60} min";
    }
    public static string Until(DateTimeOffset instant, DateTimeOffset now) =>
        Duration(Math.Max(0, (long)Math.Ceiling((instant - now).TotalMinutes)) * 60);
}
