# Interview Q&A

**Q1. What did you build, in one line?**
A governed agentic SDLC system. Claude-powered planner, developer, tester and documentation agents turn a requirement
into a reviewable outcome. A C# orchestrator controls the work and proves it with gates, policies, a real
build-and-test sandbox, human approvals, retries with repair feedback, rollback, safe-stop and a hash-chained audit
trail. The URL shortener is the product it works on.

**Q2. Does it really use an AI model?**
Yes. In the default `--agents ai` mode, every planner, developer, tester and documentation step is a live Claude call
through Claude Code in headless mode. The model is set by `CLAUDE_MODEL` (default `sonnet`). Each call is audited
with model, latency, tokens and cost (`model-invocations.json`). `--agents rules` switches to deterministic rule-based
agents. Those are also the automatic fallback when the model is unavailable, and they are what the offline test suite uses.

**Q3. What makes it "agentic" and not just a chain of prompts?**
Runtime decisions over state:
- a ready-set scheduler over a DAG with parallel branches and a join
- a planner-generated task graph that is validated before use
- gates that can pause for a human
- failures that trigger rollback, repair, retry or fallback
- failed nodes that block their dependents with a recorded decision
- change requests that re-plan only the affected nodes

A hard-coded prompt sequence can do none of that.

**Q4. How do you stop the model from doing damage?**
Least privilege. The model gets no tools: it runs with `--tools ""`, no MCP, no hooks, in an empty folder, and anything
that would prompt for permission is denied. The prompt goes in on stdin. The only thing the model can return is JSON that
matches a schema. Everything it proposes is checked by:
- the path scope guard
- policies (secrets, PII, required controls)
- artifact permissions per node
- a real build and test run in a throw-away copy

Proposed code is never written to the repository; it is exported for review.

**Q5. How do you know AI-generated code works?**
Not by asking the model. The orchestrator copies the product and tests to a temp folder, applies the proposed files,
runs the real test runner and parses the exit code and per-test results. The test report is written by the orchestrator,
not the agent. It lists which proposed tests actually ran. A failure goes back to the developer as repair feedback
(compiler errors or failing assertions), within a bounded number of attempts.

**Q6. What happens if Claude is down or not logged in?**
The adapter classifies the error. Authentication, unknown model, missing CLI and budget errors are non-retryable, so the
engine skips the retries and goes straight to the rule-based fallback agent. Rate limits, timeouts and invalid output are
retryable. For developer and tester, fallback is allowed only when the model is unavailable. Otherwise "no change" could
make failing code look like a pass.

**Q7. Walk me through a failure.**
1. The agent throws, or a gate, policy or sandbox rejects its output.
2. The context is restored to the snapshot (state `RolledBack`).
3. Retry with back-off, passing the failure text as `PreviousFailure`.
4. If retries run out, the fallback agent tries (if allowed).
5. If that also fails: the node is `Failed`, its dependents are `Blocked` with a recorded decision, independent branches
   finish, and the run ends `Failed`.

A critical policy violation is different: no retry, run `SafeStopped`.

**Q8. What are the task states and who changes them?**
Pending, Ready, Running, Validating, WaitingForApproval, RolledBack, Retrying, Succeeded, Failed, Blocked, Cancelled,
Skipped, Stale, SafeStopped. Only the engine changes them, through `NodeStateMachine`. An illegal transition throws, and
the tests check legal and illegal transitions.

**Q9. Gates vs policies vs validation?**
- Gates are functions over the run context: the entry gate is a precondition, the exit gate checks output shape and coverage.
- Policies are cross-cutting guardrails with severities: Warning logs, High fails the node, Critical safe-stops.
- Validation is orchestrator-owned execution (the sandbox) that produces evidence artifacts.

**Q10. Explain re-planning.**
A reviewer answers "request changes on node X". The engine:
1. stores the change request
2. marks X and its transitive dependents stale and re-queues them
3. keeps unaffected branches
4. bumps the plan version, within a re-plan budget

