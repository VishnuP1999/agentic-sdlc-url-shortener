# Engineering Summary

**Requirement (brownfield):** Allow the owner of an existing short link to extend its expiry, and include the remaining time until expiry in the analytics (stats) response, without breaking existing behaviour.

## 1. Plan and rationale
- FR-1: Owner can extend the expiry of an existing, non-deleted short link by supplying an additional duration in seconds, which is added to the current expiry. Exposed as POST /api/urls/{code}/extend using the same owner authorization mechanism as deactivation.
- FR-2: Extending behaves sensibly for links in special states: expired, deactivated, or having no expiry.
- FR-3: The stats response of GET /api/urls/{code}/analytics includes the remaining time until expiry, as an additive field.
- FR-4: Existing behaviour must stay backward compatible, and the new extend endpoint must not alter redirect, create, or deactivate semantics.
- NFR: The extend endpoint responds within 200 ms at p95 under 50 concurrent requests, excluding network latency.
- NFR: Concurrent extend requests on the same link must not lose updates: N concurrent extends of S seconds each result in total added time of N*S seconds, unless the maximum lifetime cap is reached.

Key decisions:
- **Add audit logging for expiry changes (reviewer request): emit structured ILogger events for every extend attempt, success and denied/failed, with action, code, hashed actor, outcome, old and new expiry.** - Satisfies the reviewer change request with no new infrastructure; includes denials since they are security-relevant. (alternative: Persistent audit table: more durable but needs a schema change and storage not present in the in-memory prototype.)
- **Expose both POST /api/urls/{code}/extend and PUT /api/urls/{code}/expiry on one handler.** - The requirement names the former, the canonical routes name the latter; both stay consistent. (alternative: Only one route: would violate either the requirement or the canonical contract.)
- **Add IUrlRepository.TryReplace(code, expected, updated) compare-and-swap and loop in the service.** - Mirrors the existing Deactivate CAS pattern; guarantees no lost updates for N concurrent extends. (alternative: A global lock: simpler but serialises all links and does not suit a future SQL repository.)
- **Compute remainingSeconds in the service from TimeProvider, floor, clamp to 0; null when ExpiresAt is null.** - Deterministic with a fake clock in tests; additive and backward compatible. (alternative: Compute in the endpoint: splits logic and harder to test.)
- **Status mapping: deactivated 409, no expiry 409, bad/missing/non-positive additionalSeconds 400, cap exceeded 400, wrong owner 403 (same as deactivate), unknown 404.** - Matches the acceptance criteria and the deactivate behaviour. (alternative: 422 for validation: deviates from the stated criteria.)
- **Apply FixedWindowRateLimiter to extend, keyed by client id.** - Mandatory rate-limit control; reuses existing component. (alternative: No limit: leaves a mutation endpoint unthrottled.)

Impact analysis (existing code):
- `src/UrlShortener.Api/Endpoints/ShortLinkEndpoints.cs` (score 3.4; Map POST /{code}/extend and PUT /{code}/expiry, handler with rate limiting, status mapping.)
- `src/UrlShortener.Api/Endpoints/Contracts.cs` (score 0.4; Add ExtendExpiryRequest and ExtendExpiryResponse.)
- `src/UrlShortener.Api/Services/Results.cs` (score 3.2; Add ExtendStatus/ExtendResult and RemainingSeconds on LinkStats.)
- `src/UrlShortener.Api/Services/ShortenerService.cs` (score 7.8; Add ExtendExpiry, remainingSeconds in GetStats, audit logging.)
- `src/UrlShortener.Api/Persistence/IUrlRepository.cs` (score 0.8; Add atomic TryReplace.)
- `src/UrlShortener.Api/Persistence/InMemoryUrlRepository.cs` (score 0.6; Implement TryReplace using ConcurrentDictionary.TryUpdate.)
- `src/UrlShortener.Api/AppHost.cs` (score 2.4; Pass ILogger into ShortenerService construction.)
- data flow: Client -> POST extend (X-Client-Id, body) -> rate limiter -> body validation -> ShortenerService.ExtendExpiry -> repo.Get -> owner/state/cap checks -> repo.TryReplace (CAS, retry on conflict) -> audit log event -> 200 with new expiresAt.
- data flow: Denied or failed extend (403/409/400/404) -> audit log event with outcome -> error response; no state change.
- data flow: Client -> GET analytics -> service.GetStats -> repo.Get + AnalyticsStore.Get + clock -> LinkStats with remainingSeconds.
- data flow: Redirect path untouched: repo.Get -> active and unexpired check -> click enqueue -> 302.

Human change requests applied (re-planning):
- [design] Add audit logging for expiry changes

