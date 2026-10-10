<#
.SYNOPSIS
  One command to verify everything: build, unit + integration tests, and the end-to-end acceptance (SIT) suite.

.DESCRIPTION
  Stages (each writes JUnit XML under TestResults/ for the CI dashboard):
    1. build      dotnet build of the whole solution
    2. product    URL shortener unit + in-process HTTP integration tests
    3. engine     orchestrator, state machine, AI agents (fake model), model adapter, sandbox, scenarios
    4. acceptance greenfield, brownfield, ambiguous and safe-stop end to end, checked on their evidence,
                  plus a live smoke test of the URL shortener (shorten -> redirect -> analytics)
  Exit code 0 only if every stage passed.

.PARAMETER Agents
  auto (default): Claude if Claude Code is installed and healthy, else rule-based.
  ai: always Claude.  rules: deterministic, no model calls, no cost (recommended for CI on every commit).

.PARAMETER RequireAi
  Fail the run if Claude cannot be used (use on the SIT environment where AI must be exercised).

.PARAMETER NoExec
  Do not build/run proposals in the sandbox during acceptance (faster, results marked unverified).

.EXAMPLE
  ./run-tests.ps1                      # everything, AI if available
  ./run-tests.ps1 -Agents rules        # deterministic CI gate
  ./run-tests.ps1 -RequireAi           # SIT: AI must work
#>
param(
    [ValidateSet("auto", "ai", "rules")] [string] $Agents = "auto",
    [switch] $RequireAi,
    [switch] $NoExec,
    [string] $Scenarios = "greenfield,brownfield,ambiguous,safe-stop"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$results = Join-Path $root "TestResults"
# isolated build output: works while the app is running and never touches the developer build folders
$build = Join-Path $root ".build"
New-Item -ItemType Directory -Force $results | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$stages = [ordered]@{}
function Run-Stage([string] $name, [scriptblock] $command) {
    Write-Host ""
    Write-Host "==== $name ====" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $command | Write-Host   # keep tool output out of the return value, but still capturable by CI logs (*>)
    $code = $LASTEXITCODE
    $stages[$name] = [pscustomobject]@{ Stage = $name; Result = $(if ($code -eq 0) { "PASS" } else { "FAIL" }); Seconds = [int]$clock.Elapsed.TotalSeconds }
    return $code
}

$failed = 0
$failed += [int]((Run-Stage "build" { dotnet build (Join-Path $root "AgenticSdlc.sln") -nologo -v q --artifacts-path $build }) -ne 0)
if ($failed -eq 0) {
    $failed += [int]((Run-Stage "product tests" { dotnet run --project (Join-Path $root "tests/UrlShortener.Tests") --no-build --artifacts-path $build -- --junit (Join-Path $results "product.junit.xml") }) -ne 0)
    $failed += [int]((Run-Stage "engine tests" { dotnet run --project (Join-Path $root "tests/Agentic.Tests") --no-build --artifacts-path $build -- --junit (Join-Path $results "engine.junit.xml") }) -ne 0)

    $acceptanceArgs = @("--agents", $Agents, "--scenarios", $Scenarios, "--out", (Join-Path $results "acceptance"))
    if ($RequireAi) { $acceptanceArgs += "--require-ai" }
    if ($NoExec) { $acceptanceArgs += "--no-exec" }
    $failed += [int]((Run-Stage "acceptance" { dotnet run --project (Join-Path $root "tests/Agentic.Acceptance") --no-build --artifacts-path $build -- @acceptanceArgs }) -ne 0)
}

Write-Host ""
Write-Host "==== summary ====" -ForegroundColor Cyan
$stages.Values | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "JUnit reports: $results"
Write-Host "Acceptance report: $(Join-Path $results 'acceptance/acceptance-report.md')"
if ($failed -eq 0) { Write-Host "ALL STAGES PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$failed STAGE(S) FAILED" -ForegroundColor Red
exit 1
