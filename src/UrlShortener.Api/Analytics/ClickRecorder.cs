using System.Threading.Channels;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Hot-path entry for analytics: enqueues a click without blocking the redirect. When the queue is
/// full the click is dropped (and counted) instead of slowing redirects down.
/// </summary>
public sealed class ClickRecorder
{
    private readonly Channel<ClickEvent> _channel;
    private long _dropped;

    public ClickRecorder(int capacity)
    {
        _channel = Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // with Wait, TryWrite returns false when full, so drops can be counted
            SingleReader = true
        });
    }

    public long DroppedClicks => Interlocked.Read(ref _dropped);

    public void Enqueue(ClickEvent click)
    {
        if (!_channel.Writer.TryWrite(click)) Interlocked.Increment(ref _dropped);
    }

    internal ChannelReader<ClickEvent> Reader => _channel.Reader;
}

/// <summary>Background consumer that drains the click queue into the analytics store.</summary>
public sealed class ClickProcessor : BackgroundService
{
    private readonly ClickRecorder _recorder;
    private readonly AnalyticsStore _store;

    public ClickProcessor(ClickRecorder recorder, AnalyticsStore store)
    {
        _recorder = recorder;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var click in _recorder.Reader.ReadAllAsync(stoppingToken))
                _store.Record(click);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
