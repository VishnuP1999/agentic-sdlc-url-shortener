# Engineering Summary

**Requirement (greenfield):** Deliver a URL shortener whose redirects are fast (p95 < 50 ms at 500 req/s, with asynchronous click analytics) and whose links are safer (unsafe/malicious/private-network targets rejected, abusive clients rate limited, links expire).

## 1. Plan and rationale
- FR-1: Create short links via POST /api/urls with a target URL and optional expiry; response returns the code and short URL.
- FR-2: Resolve GET /r/{code} to the target quickly, with the click recorded without blocking the response.
- FR-3: Click analytics are recorded asynchronously and exposed via GET /api/urls/{code}/analytics.
- FR-4: Reject unsafe target URLs at creation: non-http(s) schemes, embedded credentials, loopback/private/link-local network targets, and domains on the malicious blocklist.
- FR-5: Links support an expiry time after which they no longer redirect.
- FR-6: Rate limit abusive clients on creation and redirect, with separate limits per endpoint.
- NFR: p95 redirect latency under 50 ms at a sustained 500 requests/second
- NFR: Analytics recording adds no synchronous work to the redirect path; the in-memory analytics queue is bounded (configurable capacity) and drops or overflows without failing redirects
- NFR: Rate limit rejections are returned in under 10 ms at p95

Key decisions:
- **Use a bounded Channel with DropWrite and a background consumer for clicks** - Keeps the redirect path non-blocking and unaffected by a slow or failing sink (alternative: Unbounded queue or synchronous write, rejected because of memory risk and latency)
- **Static configurable blocklist with suffix matching** - Matches the stated assumption; no external dependency or latency (alternative: External Safe Browsing API)
- **Per-IP partitioned rate limiting with separate policies for create and redirect** - Isolates clients and allows independent configuration (alternative: Global limit, which lets one client affect others)
- **Resolve and validate DNS at creation, rejecting any private result** - Meets the hostname-to-private-IP acceptance criterion (alternative: Literal IP checks only)
- **Use 302 and return 410 for expired links; count only successful 302s** - Honors expiry and keeps analytics accurate (alternative: 301 permanent redirect, which is cached by clients)

Impact analysis (existing code):
- `src/UrlShortener.Api/Validation/UrlValidator.cs` (score 2.2; Add scheme, credential, private-range, DNS and blocklist checks)
- `src/UrlShortener.Api/Services/ShortenerService.cs` (score 6.2; Expiry validation, expired resolve status, click enqueue only on success)
- `src/UrlShortener.Api/Services/Results.cs` (score 7; Add Expired and invalid-expiry statuses)
- `src/UrlShortener.Api/Endpoints/ShortLinkEndpoints.cs` (score 4.2; Map 410/429/400, apply rate limit policies, Retry-After)
- `src/UrlShortener.Api/Endpoints/Contracts.cs` (score 3.4; Expose expiry and expired flag; accept expiry on create)
- `src/UrlShortener.Api/Analytics/ClickRecorder.cs` (score 8.8; Bounded channel, non-blocking writes, failure isolation)
- `src/UrlShortener.Api/Analytics/AnalyticsStore.cs` (score 4.4; Eventually consistent counters)
- `src/UrlShortener.Api/Reliability/FixedWindowRateLimiter.cs` (score 13; Per-client, per-policy limits, Retry-After, eviction)
- `src/UrlShortener.Api/Options/ShortenerOptions.cs` (score 1.8; Add limits, queue capacity, blocklist, trusted proxies)
- `src/UrlShortener.Api/Domain/ShortLink.cs` (score 3.4; Ensure ExpiresAt and expiry check)
- `src/UrlShortener.Api/Persistence/InMemoryUrlRepository.cs` (score 0.4; Fast lookup path)
- `src/UrlShortener.Api/Program.cs` (score 0.2; DI and config wiring)
- `src/UrlShortener.Api/AppHost.cs` (score 9.6; Middleware and host wiring)
- data flow: Create: client -> rate limiter -> endpoint -> UrlValidator (scheme, credentials, blocklist, DNS/private check) -> expiry check -> repository -> 201
- data flow: Redirect: client -> rate limiter -> repository lookup -> expiry/active check -> 302 and TryWrite ClickEvent to channel (non-blocking)
- data flow: Analytics: channel -> ClickProcessor background consumer -> AnalyticsStore -> GET analytics
- data flow: Rejected (404/410/429): no ClickEvent enqueued

