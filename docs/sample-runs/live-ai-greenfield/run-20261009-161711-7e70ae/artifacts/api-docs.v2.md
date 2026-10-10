# URL Shortener API Documentation

> Status: this is a proposal-stage document based on the requirement and design. Nothing here has been built, run or tested. Items marked **(unverified)** must be checked against the implementation and tests before release.

## Overview

An ASP.NET Core (.NET 8) minimal-API URL shortener. It supports:

- Creating short links with an optional custom alias and an optional absolute UTC expiry.
- Redirecting short links with HTTP 302.
- Per-link click analytics.
- Owner deactivation using an owner token.
- Per-client rate limiting of link creation.
- Rejection of unsafe or private-network target URLs.

Storage is in-memory only, so data is lost on restart. There are no user accounts.

## Architecture

| Component | Responsibility |
|---|---|
| ShortLinkEndpoints | Minimal API route mapping, bearer token parsing, problem-details mapping |
| ShortenerService | Create, resolve, deactivate, metadata and analytics orchestration |
| UrlValidator | Scheme allowlist, userinfo rejection, length limit, private/loopback/link-local/IPv6 unique-local host block |
| AliasValidator | 3-32 characters, `[A-Za-z0-9_-]` |
| CodeGenerator/Base62 | 7-character codes from `RandomNumberGenerator` |
| IUrlRepository + InMemoryUrlRepository | `ConcurrentDictionary`, case-sensitive keys, atomic add |
| AnalyticsStore + ClickRecorder | Thread-safe counters (`Interlocked`), per-UTC-day buckets |
| FixedWindowRateLimiter | Per-IP, 10 requests per 60 s, `Retry-After` |
| ShortenerOptions | Configurable limits, window, base URL |
| Owner token hashing | SHA-256 of 32 random bytes, constant-time compare |

Data flows:

- **Create:** client → rate limiter (IP) → validators → ShortenerService → CodeGenerator → repository (token hash stored) → 201 with the plaintext token, shown once.
- **Redirect:** client → ShortenerService.Resolve → repository → 404, 410 or 302. A click is recorded only on 302.
- **Analytics:** client → service → AnalyticsStore snapshot → JSON.
- **Deactivate:** client with bearer token → hash and constant-time compare → `IsActive=false`.

Data model:

- `ShortLink`: Code (case-sensitive key), TargetUrl, CreatedAt (UTC), ExpiresAt?, IsActive, OwnerTokenHash.
- Analytics per code: TotalClicks, LastClickAt?, per-UTC-day count map.
- Options: MaxUrlLength=2048, alias min 3 / max 32, CodeLength=7, RateLimit=10, WindowSeconds=60.
- No persisted schema change.

## Setup

```
dotnet build AgenticSdlc.sln
dotnet run --project src/UrlShortener.Api
dotnet run --project tests/UrlShortener.Tests
```

Configuration lives in `ShortenerOptions`: max URL length, alias bounds, code length, rate limit (default 10), window (default 60 s) and base URL. The exact configuration section name and key names are **(unverified)**.

## API reference

| Method | Path | Summary | Statuses |
|---|---|---|---|
| POST | `/api/urls` | Create a short link (url, optional alias, optional expiresAt) | 201, 400, 409, 429 |
| GET | `/r/{code}` | 302 redirect for an active, unexpired link; records a click | 302, 404, 410 |
| GET | `/api/urls/{code}` | Link metadata, never the owner token | 200, 404 |
| GET | `/api/urls/{code}/analytics` | totalClicks, lastClickAt, clicks per UTC day | 200, 404 |
| DELETE | `/api/urls/{code}` | Deactivate a link with the bearer owner token | 204, 401, 403, 404 |
| GET | `/health` | Liveness check | 200 |
| GET | `/{code}` | Legacy redirect, behaviour preserved | 302, 404, 410 |
| GET | `/api/urls/{code}/stats` | Legacy stats, behaviour preserved | 200, 404 |

### Create a link

Request:

```http
POST /api/urls
Content-Type: application/json

{ "url": "https://example.com/page", "alias": "my-link", "expiresAt": "2030-01-01T00:00:00Z" }
```

Response `201 Created`, with `Location: /api/urls/my-link`:

```json
{
  "code": "my-link",
  "shortUrl": "<base-url>/r/my-link",
  "expiresAt": "2030-01-01T00:00:00Z",
  "ownerToken": "<shown once; store it safely>"
}
```

