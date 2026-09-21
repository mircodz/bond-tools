#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null; then
  echo "dotnet missing" >&2
  exit 1
fi

cd "$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

out_dir="out/test-results/coverage"
report_dir="out/coverage-report"
rm -rf "$out_dir" "$report_dir"
mkdir -p "$out_dir"

dotnet test Bond.Parser.Tests/Bond.Parser.Tests.csproj \
  --collect:"XPlat Code Coverage" \
  --results-directory:"$out_dir"

# Find the coverage file (XPlat creates it in a GUID subdirectory)
coverage_file=$(find "$out_dir" -name "coverage.cobertura.xml" | head -n 1)

if [ -z "$coverage_file" ]; then
  echo "Error: Coverage file not generated" >&2
  exit 1
fi

echo "Coverage file: $coverage_file"

mkdir -p "$report_dir"
dotnet tool update -g dotnet-reportgenerator-globaltool >/dev/null 2>&1 || true

reportgenerator \
  -reports:"$coverage_file" \
  -targetdir:"$report_dir" \
  -reporttypes:Html

index="$report_dir/index.html"
echo "Report: $index"

if [ -f "$index" ]; then
  if command -v xdg-open >/dev/null; then
    xdg-open "$index" >/dev/null 2>&1 &
  elif command -v open >/dev/null; then
    open "$index" >/dev/null 2>&1 &
  fi
fi