## 2. Artifacts
- `api-docs@v1` by AiDocsAgent (sha256 c4a8070146a1)
- `changeset@v1` by AiDeveloperAgent (sha256 85981075abad)
- `design@v1` by AiArchitectAgent (sha256 c4de6b3562a5)
- `openapi@v1` by AiDeveloperAgent (sha256 e9344c0e1ccd)
- `plan@v1` by AiArchitectAgent (sha256 bd49df9f1610)
- `proposed-changes@v1` by AiDeveloperAgent (sha256 e04526bd8f77)
- `proposed-tests@v1` by AiTesterAgent (sha256 df3b247db333)
- `requirement@v3` by AiRequirementsAgent (sha256 49bf525e13e3)
- `schema@v1` by AiDeveloperAgent (sha256 8767e4e8db07)
- `security-report@v1` by SecurityAgent (sha256 90a584f2bfcd)
- `test-plan@v1` by AiTesterAgent (sha256 2f9a1fb2b3f4)
- `test-report@v1` by orchestrator:sandbox (sha256 90873ce77144)
- `validation-report@v2` by orchestrator:sandbox (sha256 65e161eaa198)
- planned changes: 14 (1 new, 8 modified)

## 3. Risks, trade-offs and validation
- Risk: DNS rebinding: creation-time resolution can differ later; redirects are client-side so server SSRF is limited, but document it
- Risk: DNS lookups at creation can be slow; apply timeout and fail closed
- Risk: Bounded queue drops clicks under overload, so analytics can undercount (accepted trade-off)
- Risk: In-memory state is lost on restart and is not shared across instances; rate limits are per-instance
- Risk: Per-IP limiting behind proxies needs trusted-proxy config or all clients look the same
- Risk: Fixed-window limiter allows boundary bursts; sliding window costs more
- Risk: Stale clock or time zone bugs in expiry; use UTC and an injectable clock
- Risk: The p95 < 50 ms target must be verified with a real load test, which is environment-dependent
- Risk: Rate limiter memory growth from many IPs; needs window eviction
- Tests: Real suite executed: build succeeded and 48/48 tests passed (exit code 0)
- Security review: passed; controls: url-scheme-allowlist, ssrf-block, input-limits, rate-limit
- Checklist:
  - [x] Requirements resolved (no open ambiguity) - 6 functional requirement(s)
  - [x] Task plan is a valid DAG covering every requirement - 7 task(s)
  - [x] Design declares required security controls - url-scheme-allowlist, ssrf-block, input-limits, rate-limit
  - [x] Change-set paths verified against the codebase - 14 path(s); 9 file(s) with proposed code
  - [x] Proposed code built and existing suite passed in the sandbox - build succeeded and 48/48 tests passed (exit code 0)
  - [x] Tests executed by the real test runner and passed - Real suite executed: build succeeded and 48/48 tests passed (exit code 0)
  - [x] Documentation generated - api-docs@v1
  - [x] Security and policy review passed - 1 finding(s)
  - [x] Human approval recorded - 1 human approval(s)
  - [x] Proposed changes not applied to the repository (exported for review) - Proposed by the developer agent. NOT applied to the repository; validated in a sandbox and exported for human review.

## 4. Assumptions
- Assumed (no answer): the malicious-domain list is a static, configurable blocklist maintained by us (configuration file); no external Safe Browsing/threat-intelligence service is called.
- Assumed (no answer): clients are identified by remote IP address (no API keys); proxy headers are only trusted if explicitly configured.
- Assumed (no answer): default limits are 30 link creations per minute and 600 redirects per minute per client IP, fixed or sliding window, configurable.
- Assumed: 'faster' applies to redirects only; creation has no separate latency target.
- Assumed: expiry is optional per link, specified as a UTC timestamp at creation; no default expiry unless configured.
- Assumed: owner deactivation and extension of expiry are out of scope for this requirement as the stakeholder did not select them.
- Assumed: redirect uses HTTP 302 so that expiry and deactivation are honored by clients; storage is in-memory/with a cache for the first iteration.
- Stakeholder answer: "p95 redirect latency under 50 ms at 500 req/s; click analytics must be recorded asynchronously."
- Stakeholder answer: "Block malicious and private-network target URLs and rate limit abusive clients; links must also support expiry."

## 5. Agents and verification
- Agents: LLM-backed (claude-sonnet-5-5); 7 model call(s), 0 rejected or failed, cost $0.5533.
- Verification: real test runner output (exit code 0); 11 proposed test(s) executed.
- Proposed code is never applied to the repository by the system; it is exported under `proposed/` for human review.

## 6. Limitations
- Model output is non-deterministic; gates, policies, sandbox execution and human approval are the safety net, not the model's own claims.
- In-memory storage and in-process rate limiting do not scale horizontally (production path: SQL store, shared rate-limit state).
- Human approval is scripted or console based; production would integrate a ticketing or review UI.
- Client identity is a spoofable header until real authentication is added.

Decision lineage entries: 9; artifact versions produced: 16.
