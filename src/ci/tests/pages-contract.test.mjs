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
const apiSettings = JSON.parse(read("src/Hook2Stream.Api/appsettings.json"));
const apiEndpoints = read("src/Hook2Stream.Api/Endpoints.cs");
const onboarding = read("src/web/src/app/onboarding/onboarding-client.tsx");
const appHome = read("src/web/src/app/page.tsx");
const cname = read("site/CNAME").trim();
const index = read("site/index.html");
const privacy = read("site/privacy/index.html");
const terms = read("site/terms/index.html");
const notFound = read("site/404.html");
const styles = read("site/styles.css");

assert.equal(cname, "www.hook2stream.com", "Pages must own exactly the www hostname");
assert.match(index, /<link rel="canonical" href="https:\/\/www\.hook2stream\.com\/" \/>/);
assert.match(index, /href="https:\/\/hook2stream\.com\/"/);
assert.match(index, /href="\.\/styles\.css"/);
assert.match(index, /href="\.\/privacy\/"/);
assert.match(index, /href="\.\/terms\/"/);
assert.match(index, /uses Google sign-in only to authenticate invited users/);
assert.match(index, /production checkout is temporarily unavailable/);
assert.match(privacy, /<link rel="canonical" href="https:\/\/www\.hook2stream\.com\/privacy\/" \/>/);
assert.match(terms, /<link rel="canonical" href="https:\/\/www\.hook2stream\.com\/terms\/" \/>/);
for (const legalPage of [privacy, terms]) {
  assert.match(legalPage, /Effective September 4, 2026/);
  assert.match(legalPage, /href="mailto:markevich\.roma@gmail\.com"/);
  assert.match(legalPage, /href="\.\.\/styles\.css"/);
  assert.doesNotMatch(legalPage, /<script\b/i, "legal pages must remain static and script-free");
}
assert.match(privacy, /openid/);
assert.match(privacy, /OpenRouter/);
assert.match(privacy, /Storj/);
assert.match(privacy, /Production billing is disabled/);
assert.match(terms, /AGPL-3\.0-only/);
assert.match(terms, /invite-only Hook2Stream service/);
assert.doesNotMatch(privacy, /inaccessible in a backup/,
  "the policy must not claim that operator-recoverable encrypted backups are inaccessible");
assert.doesNotMatch(index, /unlock the clean files/,
  "the OAuth homepage must not promise a production purchase path while billing is disabled");
assert.doesNotMatch(appHome, /unlock the clean files/,
  "the application homepage must not promise a production purchase path while billing is disabled");

assert.equal(apiSettings.Legal.TermsVersion, "2026-09-04");
assert.equal(apiSettings.Legal.PrivacyVersion, "2026-09-04");
assert.match(apiEndpoints, /configuration\["Legal:TermsVersion"\] \?\? "2026-09-04"/);
assert.match(apiEndpoints, /configuration\["Legal:PrivacyVersion"\] \?\? "2026-09-04"/);
assert.match(compose, /Legal__TermsVersion: "2026-09-04"/);
assert.match(compose, /Legal__PrivacyVersion: "2026-09-04"/);
assert.match(onboarding, /const legalVersion = "2026-09-04"/);
assert.match(onboarding, /href="https:\/\/www\.hook2stream\.com\/terms\/"/);
assert.match(onboarding, /href="https:\/\/www\.hook2stream\.com\/privacy\/"/);
assert.doesNotMatch(onboarding, /current draft|local MVP testing/i);
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