The planner must echo design change requests verbatim and docs must quote review notes, so the gates can check that the
feedback was applied.

**Q11. How is ambiguity handled?**
The planner lists at most 3 questions, and only where the interpretation changes the design. Otherwise it records an
explicit assumption. The exit gate pauses for the human, the answers are stored, and the planner re-runs. A question is
never asked twice: an unanswered question becomes an "Assumed (no answer)" assumption. Clarification rounds are bounded.

**Q12. How is it observable?**
- Append-only audit log, where each entry hashes the previous one (tamper-evident) and PII is redacted.
- `state.json`, rewritten atomically on every transition.
- Decision lineage: inputs, outputs and rationale for every decision.
- Model invocations, without prompts.
- `out/runs-index.jsonl`: one line per run. `--metrics` computes run success rate, retry and rollback frequency, MTTR
  and latency over a stated measurement window, from recorded data only.

**Q13. Why Claude Code instead of calling the API directly?**
It reuses the developer's existing login (no API key in the app), and gives JSON-schema-validated output, per-call budget
caps and a hardened headless mode out of the box. All model-specific code sits behind `IModelClient`, so switching to
the HTTP API is one new class.

**Q14. How did you test something non-deterministic?**
- Most tests use `FakeModelClient`, which returns canned schema-valid answers per role. They cover: the full AI-mode workflow,
  the repair loop, fallback on outage, scope rejection, prompt-injection framing, and the model not being allowed to skip
  change control.
- Adapter tests use a fake process runner to cover error classification and the exact CLI flags.
- One test runs the real sandbox.
- The live model is checked by `--check-model` and the scenario runs.
- 109 automated tests in total (37 product + 72 orchestrator).

**Q15. What is the weakest part?**
- Output quality varies between runs: the gates catch bad output, but a run can need repairs or end `Failed`.
- Impact analysis is heuristic plus model judgement (production: Roslyn).
- Runs cannot be resumed after a crash (state is persisted, but replay is not implemented).
- Approval is scripted or on the console (production: a pull-request flow).
- Proposed code is exported, not applied; applying it would need an approval-gated apply step.

**Q16. Product design questions (URL shortener)**
- **Codes:** Base62 of a scrambled sequential id. A bijection, so collision-free and not enumerable; at most 7 characters.
- **Fast redirects:** clicks go to a bounded queue processed in the background; when full, a click is dropped and counted.
- **Security:** http/https only, no credentials, private and metadata hosts blocked, size limits, per-client rate limit
  with Retry-After, owner-only deactivation, safe headers.
- **Scaling:** SQL behind the repository interface, Redis for rate limits and click aggregation, a cache on the redirect
  path, multiple instances.

**Q17. Tell me about a bug you found.**
The automated AI acceptance run failed the ambiguous scenario at release: "Documentation generated" was not satisfied,
although the docs stage had succeeded. The audit trail showed why. `docs` ran in parallel with `implementation`;
implementation failed afterwards, and its rollback restored the *entire* shared context to a snapshot taken before docs
finished, which erased the docs artifact. Deterministic tests never hit that interleaving; AI timing did. The fix scopes
rollback to the artifacts the failing node wrote. That is exact, because each node may only write its declared artifacts.
A regression test reproduces the interleaving. The governance layer did its job: it refused to release instead of
shipping without docs.

**Q18. How is it tested without anyone running commands by hand?**
`./run-tests.ps1` (or `run-tests.sh` on Linux) builds and runs the 37 product tests, the 72 engine tests, and the
acceptance suite: all four scenarios end to end, checked on their evidence, plus a live smoke test of the URL shortener.
It writes JUnit XML for the CI dashboard and exits non-zero on any failure. CI runs it rule-based on every push (about
1 minute, no secrets) and with real Claude agents nightly as SIT. Without Claude Code it automatically falls back to
rule-based agents, unless `-RequireAi` says AI is mandatory.
