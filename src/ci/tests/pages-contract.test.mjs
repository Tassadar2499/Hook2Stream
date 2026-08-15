import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "../../..");
const read = (path) => readFileSync(resolve(root, path), "utf8");

const workflow = read(".github/workflows/pages.yml");
const caddy = read("src/deploy/Caddyfile.production");
const compose = read("src/deploy/compose.yaml");
const baseEnvironment = read("src/deploy/.env.example");
const productionEnvironment = read("src/deploy/environments/production.env.example");
const cname = read("site/CNAME").trim();
const index = read("site/index.html");
const notFound = read("site/404.html");
const styles = read("site/styles.css");

assert.equal(cname, "www.hook2stream.com", "Pages must own exactly the www hostname");
assert.match(index, /<link rel="canonical" href="https:\/\/www\.hook2stream\.com\/" \/>/);
assert.match(index, /href="https:\/\/hook2stream\.com\/"/);
assert.match(index, /href="\.\/styles\.css"/);
assert.match(notFound, /href="https:\/\/www\.hook2stream\.com\/"/);
assert.match(notFound, /href="\/styles\.css"/,
  "the custom 404 page must load CSS from the domain root for nested missing paths");
assert.ok(styles.length > 1_000, "the checked-in static site stylesheet must not be empty");
assert.doesNotMatch(index, /<script\b/i, "the static landing page must not execute JavaScript");
const absoluteOrigins = new Set(
  [...index.matchAll(/https?:\/\/[^"'\s<]+/gi)].map((match) => new URL(match[0]).origin),
);
assert.deepEqual(absoluteOrigins, new Set(["https://www.hook2stream.com", "https://hook2stream.com"]),
  "the landing page must not load third-party origins");

assert.doesNotMatch(caddy, /www\.hook2stream\.com/, "Caddy must not claim the Pages hostname");
assert.doesNotMatch(caddy, /redir\s+https:\/\/hook2stream\.com/, "Caddy must not redirect www");
for (const config of [compose, baseEnvironment, productionEnvironment]) {
  assert.doesNotMatch(config, /\bWWW_DOMAIN\b/, "the app bundle must not retain the retired www variable");
}

assert.match(workflow, /^on:\n  workflow_dispatch:\s*$/m,
  "Pages publishing must be an explicit manual operation");
assert.doesNotMatch(workflow, /^\s+push:\s*$/m,
  "Pages publishing must not trigger the app staging pipeline indirectly");
assert.match(workflow, /if: github\.ref == 'refs\/heads\/main'/,
  "the Pages workflow must reject dispatches from non-main refs");
assert.match(workflow, /path: site/);
assert.match(workflow, /name: github-pages/);
assert.match(workflow, /actions\/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd/,
  "Pages checkout must use the reviewed Node 24 release");
assert.match(workflow, /actions\/configure-pages@45bfe0192ca1faeb007ade9deae92b16b8254a0d/,
  "Pages configuration must use the reviewed Node 24 release");

const uses = [...workflow.matchAll(/^\s*uses:\s*([^\s#]+)(?:\s*#.*)?$/gm)].map((match) => match[1]);
assert.ok(uses.length >= 4, "Pages workflow must contain all expected official actions");
for (const action of uses) {
  assert.match(action, /^[^@\s]+@[0-9a-f]{40}$/,
    `GitHub Action must be pinned to a full commit SHA: ${action}`);
}

console.log("GitHub Pages and www domain contracts passed.");
