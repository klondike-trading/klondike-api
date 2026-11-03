#!/bin/sh
# Regenerates src/capnp/klondike_api.ts from the schema (../schema/klondike_api.capnp)
# with capnp-es. Needs the capnp tool (https://capnproto.org/install.html) and
# `npm install`. An optional argument writes to another directory (the tests
# use it to check that the committed bindings are up to date).
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/src/capnp}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
cd "$ROOT"
# capnp-es writes next to the working directory when it reads the request on stdin.
capnp compile -o- --src-prefix=../schema ../schema/klondike_api.capnp > "$OUT/.request.bin"
(cd "$OUT" && node "$ROOT/node_modules/capnp-es/dist/compiler/capnpc-ts.mjs" < .request.bin > /dev/null)
rm -f "$OUT/.request.bin"
