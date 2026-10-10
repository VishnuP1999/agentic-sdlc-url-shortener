# URL Shortener API

> Build a URL shortener service: create short links with optional custom aliases and expiry, redirect them, track click analytics per link, let owners deactivate links, and rate limit link creation per client. Block unsafe or private-network target URLs.

## Endpoints

| Method | Path | Purpose | Status codes |
|---|---|---|---|
| POST | `/api/urls` | Create a short link | 201, 200, 400, 409, 429 |
| GET | `/{code}` | Redirect to the target URL | 302, 404, 410 |
| GET | `/api/urls/{code}/stats` | Click analytics for a link | 200, 404 |
| DELETE | `/api/urls/{code}` | Deactivate a link | 204, 403, 404 |
| GET | `/health` | Liveness probe | 200 |

## Requirements and acceptance criteria

**FR-1 - Create a short link for a long URL**
- POST /api/urls returns 201 with a unique short code
- Invalid or unsafe target URLs are rejected with 400
- Creating the same URL again for the same client returns the existing link

**FR-2 - Redirect a short code to its target URL**
- GET /{code} answers 302 with the target in Location
- Unknown codes answer 404
- Expired or deactivated links answer 410

**FR-3 - Expose click analytics per short link**
- GET /api/urls/{code}/stats returns total clicks, last access and a daily breakdown
- Recording clicks never slows down redirects
- Unknown codes answer 404

**FR-4 - Support optional link expiry**
- A link created with expiresInMinutes stops redirecting after that time (410)
- Out-of-range expiry values are rejected with 400

**FR-5 - Rate-limit link creation per client**
- Requests above the per-client quota answer 429 with Retry-After
- The quota window resets after it elapses

**FR-6 - Allow custom aliases for short links**
- A valid alias becomes the short code
- A taken alias answers 409; reserved or malformed aliases answer 400

**FR-7 - Allow owners to deactivate a link**
- DELETE /api/urls/{code} answers 204 for the owner
- Other clients receive 403
- Deactivated links answer 410 on redirect

**FR-8 - Reject unsafe target URLs (non-http schemes, credentials, private/loopback networks)**
- Non-http(s) schemes are rejected
- Loopback, private and link-local targets are rejected

## Security controls
- `url-scheme-allowlist`
- `ssrf-block`
- `input-limits`
- `rate-limit`

## Assumptions
- No end-user authentication in this iteration: clients are identified by the X-Client-Id header or IP (spoofable).
- Single-instance deployment with in-memory storage behind a repository abstraction.

## Additional documentation (requested in review)

_Review note: Document the rate limit response and Retry-After header_

When a client exceeds its quota the API answers `429 Too Many Requests` and sets a
`Retry-After` header with the number of seconds to wait before retrying.
