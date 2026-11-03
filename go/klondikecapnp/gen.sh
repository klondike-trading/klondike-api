#!/bin/sh
# Regenerates klondike_api.capnp.go from the schema (../../schema/klondike_api.capnp).
# The Go annotations are added to a temporary copy so the schema stays language neutral.
# Needs capnp and capnpc-go (go install capnproto.org/go/capnp/v3/capnpc-go@v3.1.0-alpha.2).
set -eu
cd "$(dirname "$0")"
STD="$(go list -m -f '{{.Dir}}' capnproto.org/go/capnp/v3)/std"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
{
  head -n 1 ../../schema/klondike_api.capnp
  echo 'using Go = import "/go.capnp";'
  echo '$Go.package("klondikecapnp");'
  echo '$Go.import("github.com/klondike-trading/klondike-api/go/klondikecapnp");'
  tail -n +2 ../../schema/klondike_api.capnp
} > "$TMP/klondike_api.capnp"
capnp compile -I"$STD" --src-prefix="$TMP" -o"$(go env GOPATH)/bin/capnpc-go:." "$TMP/klondike_api.capnp"
