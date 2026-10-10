# Automated SIT run with real Claude agents: PASSED 93/93

Command: `run-tests ai` (2026-10-10, 22.4 min). Every stage passed:

| Stage | Result |
|---|---|
| build | pass |
| product tests | 37/37 |
| engine tests | 70/70 |
| acceptance | 93/93 checks |

**Scenarios**:

| Scenario | Result |
|---|---|
| greenfield | Completed |
| brownfield | Completed on the automatic re-run (reported as `FLAKY: needed a re-run`) |
| ambiguous | Completed |
| safe-stop | SafeStopped, as designed |

The URL shortener smoke test passed 8/8. Model cost: $2.85. The per-run folders referenced in the report stayed on the
build machine (`TestResults/` is not committed); the reports here are the evidence.

**Why brownfield needed a re-run:** on the first attempt the model wrote security controls with explanations
(`"url-scheme-allowlist: existing UrlValidator stays in force..."`) instead of bare ids. The design-stage policy skipped the
check because that change did not touch `POST /api/urls`, so only the release checklist caught it, too late to repair, and
the release was blocked. The second attempt passed. This build predates commit `eda588f`, which fixes this:
- the controls policy now applies to every design
- the schema requires bare ids
- "id: explanation" answers are normalised

Every failure seen in live AI runs is in [ENGINEERING_SUMMARY.md](../../ENGINEERING_SUMMARY.md). In each case the
system blocked the release instead of shipping something unverified.
