# Testing approach

Every test is automated. One command runs everything. It exits `0` only if every stage passed and writes JUnit XML that
any CI server can display.

```bat
run-tests            :: Command Prompt: auto (AI if available)
run-tests rules      :: Command Prompt: free, about 1 min
run-tests ai         :: Command Prompt: real Claude agents required
```
```powershell
./run-tests.ps1                    # Windows. AI agents if Claude Code is installed and healthy, else rule-based
./run-tests.ps1 -Agents rules      # deterministic, free: the gate for every commit
./run-tests.ps1 -Agents ai -RequireAi   # SIT: real Claude agents, fail if Claude is not usable
```
```bash
./run-tests.sh                     # Linux/macOS build agents (AGENTS=rules|ai|auto, REQUIRE_AI=1, NO_EXEC=1)
```

The scripts build into `.build/`, so they work while the app is running and never touch developer build folders.
Results go to `TestResults/`.

## The four stages

| Stage | What it proves | How | Report |
|---|---|---|---|
| 1. build | The solution compiles with no errors | `dotnet build` | console |
| 2. product tests (37) | The URL shortener works | Unit tests + in-process HTTP integration tests (real Kestrel on a random port, fake clock) | `TestResults/product.junit.xml` |
| 3. engine tests (72) | Every orchestration control works, including its failure path | Fake model client (deterministic, free); one real sandbox build | `TestResults/engine.junit.xml` |
| 4. acceptance (SIT) | The system meets the assignment end to end | Runs greenfield, brownfield, ambiguous and safe-stop; checks the **evidence** each run leaves; product smoke test | `TestResults/acceptance/acceptance.junit.xml`, `acceptance-report.md`, `acceptance-report.json` |

## Acceptance suite (stage 4) in detail

`tests/Agentic.Acceptance` is a separate program. It does not trust a run's own "Completed". It opens the artifacts,
audit trail, decision lineage and metrics, and checks the requirements of the assignment.

**Every completed scenario**:
- status as expected; audit hash chain valid; a decision recorded for every stage; metrics recorded
- every stage succeeded; requirement normalised (no open ambiguity, at least 2 testable criteria each)
- task plan is a valid dependency graph covering every requirement
- required security controls designed; OpenAPI and schema produced
- proposals not applied; code built in the sandbox; tests executed by the real runner and passed (exit code 0)
- every proposed test actually ran; release checklist satisfied
- engineering summary has the plan, artifacts, risks, assumptions and limitations sections
- a human approved the release
- independent stages ran in parallel; security waited for both branches
- AI mode: the model was used and AI agents produced artifacts. Rules mode: zero model calls.

**Scenario specific**:

| Scenario | Additional checks |
|---|---|
| greenfield | Injected failure retried and rolled back; MTTR measured; reviewer comment re-planned only the affected branch; the review note appears in the regenerated docs |
| brownfield | Impact analysis names real files; schema change detected and approved by a human; change request re-planned from design and was applied; failing docs agent replaced by its fallback; AI mode: real code proposed |
| ambiguous | Ambiguity paused the run; answers became explicit assumptions; vague wording became measurable NFRs; **each answer matched its question** |
| safe-stop | Critical policy stopped the run; unsafe output rolled back; violation not retried; nothing released |

**Whole suite**:
- The repository is **unchanged**: a SHA-256 fingerprint of the product and tests is taken before and after, which proves agents only propose.
- **Product smoke test** on a live instance: health; shorten a link; the short link redirects (302) to the original;
  click analytics recorded; metadata; custom alias and duplicate (201 then 409); unsafe URLs rejected (400); unknown code (404).
- AI mode only: a model health check, and with `-RequireAi` the run fails if Claude is not usable.

**Flaky AI answers**: model output varies. In AI mode a failed scenario is re-run once. If it then passes, the report
records a `FLAKY: needed a re-run` check, so it is visible rather than hidden. In rule-based mode there are no re-runs.

## Running on a SIT / CI environment

| Need | Rule-based (every commit) | AI (SIT, nightly) |
|---|---|---|
| .NET 8 SDK | yes | yes |
| Claude Code | no | yes: `npm install -g @anthropic-ai/claude-code` |
| Credentials | none | `ANTHROPIC_API_KEY` as a pipeline secret (Claude Code reads it), or a logged-in Claude Code |
| Network | none (NuGet not needed) | Anthropic API |
| Duration / cost | about 1 min / $0 | about 25-35 min / about $3-5 |

`.github/workflows/ci.yml` implements both: job `verify` runs on every push (rule-based), and job `sit-ai` runs nightly
or on demand with real AI. For Azure DevOps, Jenkins or GitLab, call the same script and publish `TestResults/**/*.junit.xml`.

**Without Claude Code**, the default `auto` mode detects this and runs rule-based agents, stating why in the console
and the report. Everything still passes; AI-specific checks are simply not applicable. With `-RequireAi`, the absence
of Claude is reported as a failure.

## What is not tested

- Load and latency: NFRs such as "p95 < 50 ms at 500 req/s" are recorded but there is no load test.
- AI output quality beyond the gates: the checks prove the output is safe, consistent and passes the real tests. They
  do not prove it is the best possible design.
- Multi-instance behaviour of the product (in-memory storage and rate limiting are single-instance by design).
