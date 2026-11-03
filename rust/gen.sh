#!/bin/sh
# Regenerates src/klondike_api_capnp.rs from the schema of the repository
# (../schema/klondike_api.capnp). The generated code is committed so that users
# of the crate do not need the Cap'n Proto compiler.
#
# Needs capnp (https://capnproto.org/install.html) and capnpc-rust, of the same
# minor version as the `capnp` dependency of Cargo.toml:
#   cargo install capnpc --version 0.27.0 --locked
#
# The schema is copied to schema/klondike_api.capnp: the test
# `generated_code_matches_schema` fails when that copy differs from
# ../schema/klondike_api.capnp, i.e. when this script must be run again.
set -eu
cd "$(dirname "$0")"
cp ../schema/klondike_api.capnp schema/klondike_api.capnp
capnp compile --src-prefix=schema -orust:src schema/klondike_api.capnp
rustfmt --edition 2021 src/klondike_api_capnp.rs
