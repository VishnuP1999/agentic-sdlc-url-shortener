# Automated SIT run with real Claude agents: PASSED 92/92

Command: `./run-tests.ps1 -Agents ai -RequireAi` (2026-10-10). Every stage passed:

| Stage | Result |
|---|---|
| build | pass |
| product tests | 37/37 |
| engine tests | 68/68 |
| acceptance | 92/92 checks |

Total model cost: about $3.00. The per-run folders referenced in the report stayed on the build machine (`TestResults/` is
not committed); the reports here are the evidence.

**Brownfield latency (1528 s):** the laptop entered Windows Modern Standby from 16:46:31 to 17:04:05 local time, during
the developer's model call (Windows System log, Kernel-Power events 506/507). Timeouts correctly do not count time spent
asleep; wall-clock latency does. Active time was about 8 minutes. On a SIT server, disable sleep.
