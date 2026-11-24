#!/bin/sh
# Builds everything and runs the self-test in a scratch folder (never the real library).
#   ./smoke.sh [scratch-dir]        prints what failed and the verdict; screenshots land in <scratch-dir>/out
set -e
here="$(cd "$(dirname "$0")" && pwd)"
scratch="${1:-${TMPDIR:-/tmp}/phoenix-smoke}"
rm -rf "$scratch"
mkdir -p "$scratch"
cd "$here"
dotnet build backend -c Debug -v q 2>&1 | grep -E "error|Build succeeded" | sort -u
npx tsc --noEmit -p .
node build.mjs >/dev/null
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) data="$(cygpath -w "$scratch/data")"; out="$(cygpath -w "$scratch/out")"; electron=./node_modules/electron/dist/electron.exe ;;
  Darwin) data="$scratch/data"; out="$scratch/out"; electron=./node_modules/electron/dist/Electron.app/Contents/MacOS/Electron ;;
  *) data="$scratch/data"; out="$scratch/out"; electron=./node_modules/electron/dist/electron ;;
esac
set +e
PHOENIX_DATA="$data" "$electron" . --smoke "$out"
code=$?
grep -v "^PASS" "$scratch/out/smoke.log"
echo "checks passed: $(grep -c '^PASS' "$scratch/out/smoke.log")   exit code: $code"
exit $code
