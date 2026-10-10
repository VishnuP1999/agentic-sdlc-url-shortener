using System.Collections.Concurrent;
using System.Globalization;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Analytics;

public sealed record LinkAnalytics(long TotalClicks, DateTimeOffset? LastAccessedAt, IReadOnlyDictionary<string, long> DailyClicks);

/// <summary>Aggregated click counters per short code (total, last access, per-day).</summary>
public sealed class AnalyticsStore
{
    private sealed class Counters
    {
        public long Total;
        public DateTimeOffset? Last;
        public readonly SortedDictionary<string, long> Daily = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, Counters> _byCode = new(StringComparer.Ordinal);

    public void Record(ClickEvent click)
    {
        var c = _byCode.GetOrAdd(click.Code, _ => new Counters());
        var day = click.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (c)
        {
            c.Total++;
            if (c.Last is null || click.At > c.Last) c.Last = click.At;
            c.Daily[day] = c.Daily.GetValueOrDefault(day) + 1;
        }
    }

    public LinkAnalytics Get(string code)
    {
        if (!_byCode.TryGetValue(code, out var c)) return new LinkAnalytics(0, null, new Dictionary<string, long>());
        lock (c)
        {
            return new LinkAnalytics(c.Total, c.Last, new Dictionary<string, long>(c.Daily));
        }
    }
}
