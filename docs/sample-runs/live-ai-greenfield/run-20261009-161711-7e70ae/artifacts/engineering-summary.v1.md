# Engineering Summary

**Requirement (greenfield):** Deliver an ASP.NET Core minimal-API URL shortener that creates short links (optional custom alias and expiry), redirects them, records per-link click analytics, lets owners deactivate links, rate limits link creation per client, and rejects unsafe or private-network target URLs.

## 1. Plan and rationale
- FR-1: Create a short link via POST /api/urls with a target URL, optional custom alias and optional expiry. The response includes the short code and an owner token.
- FR-2: Allow an optional custom alias used as the short code.
- FR-3: Redirect GET /r/{code} to the target URL for active, unexpired links.
- FR-4: Support optional expiry on links, supplied as an absolute UTC timestamp at creation.
- FR-5: Expose link metadata via GET /api/urls/{code}.
- FR-6: Record each successful redirect and expose analytics via GET /api/urls/{code}/analytics.
- FR-7: Let the owner deactivate a link using the owner token returned at creation.
- FR-8: Rate limit POST /api/urls per client, identified by the remote IP address.
- FR-9: Reject unsafe target URLs at creation.
- NFR: GET /r/{code} p95 server-side latency under 50 ms at 200 requests/second with in-memory storage.
- NFR: Short code generation uses a cryptographically secure random source.
- NFR: Owner tokens are at least 128 bits of entropy and are stored only as a hash.
- NFR: Unit/integration test line coverage of at least 80% for src/UrlShortener.Api.
- NFR: Concurrent click recording is thread-safe: 1000 parallel redirects yield totalClicks of exactly 1000.

Key decisions:
- **Use 302 redirects** - Ensures every click reaches the server so it can be counted and expiry/deactivation apply (alternative: 301/308 permanent redirects, which browsers cache)
- **Owner token is 32 random bytes, base64url, stored as SHA-256 hash and shown once** - At least 128 bits entropy, no accounts needed, a store leak does not expose tokens (alternative: User accounts or storing tokens in plain text)
- **Fixed-window per-IP limiter on POST /api/urls only, with Retry-After** - Simple, matches the spec, configurable; redirects remain unaffected (alternative: Built-in ASP.NET RateLimiter middleware or sliding window)
- **Keep legacy routes GET /{code}, /stats and DELETE working by sharing service logic with canonical routes** - Backward compatibility (alternative: Remove or redirect the legacy routes)
- **Use TimeProvider injection and Interlocked/ConcurrentDictionary for counters** - Deterministic tests and exact counts under 1000 parallel redirects (alternative: Locks around a shared dictionary, or DateTime.UtcNow directly)
- **Host safety check by literal IP and localhost, no DNS resolution** - Matches the stated assumption and avoids network access at creation (alternative: Resolve DNS and block private results, which adds latency and still has rebinding risk)

Impact analysis (existing code):
- `src/UrlShortener.Api/Endpoints/ShortLinkEndpoints.cs` (score 9.2; Holds all routes; verify canonical and legacy routes, status codes, Location header, bearer parsing, Retry-After)
- `src/UrlShortener.Api/Endpoints/Contracts.cs` (score 6; Response DTOs must include expiresAt, isExpired, isActive and exclude the owner token from metadata)
- `src/UrlShortener.Api/Services/ShortenerService.cs` (score 9.4; Core create/resolve/deactivate logic and click recording on success only)
- `src/UrlShortener.Api/Services/Results.cs` (score 10.4; Result and status types may need expired/forbidden/unauthorized statuses)
- `src/UrlShortener.Api/Validation/UrlValidator.cs` (score 7.8; Scheme allowlist, userinfo, length and private-network host blocking)
- `src/UrlShortener.Api/Validation/AliasValidator.cs` (score 6.6; Alias length and character rules)
- `src/UrlShortener.Api/Reliability/FixedWindowRateLimiter.cs` (score 7; Per-IP 10 per 60s limit and retry-after calculation)
- `src/UrlShortener.Api/Analytics/AnalyticsStore.cs` (score 2.6; Thread-safe counters, daily buckets, lastClickAt)
- `src/UrlShortener.Api/Analytics/ClickRecorder.cs` (score 7.4; Click recording must be exact under concurrency)
- `src/UrlShortener.Api/Persistence/InMemoryUrlRepository.cs` (score 2.2; Atomic add and case-sensitive lookup for alias conflicts)
- `src/UrlShortener.Api/Domain/CodeGenerator.cs` (score 1; Must use cryptographically secure randomness for 7-char codes)
- `src/UrlShortener.Api/Domain/ShortLink.cs` (score 3.2; Stores owner token hash, active flag and expiry)
- `src/UrlShortener.Api/Options/ShortenerOptions.cs` (score 1.2; Configurable limits)
- `src/UrlShortener.Api/Program.cs` (score 0.2; DI registration, TimeProvider, forwarded headers if needed)
- data flow: Create: client -> rate limiter (IP) -> UrlValidator/AliasValidator -> ShortenerService -> CodeGenerator -> repository (hash of owner token) -> 201 with plaintext token once
- data flow: Redirect: client -> ShortenerService.Resolve -> repository -> status (404/410/302) -> on 302 only ClickRecorder -> AnalyticsStore
- data flow: Analytics: client -> service -> AnalyticsStore snapshot -> JSON
- data flow: Deactivate: client with bearer token -> hash and constant-time compare -> repository sets IsActive=false

