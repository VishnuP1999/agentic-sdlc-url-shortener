#!/usr/bin/env bash
# One command to verify everything (Linux/macOS build agents): build, unit + integration tests, and the
# end-to-end acceptance (SIT) suite. JUnit XML is written under TestResults/. Exit code 0 only if all stages pass.
#
#   ./run-tests.sh                         # agents=auto: Claude if installed and healthy, else rule-based
#   AGENTS=rules ./run-tests.sh            # deterministic CI gate, no model calls
#   AGENTS=ai REQUIRE_AI=1 ./run-tests.sh  # SIT: fail if Claude cannot be used
#   NO_EXEC=1 ./run-tests.sh               # skip sandbox builds inside acceptance (results marked unverified)
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
RESULTS="$ROOT/TestResults"
BUILD="$ROOT/.build"   # isolated build output (works while the app is running)
AGENTS="${AGENTS:-auto}"
SCENARIOS="${SCENARIOS:-greenfield,brownfield,ambiguous,safe-stop}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$RESULTS"

failed=0
summary=""
stage() {
  local name="$1"; shift
  echo; echo "==== $name ===="
  local start=$SECONDS
  if "$@"; then summary+="PASS  $name ($((SECONDS - start))s)"$'\n'
  else summary+="FAIL  $name ($((SECONDS - start))s)"$'\n'; failed=$((failed + 1)); fi
}

stage "build" dotnet build "$ROOT/AgenticSdlc.sln" -nologo -v q --artifacts-path "$BUILD"
if [ "$failed" -eq 0 ]; then
  stage "product tests" dotnet run --project "$ROOT/tests/UrlShortener.Tests" --no-build --artifacts-path "$BUILD" -- --junit "$RESULTS/product.junit.xml"
  stage "engine tests" dotnet run --project "$ROOT/tests/Agentic.Tests" --no-build --artifacts-path "$BUILD" -- --junit "$RESULTS/engine.junit.xml"
  args=(--agents "$AGENTS" --scenarios "$SCENARIOS" --out "$RESULTS/acceptance")
  [ -n "${REQUIRE_AI:-}" ] && args+=(--require-ai)
  [ -n "${NO_EXEC:-}" ] && args+=(--no-exec)
  stage "acceptance" dotnet run --project "$ROOT/tests/Agentic.Acceptance" --no-build --artifacts-path "$BUILD" -- "${args[@]}"
fi

echo; echo "==== summary ===="; printf "%s" "$summary"
echo "JUnit reports: $RESULTS"
echo "Acceptance report: $RESULTS/acceptance/acceptance-report.md"
[ "$failed" -eq 0 ] && { echo "ALL STAGES PASSED"; exit 0; }
echo "$failed STAGE(S) FAILED"; exit 1
