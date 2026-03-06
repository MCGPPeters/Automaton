#!/usr/bin/env bash
# Builds the Abies WASM benchmark and runs it against js-framework-benchmark.
#
# Usage:
#   ./scripts/run-benchmark.sh                      # Build only
#   ./scripts/run-benchmark.sh --run [benchmark]    # Build + run benchmarks
#   ./scripts/run-benchmark.sh --run 01_run1k       # Build + run specific benchmark
#
# Prerequisites:
#   - .NET 10 SDK with wasm-tools workload
#   - Node.js (for js-framework-benchmark server)
#   - Chrome/Chromium (for webdriver-ts)
#
# First-time setup:
#   cd js-framework-benchmark && npm ci
#   cd webdriver-ts && npm ci

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BENCH_DIR="$ROOT_DIR/js-framework-benchmark"
ABIES_SRC="$BENCH_DIR/frameworks/keyed/abies/src"
FRAMEWORK_NAME="abies-v2.0.0-keyed"

echo "=== Building Abies WASM Benchmark ==="
echo "Cleaning previous build..."
rm -rf "$ABIES_SRC/bin" "$ABIES_SRC/obj"

echo "Publishing (Release)..."
dotnet publish "$ABIES_SRC/AbiesBenchmark.csproj" -c Release --nologo -v quiet

echo "✅ Published to $(dirname "$ABIES_SRC")/bundled-dist/wwwroot/"

if [[ "${1:-}" != "--run" ]]; then
    echo ""
    echo "To run benchmarks, use: $0 --run [benchmark_name]"
    echo "Example: $0 --run 01_run1k"
    exit 0
fi

BENCHMARK="${2:-}"

echo ""
echo "=== Starting js-framework-benchmark server ==="
cd "$BENCH_DIR"

# Start server in background if not already running
if ! curl -s http://localhost:8080 > /dev/null 2>&1; then
    npm start &
    SERVER_PID=$!
    echo "Server started (PID: $SERVER_PID) on http://localhost:8080"
    # Wait for server to be ready
    for i in {1..30}; do
        if curl -s http://localhost:8080 > /dev/null 2>&1; then
            break
        fi
        sleep 1
    done
else
    SERVER_PID=""
    echo "Server already running on http://localhost:8080"
fi

echo ""
echo "=== Running Benchmarks ==="
cd "$BENCH_DIR/webdriver-ts"

if [[ -n "$BENCHMARK" ]]; then
    echo "Running benchmark: $BENCHMARK"
    npm run bench -- --headless --framework "$FRAMEWORK_NAME" --benchmark "$BENCHMARK"
else
    echo "Running all benchmarks for $FRAMEWORK_NAME"
    npm run bench -- --headless --framework "$FRAMEWORK_NAME"
fi

echo ""
echo "=== Results ==="
ls -la "$BENCH_DIR/webdriver-ts/results/${FRAMEWORK_NAME}"*.json 2>/dev/null || echo "No result files found"

# Cleanup
if [[ -n "${SERVER_PID:-}" ]]; then
    echo ""
    echo "Stopping server (PID: $SERVER_PID)..."
    kill "$SERVER_PID" 2>/dev/null || true
fi

echo "✅ Done"
