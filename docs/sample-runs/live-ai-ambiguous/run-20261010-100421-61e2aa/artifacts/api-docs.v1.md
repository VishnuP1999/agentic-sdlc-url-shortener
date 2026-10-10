# URL Shortener: Fast Redirects and Safer Links

> This is a documentation proposal written from the design and task plan. Nothing here has been built, run or tested. Items marked **(unverified)** need confirmation against the real code, build and tests.

## Overview

The service is an ASP.NET Core (.NET 8) minimal-API URL shortener. This change has two goals.

- **Faster redirects.** The target is p95 < 50 ms at a sustained 500 req/s. Click analytics are recorded asynchronously. **(unverified: needs a real load test, and the result depends on the environment)**
- **Safer links.**
  - Unsafe targets are rejected at creation: non-http(s) schemes, embedded credentials, loopback, private and link-local addresses, and blocklisted domains.
  - Abusive clients are rate limited per IP.
  - Links can expire.

Requirements covered: FR-1 (create and metadata), FR-2 (redirect), FR-3 (analytics), FR-4 (target safety), FR-5 (expiry), FR-6 (rate limiting).

## Architecture

Components (from the design):

- **UrlValidator**: checks scheme allowlist, credentials, IP and private ranges, blocklist with subdomain match, DNS resolution, and input length limits.
- **HostBlocklist**: config-backed set of malicious domains, matched by suffix.
- **SsrfGuard / HostResolver**: resolves DNS and rejects private, loopback, link-local, ULA and IPv4-mapped results. It applies a timeout and fails closed.
- **ShortenerService**: validates expiry, resolves links (including an Expired status) and reports expiry.
- **IUrlRepository / InMemoryUrlRepository**: lookups via `ConcurrentDictionary`. `ShortLink` stores `ExpiresAt`.
- **ClickRecorder / ClickProcessor**: bounded `Channel` with DropWrite on full, a background consumer, and swallowed sink errors.
- **AnalyticsStore**: eventually consistent atomic counters.
- **Rate limiting**: per-IP, with separate create and redirect policies, `Retry-After`, and stale-window eviction.
- **ShortenerOptions**: limits, queue capacity, blocklist, expiry, trusted proxies.
- **ShortLinkEndpoints, Contracts, Program.cs, AppHost**: route wiring, status mapping, DI and config binding.

Data flows:

- **Create:** client → rate limiter → endpoint → UrlValidator (scheme, credentials, blocklist, DNS/private check) → expiry check → repository → 201.
- **Redirect:** client → rate limiter → repository lookup → expiry/active check → 302, plus a non-blocking `TryWrite` of a ClickEvent.
- **Analytics:** channel → ClickProcessor background consumer → AnalyticsStore → analytics endpoint.
- **Rejected (404/410/429):** no ClickEvent is enqueued.

Data model (no schema change):

- `ShortLink`: Code, TargetUrl, CreatedAt (UTC), ExpiresAt (UTC, nullable), Active.
- `ClickEvent`: Code, Timestamp (queued, in-memory).
- `LinkAnalytics`: Code, TotalClicks.

## Setup

```
dotnet build AgenticSdlc.sln
dotnet run --project src/UrlShortener.Api
dotnet run --project tests/UrlShortener.Tests
```

Configuration (`ShortenerOptions`). The exact configuration key names and the `AnalyticsQueueCapacity`, `DnsTimeout` and default expiry values are not fixed by the design. **(unverified: confirm against the code)**

| Option | Default | Purpose |
|---|---|---|
| `CreateLimitPerMinute` | 30 | Creation limit per client IP |
| `RedirectLimitPerMinute` | 600 | Redirect limit per client IP |
| `AnalyticsQueueCapacity` | configurable, no default in the design | Bound on the click queue |
| `Blocklist[]` | empty or maintained by us | Malicious domains, suffix-matched |
| `DnsTimeout` | configurable, no default in the design | DNS lookup timeout at creation |
| `TrustedProxies[]` | none | Proxies whose forwarded headers are trusted |
| Default expiry | none unless configured | Optional default link expiry |

## API reference

| Method | Path | Summary | Statuses |
|---|---|---|---|
| POST | `/api/urls` | Create a short link with a validated target and optional UTC expiry. Rate limited. | 201, 400, 429 |
| GET | `/r/{code}` | 302 redirect to the target. The click is enqueued without blocking. Returns 410 if expired. Rate limited. | 302, 404, 410, 429 |
| GET | `/{code}` | Legacy redirect, same behavior as `/r/{code}` | 302, 404, 410, 429 |
| GET | `/api/urls/{code}` | Link metadata: target, created, expiry, expired flag | 200, 404 |
| GET | `/api/urls/{code}/analytics` | Eventually consistent total click count | 200, 404 |
| GET | `/api/urls/{code}/stats` | Existing stats route, kept working | 200, 404 |
| DELETE | `/api/urls/{code}` | Existing deactivation, kept working | 204, 404 |
| GET | `/health` | Health check | 200 |

