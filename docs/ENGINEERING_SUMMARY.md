# Final Engineering Summary

## 1. Plan and rationale

**Goal.** Turn software requirements into reviewable engineering outcomes through an agentic execution model:
- requirement analysis
- task decomposition
- AI-assisted implementation
- validation
- documentation
- controlled human oversight

The orchestration engine is the differentiator; the URL shortener is the product it works on.

**Approach.**
1. A real, tested product (URL shortener) gives the agents a concrete codebase to reason about and be verified against.
2. A generic orchestration engine (`Agentic.Core`) that knows nothing about URL shorteners. It provides:
   - DAG scheduling and an enforced state machine
   - gates, validation, permissions and policies
   - approvals, re-planning
   - audit, metrics and persistence
3. A model adapter (`Agentic.Model`) that keeps all model-specific code (Claude Code headless, authentication, model
   selection, error classification) away from business logic and workflow control.
4. Claude-backed planner, developer, tester and documentation agents (`Agentic.Agents/Ai`) with distinct prompts,
   schemas and artifact permissions. The rule-based agents stay as fallbacks and as the deterministic test mode.
5. Proof over trust: proposals are built and tested in a sandbox, and the test runner's exit code is the source of truth.
6. Every control has a failing-path test; every scenario runs end to end.

**Why Claude Code headless.** It uses the developer's existing Claude login (no API key handled by the app), validates
structured output against a JSON schema, enforces per-call budgets, and can be locked down to "no tools". Only
`ClaudeCodeModelClient` knows about it.

## 2. Artifacts produced

| Artifact | Location |
|---|---|
| Product: URL shortener API (create, redirect, metadata, analytics, deactivate, health) | `src/UrlShortener.Api` |
| Orchestration engine | `src/Agentic.Core` |
| Model adapter (Claude Code + fake) | `src/Agentic.Model` |
| AI agents, rule-based fallbacks, workflow, sandbox, policies, scenarios | `src/Agentic.Agents`, `scenarios/*.json` |
| CLI (scenarios, model smoke test, metrics, proposal export) | `src/Agentic.Cli` |
| Tests: 37 product + 69 orchestrator/agents/adapter/sandbox/scenarios | `tests/` |
| Architecture, diagrams, decisions | `docs/ARCHITECTURE.md` |
| Per run: versioned artifacts, `state.json`, audit, lineage, model invocations, proposed files | `out/<runId>/` |

### Evidence from live runs (Claude `sonnet` via Claude Code, real sandbox execution)

| Run | Outcome | What happened |
|---|---|---|
| `docs/sample-runs/live-ai-greenfield/` | **Completed** in 5.2 min, 8 model calls, $0.81 | Planner asked 3 clarifying questions (unanswered, so it recorded explicit assumptions); 7-task plan validated; injected developer failure rolled back and retried; 7 proposed source files built and kept the existing 37 tests green; the tester's first answer was rejected (13 tests > limit 12) and repaired; **49/49 tests passed in the real runner (exit 0), including 12 AI-written tests**; reviewer comment re-planned only docs, security and release (plan v2); all 10 checklist items satisfied |
| `docs/sample-runs/live-ai-brownfield/` | **Completed** in 9.7 min, 12 model calls, $1.59 | Impact analysis on the real source (6-7 impacted files); the design was rejected twice by policy SEC-002 (missing `url-scheme-allowlist`) and Claude repaired it from the feedback; schema change approved by a human; the developer proposed the "extend expiry" feature and the sandbox build plus existing 37 tests passed; the AI tester's first tests asserted the wrong existing behaviour (e.g. expected 201, actual 200 for an idempotent create); the real runner caught it and the repaired tests passed; reviewer request "Add audit logging for expiry changes" re-planned everything except requirements (plan v2); final **48/48 passed (exit 0), 11 AI-written**; docs failed 4 times (injected) and the rule-based fallback took over |
| `docs/sample-runs/live-ai-ambiguous/` | **Completed** in 4.1 min, 7 model calls, $0.55 | Vague requirement "Make our links safer and faster.": the planner asked what "faster" and "safer" mean, the reviewer answered (matched by topic), the planner asked two follow-ups that went unanswered and recorded them as explicit assumptions; result: measurable NFRs (p95 redirect < 50 ms at 500 req/s, async analytics), every stage passed first time, **48/48 tests passed (exit 0), 11 AI-written** |
| `docs/sample-runs/live-ai-greenfield-blocked/` | **Failed** (release blocked) | Developer's proposal **broke an existing test** (`Rate_limit_returns_429_with_retry_after`); the sandbox caught it and the failure output went back to the developer, which repaired it. The tester then repeatedly left requirements without negative/edge cases, so after the bounded retries the node failed and `security` and `release` were **Blocked**. This led to the coverage checklist and test-size limit in the tester prompt (next run: Completed). |

The second row is a useful interview story. Governance did its job (nothing unverified was released), and the
audit trail showed exactly why, which pointed straight at the fix.

## 3. Risks, trade-offs and validation

