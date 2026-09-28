using System.Globalization;

namespace Obkhodiki.Core.Vpn;

/// <param name="Key">Identifies the event, so the same warning is announced once (not on every refresh).</param>
/// <param name="Severe">The subscription no longer works (expired or out of traffic), not just close to it.</param>
public sealed record SubscriptionAlert(string Key, string Text, bool Severe);

/// <summary>Warnings about the subscription running out, from the panel's usage and expiry data.</summary>
public static class SubscriptionAlerts
{
    public const double TrafficWarnShare = 0.9;
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(3);

    /// <returns>The most important warning, or null when all is well.</returns>
    public static SubscriptionAlert? Check(long? used, long? total, DateTimeOffset? expire, DateTimeOffset now)
    {
        if (expire is { } exp)
        {
            var day = exp.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            if (exp <= now) return new($"expired:{day}", $"Подписка закончилась {day}. Продлите её у поставщика.", true);
        }
        if (used is { } u && total is > 0 and var t)
        {
            if (u >= t) return new($"traffic-out:{t}", $"Трафик подписки израсходован ({Size(u)} из {Size(t)}).", true);
        }
        if (expire is { } soon && soon - now <= ExpiryWarning)
        {
            var left = soon - now;
            var when = left < TimeSpan.FromDays(1) ? "меньше чем через сутки" : $"через {Days((int)Math.Ceiling(left.TotalDays))}";
            var day = soon.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            return new($"expire-soon:{day}", $"Подписка заканчивается {when} ({day}).", false);
        }
        if (used is { } u2 && total is > 0 and var t2 && u2 >= t2 * TrafficWarnShare)
        {
            return new($"traffic-90:{t2}", $"Израсходовано {u2 * 100 / t2}% трафика подписки ({Size(u2)} из {Size(t2)}).", false);
        }
        return null;
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.GetCultureInfo("ru-RU")) + " ГБ"
            : (bytes / (double)(1L << 20)).ToString("F0", CultureInfo.GetCultureInfo("ru-RU")) + " МБ";

    private static string Days(int n) =>
        n % 10 == 1 && n % 100 != 11 ? $"{n} день"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} дня"
        : $"{n} дней";
}