- Without an alias, `code` is a generated 7-character `[A-Za-z0-9]` string.
- `expiresAt` is `null` when not supplied.
- `400` (problem details): missing or malformed url, invalid alias, `expiresAt` in the past, or an unsafe URL.
- `409`: alias already in use. Aliases are case-sensitive.
- `429`: rate limit exceeded (see below).

### Rate limit response

`POST /api/urls` is limited per client IP, by default to 10 creations per 60 seconds (fixed window, configurable). The 11th request from one IP within the window returns:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: <seconds until the window resets>
```

- `Retry-After` is the number of seconds to wait before retrying. The format (delta-seconds) is **(unverified)**.
- The response body format is **(unverified)**.
- Other IPs are unaffected, and creation succeeds again after the window elapses.
- `GET /r/{code}` is not rate limited.

### Redirect

```http
GET /r/my-link
→ 302 Found
Location: https://example.com/page
```

- Unknown code: `404`.
- Expired or deactivated link: `410`.
- Only 302 responses increment the click count.

### Metadata

```http
GET /api/urls/my-link
→ 200
```

```json
{
  "code": "my-link",
  "targetUrl": "https://example.com/page",
  "createdAt": "2026-10-09T10:00:00Z",
  "expiresAt": "2030-01-01T00:00:00Z",
  "isExpired": false,
  "isActive": true
}
```

### Analytics

```http
GET /api/urls/my-link/analytics
→ 200
```

```json
{
  "totalClicks": 3,
  "lastClickAt": "2026-10-09T10:05:00Z",
  "clicksPerDay": { "2026-10-09": 3 }
}
```

The exact name and shape of the per-day field is **(unverified)**.

### Deactivate

```http
DELETE /api/urls/my-link
Authorization: Bearer <ownerToken>
→ 204 No Content
```

- A missing token returns `401`. A wrong token returns `403`. An unknown code returns `404`.
- A failed attempt leaves the link active.
- After deactivation, `/r/{code}` returns 410 and metadata shows `isActive=false`.

## Security controls

- URL scheme allowlist: only http and https.
- SSRF block: rejects localhost, loopback, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16) and IPv6 unique-local/loopback hosts. IPv4-mapped IPv6 addresses are unmapped and checked. Parsing uses `System.Net.IPAddress`.
- Input limits: URL up to 2048 characters, no userinfo, alias 3-32 characters of `[A-Za-z0-9_-]`.
- Rate limiting on creation, per IP.
- Owner tokens: 32 random bytes (256 bits), base64url, stored only as a SHA-256 hash, shown once, compared in constant time.
- Codes come from a cryptographically secure random source.
- Analytics are aggregate counts only, with no IP or user agent stored.

## Assumptions

- The owner is identified only by the opaque owner token. There are no accounts.
- The client for rate limiting is the remote IP address.
- In-memory storage is acceptable, behind a repository abstraction.
- Redirects use 302 so that clicks are counted and expiry and deactivation take effect.
- Hosts are checked by literal IP and the name `localhost`. DNS resolution is out of scope.
- Expiry is an absolute UTC timestamp. A relative TTL is not supported.
- Extending expiry, audit logging and un-deactivating links are out of scope.

## Risks and trade-offs

- In-memory data is lost on restart (accepted).
- DNS names that resolve to private IPs, and DNS rebinding, are not covered.
- A limiter keyed on `RemoteIpAddress` misidentifies clients behind a proxy unless forwarded headers are configured. The limiter's window map also needs cleanup to avoid memory growth.
- Alias and generated-code races are handled with an atomic add and a retry on generated-code collision.
- Unusual host encodings (decimal, octal, hex, IPv4-mapped) could bypass the private check. The design relies on `IPAddress` parsing to mitigate this.
- 302 redirects of arbitrary URLs allow open-redirect or phishing use. They are mitigated only by the scheme and host checks.
- Existing code may already partly implement the requirements. Gaps should be found by test before code is changed.
- Time-dependent tests need an injectable `TimeProvider`.

## Limitations

- Data is not durable.
- No SSRF protection by DNS resolution.
- No user accounts, token recovery, expiry extension or reactivation.
- Analytics are aggregate only.
- Performance (p95 under 50 ms at 200 requests/second) and coverage (at least 80%) targets are **(unverified)** until measured.

## Review notes addressed

- "Document the rate limit response and Retry-After header"
  - Addressed in the "Rate limit response" section under API reference. It covers the 429 status, the `Retry-After` header, the per-IP scope, the reset after the window, and the exclusion of redirects. The body and header format details are marked unverified.
