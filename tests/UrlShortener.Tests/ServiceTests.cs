using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Reliability;
using UrlShortener.Api.Services;
using UrlShortener.Api.Validation;
using Xunit;

namespace UrlShortener.Tests;

public class CodecTests
{
    [Fact]
    public void Base62_round_trips()
    {
        foreach (var value in new long[] { 0, 1, 61, 62, 3843, 123456789, long.MaxValue / 2 })
            Assert.Equal(value, Base62.Decode(Base62.Encode(value)));
    }

    [Fact]
    public void Base62_rejects_negative_and_invalid_input()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base62.Encode(-1));
        Assert.Throws<FormatException>(() => Base62.Decode("ab$"));
    }

    [Fact]
    public void Generated_codes_are_unique_and_short()
    {
        var seen = new HashSet<string>();
        for (long id = 1; id <= 200_000; id++)
        {
            var code = CodeGenerator.FromId(id);
            Assert.True(code.Length <= 7, $"code '{code}' too long");
            Assert.True(seen.Add(code), $"duplicate code '{code}' for id {id}");
        }
    }

    [Fact]
    public void Consecutive_ids_do_not_produce_consecutive_codes()
    {
        Assert.NotEqual(Base62.Decode(CodeGenerator.FromId(1)) + 1, Base62.Decode(CodeGenerator.FromId(2)));
    }
}

public class ValidatorTests
{
    private readonly UrlValidator _validator = new(2048);

    [Fact]
    public void Accepts_normal_http_and_https_urls()
    {
        Assert.True(_validator.Validate("https://example.com/path?q=1").IsValid);
        Assert.True(_validator.Validate("http://example.org").IsValid);
    }

    [Fact]
    public void Rejects_missing_relative_and_non_http_urls()
    {
        Assert.False(_validator.Validate(null).IsValid);
        Assert.False(_validator.Validate("   ").IsValid);
        Assert.False(_validator.Validate("/relative/path").IsValid);
        Assert.False(_validator.Validate("ftp://example.com/file").IsValid);
        Assert.False(_validator.Validate("javascript:alert(1)").IsValid);
        Assert.False(_validator.Validate("file:///etc/passwd").IsValid);
    }

    [Fact]
    public void Rejects_credentials_in_url()
    {
        Assert.False(_validator.Validate("https://user:pass@example.com").IsValid);
        Assert.False(_validator.Validate("https://google.com@evil.example").IsValid);
    }

    [Fact]
    public void Rejects_loopback_private_and_metadata_hosts()
    {
        foreach (var url in new[]
                 {
                     "http://localhost/admin", "http://127.0.0.1", "http://10.1.2.3", "http://172.16.0.1",
                     "http://172.31.255.255", "http://192.168.1.1", "http://169.254.169.254/latest/meta-data",
                     "http://[::1]/", "http://[fd00::1]/", "http://0.0.0.0", "http://2130706433/", "http://service.internal/"
                 })
            Assert.False(_validator.Validate(url).IsValid, $"{url} should be blocked");
    }

    [Fact]
    public void Allows_public_addresses_next_to_private_ranges()
    {
        Assert.True(_validator.Validate("http://172.32.0.1").IsValid);
        Assert.True(_validator.Validate("http://8.8.8.8").IsValid);
    }

    [Fact]
    public void Rejects_overlong_urls()
    {
        Assert.False(new UrlValidator(30).Validate("https://example.com/" + new string('a', 40)).IsValid);
    }

    [Fact]
    public void Alias_rules()
    {
        Assert.Null(AliasValidator.Validate("my-link_1"));
        Assert.NotNull(AliasValidator.Validate("ab"));
        Assert.NotNull(AliasValidator.Validate("has space"));
        Assert.NotNull(AliasValidator.Validate("api"));
        Assert.NotNull(AliasValidator.Validate(new string('a', 33)));
    }
}

public class RateLimiterTests
{
    [Fact]
    public void Blocks_after_limit_and_resets_after_window()
    {
        var time = new FakeTimeProvider();
        var limiter = new FixedWindowRateLimiter(3, TimeSpan.FromSeconds(60), time);

        for (var i = 0; i < 3; i++) Assert.True(limiter.TryAcquire("a", out _));
        Assert.False(limiter.TryAcquire("a", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero && retryAfter <= TimeSpan.FromSeconds(60));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire("a", out _));
    }

    [Fact]
    public void Clients_are_limited_independently()
    {
        var limiter = new FixedWindowRateLimiter(1, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        Assert.True(limiter.TryAcquire("a", out _));
        Assert.False(limiter.TryAcquire("a", out _));
        Assert.True(limiter.TryAcquire("b", out _));
    }
}

public class ServiceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ShortenerService _service;
    private readonly AnalyticsStore _analytics = new();
    private readonly ClickRecorder _clicks = new(100);

    public ServiceTests()
    {
        var options = new ShortenerOptions();
        _service = new ShortenerService(new InMemoryUrlRepository(), new UrlValidator(options.MaxUrlLength), _clicks,
            _analytics, options, _time);
    }

