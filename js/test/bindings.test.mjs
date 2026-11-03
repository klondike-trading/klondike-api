// src/capnp/klondike_api.ts is generated from ../schema/klondike_api.capnp
// (npm run gen:capnp): it must be up to date with the schema.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const committed = readFileSync(join(root, "src/capnp/klondike_api.ts"), "utf8");
const schema = readFileSync(join(root, "../schema/klondike_api.capnp"), "utf8");
const hasCapnp = spawnSync("capnp", ["--version"]).status === 0;

test("bindings have the id of the schema", () => {
  const id = schema.match(/^@(0x[0-9a-f]+);/m)[1];
  assert.match(committed, new RegExp(`_capnpFileId = ${id}n;`));
});

test("bindings are those generated from the schema", { skip: !hasCapnp && "capnp is not installed" }, () => {
  const out = mkdtempSync(join(tmpdir(), "klondike-capnp-"));
  try {
    execFileSync("sh", [join(root, "scripts/gen-capnp.sh"), out]);
    const generated = readFileSync(join(out, "klondike_api.ts"), "utf8");
    assert.ok(generated === committed, "src/capnp/klondike_api.ts is stale: run npm run gen:capnp");
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
});