## 2. Artifacts
- `api-docs@v2` by DocsAgent (sha256 14c50ac90ca1)
- `changeset@v2` by AiDeveloperAgent (sha256 06b214f0e1a5)
- `design@v4` by AiArchitectAgent (sha256 d2f654c8cc50)
- `openapi@v2` by AiDeveloperAgent (sha256 e02a2d02f5ad)
- `plan@v4` by AiArchitectAgent (sha256 a5d22cde9480)
- `proposed-changes@v2` by AiDeveloperAgent (sha256 accfe79a6586)
- `proposed-tests@v4` by AiTesterAgent (sha256 9348098eeb36)
- `requirement@v2` by AiRequirementsAgent (sha256 2cb440aa6053)
- `schema@v2` by AiDeveloperAgent (sha256 edf5f452f779)
- `security-report@v2` by SecurityAgent (sha256 f8fd8de2d32a)
- `test-plan@v4` by AiTesterAgent (sha256 8c53a88b0992)
- `test-report@v4` by orchestrator:sandbox (sha256 ce1dc9c7d679)
- `validation-report@v6` by orchestrator:sandbox (sha256 4329cda73bb3)
- planned changes: 7 (0 new, 7 modified)

## 3. Risks, trade-offs and validation
- Risk: Owner identity is the spoofable X-Client-Id header (existing prototype limitation); extend inherits it. Audit actor is therefore unverified.
- Risk: Requirement says POST /extend, canonical list says PUT /expiry: both are exposed to satisfy both; small API surface duplication.
- Risk: Audit logs may be forged via log injection or contain identifiers: log only structured fields, and a SHA-256-prefix hash of the client id, never raw header text.
- Risk: Audit goes to the ILogger sink only; not tamper-proof or durable without a configured sink. Production needs an append-only store.
- Risk: Overflow: huge additionalSeconds could overflow DateTimeOffset arithmetic; bound-check against the cap before adding.
- Risk: CAS retry loop could spin under heavy contention; bounded retries then 409/500 are unlikely at the stated 50 concurrency.
- Risk: Cap semantic (total lifetime from now <= 365 days, reusing MaxExpiryMinutes) must be tested at the boundary.
- Risk: Check order (404, 403, 400, 409) leaks no extra info beyond deactivate, but an invalid body from a non-owner returns 403 first.
- Tests: Real suite executed: build succeeded and 48/48 tests passed (exit code 0)
- Security review: passed; controls: url-scheme-allowlist, ssrf-block, input-limits, rate-limit, audit-logging, owner-authorization, optimistic-concurrency
- Checklist:
  - [x] Requirements resolved (no open ambiguity) - 4 functional requirement(s)
  - [x] Task plan is a valid DAG covering every requirement - 7 task(s)
  - [x] Design declares required security controls - url-scheme-allowlist, ssrf-block, input-limits, rate-limit, audit-logging, owner-authorization, optimistic-concurrency
  - [x] Change-set paths verified against the codebase - 7 path(s); 7 file(s) with proposed code
  - [x] Proposed code built and existing suite passed in the sandbox - build succeeded and 48/48 tests passed (exit code 0)
  - [x] Tests executed by the real test runner and passed - Real suite executed: build succeeded and 48/48 tests passed (exit code 0)
  - [x] Documentation generated - api-docs@v2
  - [x] Security and policy review passed - 0 finding(s)
  - [x] Human approval recorded - 3 human approval(s)
  - [x] Proposed changes not applied to the repository (exported for review) - Proposed by the developer agent. NOT applied to the repository; validated in a sandbox and exported for human review.

## 4. Assumptions
- Assumed (no answer): the extend request specifies an additional duration in whole seconds (additionalSeconds) added to the current expiry, not an absolute timestamp.
- Assumed (no answer): the maximum total lifetime from the current time is 365 days; requests exceeding it are rejected with 400.
- Assumed (no answer): the owner is identified and authorized the same way the existing deactivation endpoint does (reusing its credential and its 401/403/404 behaviour); no new identity mechanism is introduced.
- Assumed (no answer): the remaining time is exposed as remainingSeconds, an integer or null, added to the existing analytics response, computed from the server clock at request time.
- Assumed (no answer): extending an expired but not deactivated link revives it from the current time; deactivated links cannot be extended.
- Assumed (no answer): links without an expiry cannot be extended, since there is nothing to extend (409).

## 5. Agents and verification
- Agents: LLM-backed (claude-sonnet-5-5); 12 model call(s), 0 rejected or failed, cost $1.5874.
- Verification: real test runner output (exit code 0); 11 proposed test(s) executed.
- Proposed code is never applied to the repository by the system; it is exported under `proposed/` for human review.

## 6. Limitations
- Model output is non-deterministic; gates, policies, sandbox execution and human approval are the safety net, not the model's own claims.
- In-memory storage and in-process rate limiting do not scale horizontally (production path: SQL store, shared rate-limit state).
- Human approval is scripted or console based; production would integrate a ticketing or review UI.
- Client identity is a spoofable header until real authentication is added.

Decision lineage entries: 17; artifact versions produced: 40.