    [Fact]
    public void Create_returns_unique_codes_for_different_urls()
    {
        var a = _service.Create(new CreateLinkRequest("https://example.com/a"), "c1");
        var b = _service.Create(new CreateLinkRequest("https://example.com/b"), "c1");
        Assert.Equal(CreateStatus.Created, a.Status);
        Assert.NotEqual(a.Link!.Code, b.Link!.Code);
    }

    [Fact]
    public void Create_is_idempotent_per_client_and_url()
    {
        var first = _service.Create(new CreateLinkRequest("https://example.com/a"), "c1");
        var again = _service.Create(new CreateLinkRequest("https://example.com/a"), "c1");
        var other = _service.Create(new CreateLinkRequest("https://example.com/a"), "c2");

        Assert.Equal(CreateStatus.Existing, again.Status);
        Assert.Equal(first.Link!.Code, again.Link!.Code);
        Assert.NotEqual(first.Link.Code, other.Link!.Code);
    }

    [Fact]
    public void Create_validates_input()
    {
        Assert.Equal(CreateStatus.Invalid, _service.Create(new CreateLinkRequest("ftp://x.com"), "c").Status);
        Assert.Equal(CreateStatus.Invalid, _service.Create(new CreateLinkRequest("https://x.com", "no"), "c").Status);
        Assert.Equal(CreateStatus.Invalid, _service.Create(new CreateLinkRequest("https://x.com", null, 0), "c").Status);
        Assert.Equal(CreateStatus.Invalid, _service.Create(new CreateLinkRequest("https://x.com", null, int.MaxValue), "c").Status);
    }

    [Fact]
    public void Custom_alias_is_used_and_conflicts_are_reported()
    {
        var first = _service.Create(new CreateLinkRequest("https://example.com/a", "my-alias"), "c1");
        var second = _service.Create(new CreateLinkRequest("https://example.com/b", "my-alias"), "c2");
        Assert.Equal("my-alias", first.Link!.Code);
        Assert.Equal(CreateStatus.AliasTaken, second.Status);
    }

    [Fact]
    public void Resolve_redirects_then_expires()
    {
        var created = _service.Create(new CreateLinkRequest("https://example.com/a", null, 10), "c1").Link!;

        var ok = _service.Resolve(created.Code);
        Assert.Equal(ResolveStatus.Redirect, ok.Status);
        Assert.Equal("https://example.com/a", ok.TargetUrl);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ResolveStatus.Gone, _service.Resolve(created.Code).Status);
        Assert.Equal(ResolveStatus.NotFound, _service.Resolve("missing").Status);
    }

    [Fact]
    public void Expired_link_can_be_recreated_with_a_new_code()
    {
        var first = _service.Create(new CreateLinkRequest("https://example.com/a", null, 1), "c1").Link!;
        _time.Advance(TimeSpan.FromMinutes(2));
        var second = _service.Create(new CreateLinkRequest("https://example.com/a"), "c1");
        Assert.Equal(CreateStatus.Created, second.Status);
        Assert.NotEqual(first.Code, second.Link!.Code);
    }

    [Fact]
    public void Only_the_owner_can_deactivate()
    {
        var code = _service.Create(new CreateLinkRequest("https://example.com/a"), "owner").Link!.Code;

        Assert.Equal(DeactivateStatus.Forbidden, _service.Deactivate(code, "intruder"));
        Assert.Equal(ResolveStatus.Redirect, _service.Resolve(code).Status);

        Assert.Equal(DeactivateStatus.Done, _service.Deactivate(code, "owner"));
        Assert.Equal(ResolveStatus.Gone, _service.Resolve(code).Status);
        Assert.Equal(DeactivateStatus.NotFound, _service.Deactivate("missing", "owner"));
    }

    [Fact]
    public void Stats_are_null_for_unknown_codes_and_zero_for_new_links()
    {
        Assert.Null(_service.GetStats("missing"));
        var code = _service.Create(new CreateLinkRequest("https://example.com/a"), "c").Link!.Code;
        Assert.Equal(0L, _service.GetStats(code)!.TotalClicks);
    }

    [Fact]
    public void Analytics_store_aggregates_per_day()
    {
        var store = new AnalyticsStore();
        var day1 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        store.Record(new ClickEvent("x", day1));
        store.Record(new ClickEvent("x", day1.AddHours(1)));
        store.Record(new ClickEvent("x", day1.AddDays(1)));

        var a = store.Get("x");
        Assert.Equal(3L, a.TotalClicks);
        Assert.Equal(2L, a.DailyClicks["2026-03-01"]);
        Assert.Equal(1L, a.DailyClicks["2026-03-02"]);
        Assert.Equal(day1.AddDays(1), a.LastAccessedAt!.Value);
    }

    [Fact]
    public void Click_queue_drops_instead_of_blocking_when_full()
    {
        var recorder = new ClickRecorder(2);
        for (var i = 0; i < 5; i++) recorder.Enqueue(new ClickEvent("x", DateTimeOffset.UtcNow));
        Assert.Equal(3L, recorder.DroppedClicks);
    }
}
