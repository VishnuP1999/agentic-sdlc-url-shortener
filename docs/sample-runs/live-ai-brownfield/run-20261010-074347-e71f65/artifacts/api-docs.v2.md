# URL Shortener API

> Allow the owner of an existing short link to extend its expiry, and include the remaining time until expiry in the analytics (stats) response, without breaking existing behaviour.

## Endpoints

| Method | Path | Purpose | Status codes |
|---|---|---|---|
| POST | `/api/urls/{code}/extend` | Owner extends expiry by additionalSeconds (FR-1, FR-2). Body {additionalSeconds}. Returns {code, expiresAt, remainingSeconds}. Expired-but-active links are revived from now. | 200, 400, 403, 404, 409, 429 |
| PUT | `/api/urls/{code}/expiry` | Canonical alias of the extend operation, same handler, body and responses. | 200, 400, 403, 404, 409, 429 |
| GET | `/api/urls/{code}/analytics` | Existing stats plus additive remainingSeconds (long, null if no expiry, 0 if expired) (FR-3). | 200, 404 |
| GET | `/api/urls/{code}/stats` | Backward-compatible alias of analytics; same response including remainingSeconds. | 200, 404 |
| GET | `/api/urls/{code}` | Metadata; reflects updated expiresAt after extend (FR-4). | 200, 404 |
| GET | `/r/{code}` | Redirect unchanged; works again after extending an expired link (FR-2, FR-4). | 302, 404, 410 |
| DELETE | `/api/urls/{code}` | Deactivate unchanged (204/403/404). | 204, 403, 404 |
| POST | `/api/urls` | Create unchanged. | 200, 201, 400, 409, 429 |
| GET | `/health` | Health unchanged. | 200 |

## Requirements and acceptance criteria

**FR-1 - Owner can extend the expiry of an existing, non-deleted short link by supplying an additional duration in seconds, which is added to the current expiry. Exposed as POST /api/urls/{code}/extend using the same owner authorization mechanism as deactivation.**
- POST /api/urls/{code}/extend with a valid owner credential and a positive additionalSeconds returns 200 and the body contains the new expiresAt equal to the previous expiresAt plus additionalSeconds.
- The extend request for an unknown code returns 404.
- A request with additionalSeconds missing, zero, negative or non-numeric returns 400 and leaves expiry unchanged.
- A request with a missing or wrong owner credential returns the same status as deactivation does for that case (401/403) and leaves expiry unchanged.
- A request that would make the total lifetime from now exceed 365 days returns 400 and leaves expiry unchanged.

**FR-2 - Extending behaves sensibly for links in special states: expired, deactivated, or having no expiry.**
- Extending a link that has already expired but is not deactivated returns 200 and sets expiresAt to now plus additionalSeconds, after which GET /r/{code} redirects again.
- Extending a deactivated link returns 409 (or the status deactivation conflicts use) and does not reactivate it.
- Extending a link that has no expiry returns 409 and does not set an expiry.

**FR-3 - The stats response of GET /api/urls/{code}/analytics includes the remaining time until expiry, as an additive field.**
- For a link with a future expiry, GET /api/urls/{code}/analytics returns 200 with remainingSeconds as a non-negative integer, equal to expiresAt minus now, rounded down.
- For a link that is already expired, remainingSeconds is 0.
- For a link with no expiry, remainingSeconds is null.
- All fields that existed in the analytics response before remain present with unchanged names and types.

**FR-4 - Existing behaviour must stay backward compatible, and the new extend endpoint must not alter redirect, create, or deactivate semantics.**
- GET /r/{code} for an active, unexpired link still returns the same redirect status as before and still counts the click.
- After a successful extend, GET /api/urls/{code} returns the updated expiresAt.
- POST /api/urls and the deactivate endpoint keep their existing request and response contracts.

## Security controls
- `url-scheme-allowlist`
- `ssrf-block`
- `input-limits`
- `rate-limit`
- `audit-logging`
- `owner-authorization`
- `optimistic-concurrency`

## Assumptions
- Assumed (no answer): the extend request specifies an additional duration in whole seconds (additionalSeconds) added to the current expiry, not an absolute timestamp.
- Assumed (no answer): the maximum total lifetime from the current time is 365 days; requests exceeding it are rejected with 400.
- Assumed (no answer): the owner is identified and authorized the same way the existing deactivation endpoint does (reusing its credential and its 401/403/404 behaviour); no new identity mechanism is introduced.
- Assumed (no answer): the remaining time is exposed as remainingSeconds, an integer or null, added to the existing analytics response, computed from the server clock at request time.
- Assumed (no answer): extending an expired but not deactivated link revives it from the current time; deactivated links cannot be extended.
- Assumed (no answer): links without an expiry cannot be extended, since there is nothing to extend (409).
