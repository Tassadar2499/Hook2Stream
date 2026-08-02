import assert from "node:assert/strict";
import test from "node:test";
import {
  joinApiUrl,
  normalizeApiBaseUrl,
} from "../src/lib/api-url.ts";

test("an omitted API base URL keeps browser requests on the current origin", () => {
  assert.equal(normalizeApiBaseUrl(), "");
  assert.equal(normalizeApiBaseUrl("   "), "");
  assert.equal(joinApiUrl("", "/api/v1/account/me"), "/api/v1/account/me");
});

test("an explicit local API origin remains supported", () => {
  assert.equal(
    joinApiUrl(" http://localhost:5000/// ", "/api/v1/auth/session"),
    "http://localhost:5000/api/v1/auth/session",
  );
});

test("API paths must be origin-relative", () => {
  assert.throws(
    () => joinApiUrl("", "api/v1/account/me"),
    /must start with '\/'/,
  );
});