### Examples

The JSON field names below are illustrative. The design says only that the response carries the code, short URL, target, creation time, expiry and an expired flag. **(unverified: confirm against Contracts.cs)**

Create:
```
POST /api/urls
{ "url": "https://example.com/page", "expiresAt": "2027-01-01T00:00:00Z" }

201 Created
{ "code": "abc123", "shortUrl": "<base>/r/abc123", "url": "https://example.com/page" }
```

Create errors:

- A missing or malformed URL returns 400.
- An expiry in the past returns 400.
- An unsafe target returns 400.
- Over the limit, it returns 429 with a `Retry-After` header.

Redirect:
```
GET /r/abc123
302 Found
Location: https://example.com/page
```
- An unknown code returns 404.
- An expired link returns 410.
- Over the limit, it returns 429 with `Retry-After`.

Metadata:
```
GET /api/urls/abc123
200 OK
{ "code": "abc123", "url": "https://example.com/page", "createdAt": "...", "expiresAt": "...", "expired": false }
```
An expired link still returns 200 and reports it as expired.

Analytics:
```
GET /api/urls/abc123/analytics
200 OK
{ "code": "abc123", "totalClicks": 42 }
```
The count is eventually consistent.

## Security controls

- **url-scheme-allowlist:** only http and https are accepted. `ftp:`, `javascript:` and `file:` return 400. URLs with embedded credentials return 400.
- **ssrf-block:**
  - Loopback, private and link-local targets return 400: localhost, 127.0.0.1, ::1, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 and 169.254.0.0/16.
  - IPv6 ULA and IPv4-mapped addresses are also checked.
  - Hostnames are resolved at creation, and any private result is rejected.
  - A DNS timeout fails closed.
- **Blocklist:** a host, or a subdomain of a listed host, on the static configurable blocklist returns 400 and no link is created.
- **input-limits:** length limits apply to the URL input.
- **rate-limit:** per-client-IP limits with separate create and redirect policies. 429 responses carry `Retry-After`. One client's throttling does not affect others. Limits are configurable without a code change.
- **Expiry:** links expire. Expired links return 410, and 302 (not 301) is used so expiry is honored by clients.

## Assumptions

- The blocklist is static and configurable. No external threat-intelligence service is called.
- Clients are identified by remote IP. Proxy headers are trusted only if configured.
- Default limits are 30 creations/min and 600 redirects/min per IP. The window is fixed or sliding, and configurable.
- "Faster" applies to redirects only.
- Expiry is optional and given as a UTC timestamp. There is no default expiry unless configured.
- Owner deactivation and extending expiry are out of scope for the new work. The existing `DELETE` route is kept.
- Storage is in-memory, with a cache-friendly lookup.

## Risks and trade-offs

- **DNS rebinding:** resolution at creation can differ from later resolution. Redirects are performed client-side, so server-side SSRF exposure is limited, but the gap exists.
- **Slow DNS:** lookups at creation can be slow, so a timeout applies and the check fails closed.
- **Undercounted analytics:** the bounded queue drops clicks under overload. This is an accepted trade-off.
- **Lost state:** in-memory state is lost on restart and is not shared across instances. Rate limits are per-instance.
- **Proxies:** without trusted-proxy configuration, clients behind a proxy look identical.
- **Fixed-window bursts:** a fixed window allows bursts at window boundaries, while a sliding window costs more.
- **Clock bugs:** expiry uses UTC and an injectable clock to reduce them.
- **Memory growth:** many distinct IPs can grow limiter memory, so stale windows are evicted.
- **Latency:** the p95 target is environment-dependent and must be verified by a load test. **(unverified)**

## Limitations

- Analytics are eventually consistent and can undercount under overload.
- Data is not persisted, and the service is not horizontally consistent.
- Blocklist coverage is only as good as the configured list.
- Shutdown flush of queued clicks is a noted risk. **(unverified)**
- No owner deactivation or expiry extension is added in this change.

## Review notes addressed

No review notes were supplied in the input for this documentation task, so there is nothing to quote or map. If review notes are provided later, each will be quoted verbatim here with an explanation of how it is addressed.