Human change requests applied (re-planning):
- [docs] Document the rate limit response and Retry-After header

## 2. Artifacts
- `api-docs@v2` by AiDocsAgent (sha256 37c4a697102b)
- `changeset@v1` by AiDeveloperAgent (sha256 651473f6925b)
- `design@v1` by AiArchitectAgent (sha256 76d2b75ee611)
- `openapi@v1` by AiDeveloperAgent (sha256 eaae356d9b11)
- `plan@v1` by AiArchitectAgent (sha256 107e5ada0706)
- `proposed-changes@v1` by AiDeveloperAgent (sha256 70cb9c0a7aee)
- `proposed-tests@v1` by AiTesterAgent (sha256 5151f080fb5c)
- `requirement@v2` by AiRequirementsAgent (sha256 dd25de644b37)
- `schema@v1` by AiDeveloperAgent (sha256 3db9cf0fba3d)
- `security-report@v2` by SecurityAgent (sha256 9dc07e6ddca8)
- `test-plan@v1` by AiTesterAgent (sha256 344bd25abfa8)
- `test-report@v1` by orchestrator:sandbox (sha256 57efc1d3abc6)
- `validation-report@v2` by orchestrator:sandbox (sha256 66d76b8c9b93)
- planned changes: 14 (0 new, 7 modified)

## 3. Risks, trade-offs and validation
- Risk: In-memory data lost on restart (accepted; repository abstraction allows later DB)
- Risk: SSRF via DNS names resolving to private IPs or DNS rebinding is not covered (literal IP and localhost only)
- Risk: Rate limiter keyed on RemoteIpAddress is wrong behind a proxy unless forwarded headers are configured; memory growth of window map needs cleanup
- Risk: Alias/generated code race: must use atomic TryAdd and retry on generated collision
- Risk: Decimal/octal/hex or IPv4-mapped IPv6 host encodings could bypass private check; rely on System.Net.IPAddress parsing and unmap IPv4-mapped
- Risk: 302 redirect of arbitrary URLs enables open redirect/phishing use; mitigated only by scheme and host checks
- Risk: Existing code may already partly implement the requirements; gaps must be found by test before changing code
- Risk: Time-dependent tests (expiry, rate window) need an injectable TimeProvider
- Tests: Real suite executed: build succeeded and 49/49 tests passed (exit code 0)
- Security review: passed; controls: url-scheme-allowlist, ssrf-block, input-limits, rate-limit, owner-token-hashing, constant-time-token-compare, secure-random-codes, no-personal-data-in-analytics
- Checklist:
  - [x] Requirements resolved (no open ambiguity) - 9 functional requirement(s)
  - [x] Task plan is a valid DAG covering every requirement - 7 task(s)
  - [x] Design declares required security controls - url-scheme-allowlist, ssrf-block, input-limits, rate-limit, owner-token-hashing, constant-time-token-compare, secure-random-codes, no-personal-data-in-analytics
  - [x] Change-set paths verified against the codebase - 14 path(s); 7 file(s) with proposed code
  - [x] Proposed code built and existing suite passed in the sandbox - build succeeded and 49/49 tests passed (exit code 0)
  - [x] Tests executed by the real test runner and passed - Real suite executed: build succeeded and 49/49 tests passed (exit code 0)
  - [x] Documentation generated - api-docs@v2
  - [x] Security and policy review passed - 1 finding(s)
  - [x] Human approval recorded - 1 human approval(s)
  - [x] Proposed changes not applied to the repository (exported for review) - Proposed by the developer agent. NOT applied to the repository; validated in a sandbox and exported for human review.

## 4. Assumptions
- Assumed (no answer): Owner is identified by a random opaque owner token returned once at creation and sent as a bearer token on DELETE; there are no user accounts.
- Assumed (no answer): Client for rate limiting is the remote IP address; the limit is 10 link creations per 60 seconds per IP (fixed window), configurable.
- Assumed (no answer): In-memory storage is acceptable; data is lost on restart, behind a repository abstraction so a durable database can be added later.
- Assumed: Redirect uses HTTP 302 so that clicks are always counted and expiry or deactivation takes effect.
- Assumed: Hostnames are checked for private/loopback addresses by literal IP and the name 'localhost'; DNS resolution of hostnames at creation is out of scope.
- Assumed: Expiry is an absolute UTC timestamp (expiresAt); relative TTL is not supported.
- Assumed: Analytics are aggregate counts only; no IP, user agent or other personal data is stored per click.
- Assumed: Extending expiry, audit logging and un-deactivating links are out of scope for this iteration.

## 5. Agents and verification
- Agents: LLM-backed (claude-sonnet-5-5); 8 model call(s), 1 rejected or failed, cost $0.8081.
- Verification: real test runner output (exit code 0); 12 proposed test(s) executed.
- Proposed code is never applied to the repository by the system; it is exported under `proposed/` for human review.

## 6. Limitations
- Model output is non-deterministic; gates, policies, sandbox execution and human approval are the safety net, not the model's own claims.
- In-memory storage and in-process rate limiting do not scale horizontally (production path: SQL store, shared rate-limit state).
- Human approval is scripted or console based; production would integrate a ticketing or review UI.
- Client identity is a spoofable header until real authentication is added.

Decision lineage entries: 12; artifact versions produced: 17.
