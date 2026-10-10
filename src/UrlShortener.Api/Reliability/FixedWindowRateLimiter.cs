using System.Collections.Concurrent;

namespace UrlShortener.Api.Reliability;

/// <summary>
/// Per-client fixed-window rate limiter. In-process only: with several instances each keeps its own
/// counters (production path: a shared store such as Redis).
/// </summary>
public sealed class FixedWindowRateLimiter
{
    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
    }

    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public FixedWindowRateLimiter(int limit, TimeSpan window, TimeProvider time)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _limit = limit;
        _window = window;
        _time = time;
    }

    /// <summary>Tries to consume one permit. When denied, <paramref name="retryAfter"/> says how long to wait.</summary>
    public bool TryAcquire(string clientId, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        if (_windows.Count > 10_000) Purge(now);

        var w = _windows.GetOrAdd(clientId, _ => new Window { Start = now });
        lock (w)
        {
            if (now - w.Start >= _window)
            {
                w.Start = now;
                w.Count = 0;
            }
            if (w.Count >= _limit)
            {
                retryAfter = w.Start + _window - now;
                return false;
            }
            w.Count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var (key, w) in _windows)
        {
            if (now - w.Start >= _window) _windows.TryRemove(key, out _);
        }
    }
}