| Risk / failure scenario | Control | Validated by |
|---|---|---|
| Model writes or runs something harmful | No tools, empty working dir, prompt on stdin, schema-only output; `ChangeGuard` + `ProposedChangesPolicy`; sandbox copy only | `Claude_code_runs_without_tools...`, `Change_guard_allows_only_scoped_source_files`, `Developer_proposals_outside_the_allowed_scope_are_rejected` |
| Model claims code works | Orchestrator builds and runs the real suite; test report written from runner output | `Real_sandbox_run_uses_the_actual_test_runner...` |
| Generated code does not compile / breaks tests | Validation fails -> repair attempt with compiler/test output -> bounded retries -> Failed, release blocked | `Repair_attempt_receives_the_previous_failure_as_feedback` |
| Weak fallback hides a real failure | `FallbackWhen.OnlyWhenUnavailable` for developer and tester | `Fallback_restricted_to_unavailability_does_not_mask_repeated_bad_output` |
| Model unavailable / not logged in / unknown model | Classified non-retryable -> straight to rule-based fallback | `Unavailable_model_falls_back...`, `Errors_are_classified...` |
| Invalid, empty or off-schema model output | Schema + typed deserialisation + agent rules -> retry with the reason | `Rejected_model_output_triggers_a_repair_attempt...` |
| Prompt injection via the requirement | Untrusted text framed as data; model has no tools; gates and policies apply regardless | `Untrusted_requirement_text_is_framed_as_data...` |
| Model skips change control | Deterministic schema-change rule OR'd with the model's flag | `A_model_cannot_skip_change_control...` |
| Planner produces an unusable plan | `PlanValidator`: DAG, known roles, coverage of every requirement | `Plan_validator_rejects_cycles...` |
| Agent writes outside its role | `WorkflowNode.Produces` permission check | `A_node_cannot_write_artifacts_outside_its_permissions` |
| Illegal workflow state | `NodeStateMachine`, engine throws on illegal transitions | `StateMachineTests` |
| Failed dependency silently proceeds | Dependents `Blocked` with a recorded decision | `Retry_budget_is_bounded_the_node_fails_and_its_dependents_are_blocked` |
| Unsafe output (credential leak) | Critical policy: no retry, rollback, safe-stop | `Critical_policy_violation...`, scenario `safe-stop` |
| Human silent | No decision = reject | `Rejection_and_silence_both_stop_the_run` |
| Endless loops | Bounded retries, re-plans, clarification rounds; per-call timeout and budget | `Re_planning_is_bounded`, `Unanswered_clarifications_are_bounded` |
| Crash loses state | `state.json` written atomically on every transition; append-only audit and run index | `Workflow_state_is_persisted_while_the_run_progresses` |
| Audit tampering / PII | SHA-256 hash chain; redaction before write; prompts never logged | `Audit_trail_is_hash_chained...`, `Model_invocations_are_audited...` |

**Trade-offs.**
- Output varies between runs: the same scenario can need different numbers of repairs, and can end `Failed` if the model
  cannot produce passing code within the budget. That is the system working as intended, but it makes live runs less
  predictable than the rule-based mode.
- Full-file proposals are simple to validate and apply but cost more output tokens than diffs.
- Each sandbox validation is a fresh `dotnet` build (tens of seconds). This is safe and simple; a warm build server would be faster.
- In-memory storage and in-process rate limiting keep the product runnable anywhere but do not scale horizontally.

**A real concurrency bug found by the automated AI SIT run.** In one ambiguous-scenario run, `docs` succeeded in parallel
with `implementation`; implementation then failed and was rolled back. The rollback restored the *whole* shared context
to a snapshot taken before docs finished, silently erasing the docs artifact, so release (correctly) refused to ship.
Rollback is now scoped to the artifacts the failing node itself wrote (exact, because each node may only write its own
artifacts); regression test `Rollback_in_one_branch_never_discards_a_parallel_branch_output`. Lesson: deterministic
tests had the timing fixed; non-deterministic AI timing exposed the race, and the governance layer caught the symptom.

## 4. Assumptions

- Claude Code is installed and logged in on the machine running the orchestrator; `CLAUDE_MODEL` selects the model.
- No end-user authentication in the product: clients are identified by `X-Client-Id` or IP (spoofable).
- Single-instance deployment; one UTC clock source for expiry.
- Existing public API contracts stay backward compatible (`/{code}` and `/stats` kept next to `/r/{code}` and `/analytics`).
- Scripted decisions in scenario files stand in for human reviewers; `--interactive` makes the console the reviewer.
- "Release" means readiness and approval; nothing is deployed or merged.

## 5. Limitations (unverified or not implemented)

- Proposed code is **exported, not applied**. Applying it to the working tree would be a further approval-gated step.
- Runs are persisted (`state.json`, artifacts, audit) but cannot be **resumed** after a crash.
- Impact analysis combines a heuristic code map with the model's judgement; it is not semantic (production: Roslyn).
- Tester-proposed tests run against a copy of the code and the in-process host only; there are no load or latency tests,
  so NFRs such as "p95 < 50 ms" are recorded but **not verified**.
- Approval is scripted or on the console; there is no review UI or pull-request integration.
- The sandbox isolates by copying files to a temp folder, not by OS-level sandboxing; it is safe because only orchestrator
  code writes there and only `.cs` files in two folders are accepted.

## 6. Production path

- SQL repository and migrations; Redis for rate limiting and click aggregation; authentication.
- Resumable runs replayed from `state.json` and the audit log.
- Containerised sandbox; apply-on-approval via a pull request.
- Roslyn-based impact analysis.
- OpenTelemetry export of audit events, model calls and metrics.
