#!/usr/bin/python3
"""Root-owned authenticated deployment gate for staging and production.

The only stdout records are the release capability line or the strict soak
JSON record consumed by post-deploy-e2e.sh. Secrets are read from trusted files
and are never placed in command arguments, URLs, diagnostics, or persisted
state.
"""

from __future__ import annotations

import hashlib
import hmac
import http.cookiejar
import json
import os
import re
import shutil
import socket
import ssl
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from collections import deque
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, BinaryIO, Callable


STAGING_GATE = "HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview-render18-zip,stripe-test-idempotency,egress-deny"
PRODUCTION_GATE = "HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview,egress-deny"
SOAK_SCHEMA = "hook2stream-soak-hook-result-v1"
STATE_SCHEMA = "hook2stream-authenticated-e2e-state-v1"
BASELINE_SCHEMA = "hook2stream-soak-baseline-v1"
CHECKPOINT_SCHEMA = "hook2stream-authenticated-e2e-checkpoint-v1"
HOST_ROOT = Path("/srv/hook2stream")
MAX_JSON = 4 * 1024 * 1024
MAX_EXPORT = 2 * 1024 * 1024 * 1024


class GateError(RuntimeError):
    pass


def fail(message: str) -> None:
    # Callers pass only fixed labels/statuses. Never append response bodies,
    # exception reprs, URLs with query strings, or credential material here.
    print(f"authenticated E2E: {message}", file=sys.stderr)
    raise SystemExit(1)


def parse_env_file(path: Path) -> dict[str, str]:
    try:
        raw = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise GateError("environment file is unreadable") from exc
    values: dict[str, str] = {}
    for number, line in enumerate(raw.splitlines(), 1):
        if not line or line.lstrip().startswith("#"):
            continue
        if "=" not in line:
            raise GateError(f"environment file line {number} is malformed")
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Z][A-Z0-9_]*", key) or key in values:
            raise GateError(f"environment file line {number} is duplicate or unsafe")
        if "\x00" in value or "\r" in value:
            raise GateError(f"environment file line {number} contains forbidden bytes")
        values[key] = value
    return values


def required(config: dict[str, str], name: str) -> str:
    value = config.get(name, "")
    if not value:
        raise GateError(f"{name} is required")
    return value


def trusted_file(path_text: str, modes: set[int], max_bytes: int, label: str) -> Path:
    path = Path(path_text)
    if not path.is_absolute():
        raise GateError(f"{label} path must be absolute")
    try:
        info = path.lstat()
        resolved = path.resolve(strict=True)
    except OSError as exc:
        raise GateError(f"{label} is unavailable") from exc
    if resolved != path or stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise GateError(f"{label} must be a regular non-symlink file")
    if info.st_uid != 0 or stat.S_IMODE(info.st_mode) not in modes:
        raise GateError(f"{label} ownership or mode is unsafe")
    if info.st_size <= 0 or info.st_size > max_bytes:
        raise GateError(f"{label} size is outside the accepted bound")
    parent = path.parent
    while parent != parent.parent:
        parent_info = parent.lstat()
        if stat.S_ISLNK(parent_info.st_mode) or parent_info.st_mode & 0o022:
            raise GateError(f"{label} parent path is writable or linked")
        if parent == HOST_ROOT:
            break
        parent = parent.parent
    return path


def private_e2e_file(config: dict[str, str], name: str, max_bytes: int, label: str) -> Path:
    path = trusted_file(required(config, name), {0o400, 0o600}, max_bytes, label)
    try:
        path.relative_to(HOST_ROOT / "e2e")
    except ValueError as exc:
        raise GateError(f"{label} must be below the encrypted E2E directory") from exc
    return path


def scalar(path: Path, label: str, max_bytes: int = 16_384) -> str:
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
        with os.fdopen(descriptor, "rb") as stream:
            value = stream.read(max_bytes + 1)
    except OSError as exc:
        raise GateError(f"{label} could not be read safely") from exc
    if len(value) > max_bytes or b"\x00" in value or b"\r" in value:
        raise GateError(f"{label} is malformed")
    if value.endswith(b"\n"):
        value = value[:-1]
    if b"\n" in value:
        raise GateError(f"{label} must contain one scalar")
    try:
        decoded = value.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise GateError(f"{label} is not UTF-8") from exc
    if not decoded:
        raise GateError(f"{label} is empty")
    return decoded


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        return None


@dataclass
class Response:
    status: int
    headers: Any
    body: bytes


class ApiClient:
    def __init__(self, origin: str, auth_kind: str, auth_file: Path):
        parsed = urllib.parse.urlsplit(origin)
        if parsed.scheme != "https" or parsed.query or parsed.fragment or parsed.path not in ("", "/"):
            raise GateError("PUBLIC_ORIGIN must be an HTTPS origin")
        self.origin = origin.rstrip("/")
        self.auth_kind = auth_kind
        self.bearer: str | None = None
        self.csrf: str | None = None
        self.file_csrf: str | None = None
        handlers: list[Any] = [
            urllib.request.ProxyHandler({}),
            NoRedirect(),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()),
        ]
        if auth_kind == "oauth-session":
            jar = http.cookiejar.MozillaCookieJar()
            try:
                jar.load(str(auth_file), ignore_discard=True, ignore_expires=False)
            except (OSError, http.cookiejar.LoadError) as exc:
                raise GateError("OAuth session cookie file is invalid or expired") from exc
            cookies = list(jar)
            host = parsed.hostname or ""
            allowed_names = {"__Host-h2s_session", "__Host-h2s_csrf"}
            if (
                len(cookies) != 2
                or {cookie.name for cookie in cookies} != allowed_names
                or any(
                    cookie.name not in allowed_names
                    or cookie.domain.lstrip(".") != host
                    or cookie.domain_specified
                    or cookie.domain_initial_dot
                    or cookie.path != "/"
                    or not cookie.secure
                    for cookie in cookies
                )
            ):
                raise GateError("OAuth cookie jar must contain the secure same-origin Hook2Stream session and CSRF cookies")
            self.file_csrf = next(cookie.value for cookie in cookies if cookie.name == "__Host-h2s_csrf")
            handlers.insert(0, urllib.request.HTTPCookieProcessor(jar))
        elif auth_kind == "bearer-token":
            self.bearer = scalar(auth_file, "E2E bearer token", 16_384)
        else:
            raise GateError("HOOK2STREAM_E2E_AUTH_KIND must be oauth-session or bearer-token")
        self.opener = urllib.request.build_opener(*handlers)

    def _url(self, path: str) -> str:
        if not path.startswith("/") or path.startswith("//") or "#" in path:
            raise GateError("internal request path is unsafe")
        url = self.origin + path
        if urllib.parse.urlsplit(url).netloc != urllib.parse.urlsplit(self.origin).netloc:
            raise GateError("internal request escaped the configured origin")
        return url

    def request(
        self,
        method: str,
        path: str,
        expected: set[int],
        *,
        payload: Any | None = None,
        data_file: Path | None = None,
        headers: dict[str, str] | None = None,
        timeout: int = 60,
        max_body: int = MAX_JSON,
    ) -> Response:
        request_headers = {"Accept": "application/json", "User-Agent": "hook2stream-deploy-e2e/1"}
        if self.bearer is not None:
            request_headers["Authorization"] = f"Bearer {self.bearer}"
        if method not in {"GET", "HEAD", "OPTIONS"} and self.csrf is not None:
            request_headers["X-CSRF-Token"] = self.csrf
        if headers:
            request_headers.update(headers)
        body: bytes | BinaryIO | None = None
        opened: BinaryIO | None = None
        if payload is not None:
            body = json.dumps(payload, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
            request_headers["Content-Type"] = "application/json"
            request_headers["Content-Length"] = str(len(body))
        elif data_file is not None:
            opened = data_file.open("rb")
            body = opened
            request_headers["Content-Type"] = "application/octet-stream"
            request_headers["Content-Length"] = str(data_file.stat().st_size)
        req = urllib.request.Request(self._url(path), data=body, headers=request_headers, method=method)
        try:
            try:
                response = self.opener.open(req, timeout=timeout)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                content = response.read(max_body + 1)
                if len(content) > max_body:
                    raise GateError("HTTP response exceeded its safe body bound")
                result = Response(response.status, response.headers, content)
        except (OSError, urllib.error.URLError, TimeoutError) as exc:
            raise GateError("authenticated HTTPS request failed") from exc
        finally:
            if opened is not None:
                opened.close()
        if result.status not in expected:
            raise GateError(f"{method} endpoint returned unexpected HTTP {result.status}")
        return result

    def json(self, method: str, path: str, expected: set[int], **kwargs: Any) -> tuple[Any, Response]:
        result = self.request(method, path, expected, **kwargs)
        try:
            parsed = json.loads(result.body)
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise GateError("endpoint returned malformed JSON") from exc
        return parsed, result

    def stream_to(self, path: str, destination: Path, expected: set[int], max_bytes: int) -> Any:
        headers = {"Accept": "application/octet-stream", "User-Agent": "hook2stream-deploy-e2e/1"}
        if self.bearer is not None:
            headers["Authorization"] = f"Bearer {self.bearer}"
        req = urllib.request.Request(self._url(path), headers=headers, method="GET")
        total = 0
        try:
            try:
                response = self.opener.open(req, timeout=1800)
            except urllib.error.HTTPError as error:
                response = error
            with response, destination.open("xb") as output:
                if response.status not in expected:
                    raise GateError(f"download endpoint returned unexpected HTTP {response.status}")
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > max_bytes:
                        raise GateError("download exceeded its safe size bound")
                    output.write(chunk)
                return response.headers
        except (OSError, urllib.error.URLError, TimeoutError) as exc:
            raise GateError("authenticated download failed") from exc


def parsed_json(response: Response) -> Any:
    try:
        return json.loads(response.body)
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise GateError("endpoint returned malformed JSON") from exc


def etag(response: Response) -> str:
    value = response.headers.get("ETag", "")
    if not re.fullmatch(r'"[1-9][0-9]*"', value):
        raise GateError("endpoint omitted its canonical ETag")
    return value


def safe_uuid(value: Any, label: str) -> str:
    try:
        return str(uuid.UUID(str(value)))
    except (ValueError, TypeError, AttributeError) as exc:
        raise GateError(f"{label} is not a UUID") from exc


def idempotency(prefix: str, commit: str) -> str:
    if not re.fullmatch(r"[a-z0-9-]{1,64}", prefix):
        raise GateError("idempotency operation is invalid")
    return f"deploy-e2e:{prefix}:{commit}"


def wait_json(
    client: ApiClient,
    path: str,
    predicate: Callable[[Any], bool],
    timeout: int,
    label: str,
    statuses: set[int] = {200},
) -> tuple[Any, Response]:
    deadline = time.monotonic() + timeout
    while True:
        response = client.request("GET", path, statuses)
        value = parsed_json(response) if response.status == 200 else None
        if response.status == 200 and predicate(value):
            return value, response
        if time.monotonic() >= deadline:
            raise GateError(f"{label} did not become ready before its deadline")
        time.sleep(5)


def wait_job(client: ApiClient, job_id: str, timeout: int, label: str) -> dict[str, Any]:
    def complete(value: Any) -> bool:
        state = value.get("state") if isinstance(value, dict) else None
        if state in {"failed", "cancelled"}:
            raise GateError(f"{label} entered terminal state {state}")
        return state == "succeeded"

    value, _ = wait_json(client, f"/api/v1/jobs/{job_id}", complete, timeout, label)
    return value


def verify_auth(client: ApiClient, config: dict[str, str]) -> tuple[str, str]:
    expected_email_file = private_e2e_file(
        config, "HOOK2STREAM_E2E_EXPECTED_EMAIL_FILE", 1024, "E2E expected-email file"
    )
    expected_email = scalar(expected_email_file, "E2E expected email", 512).lower()
    if not re.fullmatch(r"[^@\s]+@[^@\s]+", expected_email):
        raise GateError("E2E expected email is invalid")

    if client.auth_kind == "oauth-session":
        session, _ = client.json("GET", "/api/v1/auth/session", {200})
        if (
            not isinstance(session, dict)
            or session.get("authenticated") is not True
            or str(session.get("email", "")).lower() != expected_email
            or not isinstance(session.get("csrfToken"), str)
            or len(session["csrfToken"]) < 32
            or not hmac.compare_digest(session["csrfToken"], client.file_csrf or "")
        ):
            raise GateError("pre-issued OAuth session is unauthenticated, wrong-account, or missing CSRF")
        client.csrf = session["csrfToken"]

    account, _ = client.json("GET", "/api/v1/account/me", {200})
    if (
        not isinstance(account, dict)
        or str(account.get("email", "")).lower() != expected_email
        or account.get("onboardingRequired") is not False
    ):
        raise GateError("E2E identity is wrong or its dedicated workspace is not onboarded")
    workspace_id = safe_uuid(account.get("workspaceId"), "workspace ID")

    login = client.request("GET", "/api/v1/auth/login?returnPath=%2Fdashboard", {302, 303})
    location = login.headers.get("Location", "")
    parsed = urllib.parse.urlsplit(location)
    query = urllib.parse.parse_qs(parsed.query)
    if (
        parsed.scheme != "https"
        or parsed.hostname != "accounts.google.com"
        or not parsed.path.startswith("/o/oauth2/")
        or query.get("response_type") != ["code"]
        or not query.get("state")
    ):
        raise GateError("Google OAuth login redirect is not configured safely")
    return workspace_id, expected_email


def head_and_ranges(client: ApiClient, path: str, original: Path | None = None) -> None:
    head = client.request("HEAD", path, {200})
    if head.headers.get("Accept-Ranges", "").lower() != "bytes":
        raise GateError("media endpoint does not advertise byte ranges")
    ranged = client.request("GET", path, {206}, headers={"Range": "bytes=0-31"}, max_body=64)
    content_range = ranged.headers.get("Content-Range", "")
    if not re.fullmatch(r"bytes 0-31/[1-9][0-9]*", content_range) or len(ranged.body) != 32:
        raise GateError("media endpoint returned an invalid plaintext range")
    if original is not None:
        with original.open("rb") as source:
            if ranged.body != source.read(32):
                raise GateError("decrypted media range differs from uploaded plaintext")
    invalid = client.request(
        "GET", path, {416}, headers={"Range": "bytes=0-1,4-5"}, max_body=1024
    )
    if not invalid.headers.get("Content-Range", "").startswith("bytes */"):
        raise GateError("multi-range rejection omitted the plaintext length")


def upload_audio(client: ApiClient, config: dict[str, str], commit: str) -> tuple[str, str, str]:
    media = private_e2e_file(config, "HOOK2STREAM_E2E_MP3_FILE", 25 * 1024 * 1024 - 1, "licensed E2E MP3")
    size = media.stat().st_size
    if size < 1024 or size >= 25 * 1024 * 1024:
        raise GateError("licensed E2E MP3 must be between 1 KiB and the multipart threshold")
    local_hash = hashlib.sha256(media.read_bytes()).hexdigest()
    create, _ = client.json(
        "POST",
        "/api/v1/releases/audio-uploads",
        {200, 201},
        payload={
            "fileName": "licensed-deploy-e2e.mp3",
            "contentType": "audio/mpeg",
            "sizeBytes": size,
            "confirmsContentRights": True,
            "allowsExternalAiProcessing": True,
        },
        headers={"Idempotency-Key": idempotency("audio", commit)},
    )
    project_id = safe_uuid(create.get("project", {}).get("id"), "project ID")
    session_id = safe_uuid(create.get("upload", {}).get("sessionId"), "upload session ID")
    asset_id = safe_uuid(create.get("upload", {}).get("assetId"), "audio asset ID")
    if create.get("upload", {}).get("partCount") != 1:
        raise GateError("bounded E2E MP3 unexpectedly reserved multiple parts")
    upload_path = f"/api/v1/uploads/{session_id}/parts/1"
    first, _ = client.json("PUT", upload_path, {200}, data_file=media, timeout=600)
    second, _ = client.json("PUT", upload_path, {200}, data_file=media, timeout=600)
    if first != second or first.get("sha256") != local_hash or first.get("plaintextLength") != size:
        raise GateError("encrypted upload retry was not idempotent")
    resumed, _ = client.json("GET", f"/api/v1/uploads/{session_id}", {200})
    if resumed.get("completedParts") != [first]:
        raise GateError("durable upload receipt did not survive resume")
    completed, _ = client.json(
        "POST", f"/api/v1/uploads/{session_id}/complete", {202}, payload={"parts": None}
    )
    job_id = safe_uuid(completed.get("jobId"), "media ingest job ID")
    content_path = f"/api/v1/releases/{project_id}/assets/{asset_id}/content"
    wait_job(client, job_id, 900, "media ingest")
    head_and_ranges(client, content_path, media)
    return project_id, asset_id, content_path


def get_release(client: ApiClient, project_id: str) -> tuple[dict[str, Any], Response]:
    value, response = client.json("GET", f"/api/v1/releases/{project_id}", {200})
    if not isinstance(value, dict):
        raise GateError("release response is invalid")
    return value, response


def release_mutation(
    client: ApiClient,
    project_id: str,
    method: str,
    path: str,
    expected: int,
    payload: Any,
    extra_headers: dict[str, str] | None = None,
) -> tuple[Any, Response]:
    for _ in range(5):
        _, current = get_release(client, project_id)
        headers = {"If-Match": etag(current)}
        if extra_headers:
            headers.update(extra_headers)
        response = client.request(
            method, path, {expected, 412}, payload=payload, headers=headers
        )
        if response.status == expected:
            return parsed_json(response), response
        time.sleep(1)
    raise GateError("release mutation could not obtain a stable ETag")


def advance_pipeline(client: ApiClient, project_id: str, commit: str) -> tuple[list[str], str]:
    # A release-scoped retry must send byte-for-byte stable setup inputs even
    # after midnight. The API only requires an upcoming date; this far-future
    # QA date is deliberately unrelated to a customer schedule.
    release_day = (date(2090, 1, 1) + timedelta(days=int(commit[:4], 16) % 365)).isoformat()
    release_mutation(
        client,
        project_id,
        "PUT",
        f"/api/v1/releases/{project_id}/setup",
        200,
        {
            "projectLabel": f"Deploy E2E {commit[:12]}",
            "artistName": "Hook2Stream E2E",
            "trackTitle": "Encrypted Release Signal",
            "language": "en",
            "mode": "upcoming",
            "releaseDate": release_day,
            "campaignStartDate": None,
            "isInstrumental": False,
            "isInstrumentalConfirmed": False,
            "internalNotes": "Automated isolated deployment gate",
        },
    )
    release_mutation(
        client,
        project_id,
        "PUT",
        f"/api/v1/releases/{project_id}/rights",
        200,
        {
            "ownsAudioRights": True,
            "ownsLyricsRights": True,
            "ownsVisualRights": True,
            "allowsExternalAiArtwork": True,
            "allowsExternalAiProcessing": True,
            "syntheticContentStatus": "none",
            "policyVersion": "external-ai-zdr-v1",
        },
    )

    transcript, _ = wait_json(
        client,
        f"/api/v1/releases/{project_id}/transcript",
        lambda value: isinstance(value, dict)
        and value.get("state") in {"readyForReview", "approved"},
        1200,
        "OpenRouter transcript",
        {200, 404},
    )
    phrases = transcript.get("phrases")
    if not isinstance(phrases, list) or not phrases:
        raise GateError("OpenRouter transcript contains no phrases")
    for phrase in phrases:
        if not isinstance(phrase, dict):
            raise GateError("OpenRouter transcript phrase is invalid")
        phrase["warningAcknowledged"] = True
    if transcript.get("state") != "approved":
        revised, revised_response = release_mutation(
            client,
            project_id,
            "PUT",
            f"/api/v1/releases/{project_id}/transcript",
            201,
            {
                "source": transcript.get("source"),
                "language": transcript.get("language"),
                "isInstrumental": False,
                "phrases": phrases,
            },
            {"Idempotency-Key": idempotency("transcript", commit)},
        )
        client.json(
            "POST",
            f"/api/v1/releases/{project_id}/transcript/approve",
            {200},
            payload={"revisionId": safe_uuid(revised.get("revisionId"), "transcript revision ID")},
            headers={
                "If-Match": etag(revised_response),
                "Idempotency-Key": idempotency("transcript-approval", commit),
            },
        )

    artwork_path = f"/api/v1/releases/{project_id}/artwork"
    artwork_response = client.request("GET", artwork_path, {200, 404})
    if artwork_response.status == 404:
        generated = client.request(
            "POST",
            artwork_path,
            {202, 409},
            payload={"prompt": "Bold geometric night sky, high contrast, no text.", "style": "editorial"},
            headers={"Idempotency-Key": idempotency("artwork", commit)},
        )
        if generated.status not in {202, 409}:
            raise GateError("artwork generation was not accepted")
    artwork, artwork_response = wait_json(
        client,
        artwork_path,
        lambda value: isinstance(value, dict)
        and value.get("state") in {"readyForReview", "approved"}
        and isinstance(value.get("candidateAssetIds"), list)
        and len(value["candidateAssetIds"]) >= 1,
        1200,
        "OpenRouter artwork",
        {200, 404},
    )
    pack_id = safe_uuid(artwork.get("revisionId"), "artwork revision ID")
    selected_id = safe_uuid(artwork["candidateAssetIds"][0], "artwork asset ID")
    composition = artwork.get("compositionJson")
    if not isinstance(composition, str) or not composition or len(composition) > 40_000:
        raise GateError("artwork composition is missing or exceeds its safe bound")
    try:
        json.loads(composition)
    except json.JSONDecodeError as exc:
        raise GateError("artwork composition is malformed") from exc
    if artwork.get("state") != "approved":
        artwork, selection_response = client.json(
            "PUT",
            f"{artwork_path}/selection",
            {200},
            payload={
                "packRevisionId": pack_id,
                "selectedAssetId": selected_id,
                "compositionJson": composition,
            },
            headers={
                "If-Match": etag(artwork_response),
                "Idempotency-Key": idempotency("selection", commit),
            },
        )
        client.json(
            "POST",
            f"{artwork_path}/cover-approval",
            {200},
            payload={"revisionId": pack_id},
            headers={
                "If-Match": etag(selection_response),
                "Idempotency-Key": idempotency("cover-approval", commit),
            },
        )

    campaign, _ = wait_json(
        client,
        f"/api/v1/releases/{project_id}/campaign",
        lambda value: isinstance(value, dict)
        and isinstance(value.get("items"), list)
        and len(value["items"]) == 18,
        1800,
        "OpenRouter campaign",
        {200, 404},
    )
    item_ids = [safe_uuid(item.get("id"), "campaign item ID") for item in campaign["items"]]

    assets, _ = wait_json(
        client,
        f"/api/v1/releases/{project_id}/assets",
        lambda value: isinstance(value, list)
        and any(item.get("purpose") == "previewVideo" and item.get("state") == "ready" for item in value),
        1800,
        "preview render",
    )
    preview = next(item for item in assets if item.get("purpose") == "previewVideo" and item.get("state") == "ready")
    preview_id = safe_uuid(preview.get("id"), "preview asset ID")
    preview_path = f"/api/v1/releases/{project_id}/assets/{preview_id}/content"
    head_and_ranges(client, preview_path)
    workflow, _ = client.json("GET", f"/api/v1/releases/{project_id}/workflow", {200})
    lanes = {
        lane.get("lane"): lane.get("state")
        for lane in workflow.get("lanes", [])
        if isinstance(lane, dict)
    }
    completed_lanes = {"audio", "analysis", "transcript", "artwork", "hooks", "campaign", "preview"}
    if any(lanes.get(lane) != "succeeded" for lane in completed_lanes):
        raise GateError("media and OpenRouter workflow lanes did not all succeed through preview")
    return item_ids, preview_path


def trusted_runtime_secret(config: dict[str, str], name: str) -> Path:
    secrets_dir = Path(required(config, "SECRETS_DIR"))
    try:
        secrets_dir.resolve(strict=True).relative_to(HOST_ROOT / "secrets")
    except (OSError, ValueError) as exc:
        raise GateError("SECRETS_DIR must be below encrypted host secrets") from exc
    return trusted_file(str(secrets_dir / name), {0o400, 0o440, 0o600, 0o640}, 16_384, name)


def sign_test_webhook(secret_file: Path, payload: bytes, timestamp: int) -> str:
    secret = scalar(secret_file, "Stripe webhook secret")
    if not secret.startswith("whsec_"):
        raise GateError("Stripe webhook secret is not a Stripe signing secret")
    digest = hmac.new(secret.encode(), str(timestamp).encode() + b"." + payload, hashlib.sha256).hexdigest()
    return f"t={timestamp},v1={digest}"


def staging_billing_entitlement(
    client: ApiClient,
    config: dict[str, str],
    workspace_id: str,
    project_id: str,
    item_ids: list[str],
    commit: str,
) -> str:
    stripe_mode = required(config, "HOOK2STREAM_E2E_STRIPE_MODE")
    if stripe_mode != "test":
        raise GateError("the automated Stripe transaction is staging-test-only")
    secret_key = scalar(trusted_runtime_secret(config, "stripe_secret_key"), "Stripe API key")
    if not secret_key.startswith("sk_test_"):
        raise GateError("staging Stripe API key is not a test key")

    checkout_key = idempotency("checkout", commit)
    payload = {
        "productCode": "release_pack",
        "projectId": project_id,
        "itemIds": item_ids,
        "returnPath": f"/releases/{project_id}/campaign",
    }
    first, _ = client.json(
        "POST",
        "/api/v1/billing/checkouts",
        {200, 201},
        payload=payload,
        headers={"Idempotency-Key": checkout_key},
    )
    second, _ = client.json(
        "POST", "/api/v1/billing/checkouts", {200}, payload=payload, headers={"Idempotency-Key": checkout_key}
    )
    if first != second:
        raise GateError("Stripe checkout idempotency returned different resources")
    checkout_id = safe_uuid(first.get("checkoutId"), "checkout ID")
    checkout_url = str(first.get("checkoutUrl", ""))
    checkout_parsed = urllib.parse.urlsplit(checkout_url)
    if (
        checkout_parsed.scheme != "https"
        or checkout_parsed.hostname != "checkout.stripe.com"
        or "cs_test_" not in checkout_url
    ):
        raise GateError("Stripe checkout URL is not a test Checkout Session")

    def exact_entitlements() -> list[dict[str, Any]]:
        summary, _ = client.json("GET", "/api/v1/billing/summary", {200})
        return [
            entry
            for entry in summary.get("entitlements", [])
            if entry.get("productCode") == "release_pack"
            and entry.get("projectId") == project_id
            and entry.get("state") == "active"
            and sorted(entry.get("itemIds", [])) == sorted(item_ids)
        ]

    candidates = exact_entitlements()
    if not candidates:
        timestamp = int(time.time())
        marker = hashlib.sha256(f"{commit}:stripe-test".encode()).hexdigest()[:32]
        event = {
            "id": f"evt_h2s_e2e_{marker}",
            "type": "checkout.session.completed",
            "created": timestamp,
            "data": {
                "object": {
                    "id": f"cs_test_h2s_e2e_{marker}",
                    "payment_status": "paid",
                    "amount_total": 990,
                    "currency": "usd",
                    "payment_intent": f"pi_h2s_e2e_{marker}",
                    "metadata": {
                        "checkout_id": checkout_id.replace("-", ""),
                        "workspace_id": workspace_id.replace("-", ""),
                        "project_id": project_id.replace("-", ""),
                        "product_code": "release_pack",
                    },
                }
            },
        }
        raw = json.dumps(event, separators=(",", ":"), ensure_ascii=True).encode()
        signature = sign_test_webhook(
            trusted_runtime_secret(config, "stripe_webhook_secret"), raw, timestamp
        )

        def webhook() -> dict[str, Any]:
            request_headers = {
                "Stripe-Signature": signature,
                "Content-Type": "application/json",
            }
            request = urllib.request.Request(
                client._url("/api/v1/billing/stripe/webhook"),
                data=raw,
                headers=request_headers,
                method="POST",
            )
            webhook_opener = urllib.request.build_opener(
                urllib.request.ProxyHandler({}),
                NoRedirect(),
                urllib.request.HTTPSHandler(context=ssl.create_default_context()),
            )
            try:
                try:
                    response = webhook_opener.open(request, timeout=60)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    body = response.read(MAX_JSON + 1)
                    if response.status != 200 or len(body) > MAX_JSON:
                        raise GateError(
                            f"Stripe webhook returned unexpected HTTP {response.status}"
                        )
            except (OSError, urllib.error.URLError, TimeoutError) as exc:
                raise GateError("Stripe webhook request failed") from exc
            try:
                return json.loads(body)
            except json.JSONDecodeError as exc:
                raise GateError("Stripe webhook returned malformed JSON") from exc

        accepted = webhook()
        duplicate = webhook()
        if accepted.get("received") is not True or duplicate != {
            "received": True,
            "duplicate": True,
        }:
            raise GateError("Stripe test webhook was not accepted idempotently")
        candidates = exact_entitlements()

    if len(candidates) != 1:
        raise GateError("Stripe test flow did not create one exact active render entitlement")
    return safe_uuid(candidates[0].get("id"), "test entitlement ID")


def render_and_export(
    client: ApiClient,
    project_id: str,
    entitlement_id: str,
    item_ids: list[str],
    commit: str,
    work: Path,
) -> tuple[str, str, int]:
    started = time.monotonic()
    batch, _ = client.json(
        "POST",
        f"/api/v1/releases/{project_id}/renders",
        {202},
        payload={"entitlementId": entitlement_id, "itemIds": item_ids, "kind": "initial"},
        headers={"Idempotency-Key": idempotency("render-initial", commit)},
        timeout=120,
    )
    batch_id = safe_uuid(batch.get("batchId"), "render batch ID")

    def completed(value: Any) -> bool:
        state = value.get("state") if isinstance(value, dict) else None
        if state in {"failed", "cancelled", "partiallysucceeded", "partiallySucceeded"}:
            raise GateError(f"render batch entered terminal state {state}")
        return state == "succeeded"

    status, _ = wait_json(
        client,
        f"/api/v1/releases/{project_id}/renders/{batch_id}",
        completed,
        3600,
        "18-item render/export",
    )
    render_seconds = max(1, int(time.monotonic() - started))
    items = status.get("items")
    export = status.get("export")
    if (
        not isinstance(items, list)
        or len(items) != 18
        or any(item.get("state") != "succeeded" or not item.get("download") for item in items)
        or not isinstance(export, dict)
    ):
        raise GateError("render batch lacks 18 outputs or its export")
    export_url = str(export.get("url", ""))
    parsed = urllib.parse.urlsplit(urllib.parse.urljoin(client.origin, export_url))
    if parsed.scheme != "https" or parsed.netloc != urllib.parse.urlsplit(client.origin).netloc or parsed.query:
        raise GateError("export URL is not a query-free same-origin capability")
    export_path = parsed.path
    head_and_ranges(client, export_path)
    archive = work / "release-pack.zip"
    client.stream_to(export_path, archive, {200}, MAX_EXPORT)
    inspect_export(archive, work)
    return batch_id, export_path, render_seconds


def inspect_export(archive: Path, work: Path) -> None:
    try:
        with zipfile.ZipFile(archive) as bundle:
            infos = bundle.infolist()
            if len(infos) > 256 or sum(info.file_size for info in infos) > MAX_EXPORT:
                raise GateError("export ZIP exceeds its entry or expanded-size bound")
            names = [info.filename for info in infos]
            if len(names) != len(set(names)):
                raise GateError("export ZIP contains duplicate paths")
            for info in infos:
                unix_mode = (info.external_attr >> 16) & 0xFFFF
                file_type = stat.S_IFMT(unix_mode)
                if file_type not in {0, stat.S_IFREG, stat.S_IFDIR}:
                    raise GateError("export ZIP contains a link or special entry")
            for name in names:
                path = Path(name)
                if path.is_absolute() or ".." in path.parts or "\\" in name:
                    raise GateError("export ZIP contains an unsafe path")
            videos = [name for name in names if name.lower().endswith(".mp4")]
            lowered = [name.lower() for name in names]
            if (
                len(videos) != 18
                or not any("manifest" in name for name in lowered)
                or not any("calendar" in name for name in lowered)
                or not any("copy" in name or "caption" in name for name in lowered)
            ):
                raise GateError("export ZIP does not contain the canonical 18-video bundle")
            representative = work / "representative.mp4"
            with bundle.open(videos[0]) as source, representative.open("xb") as output:
                shutil.copyfileobj(source, output, 1024 * 1024)
    except (OSError, zipfile.BadZipFile) as exc:
        raise GateError("export ZIP is invalid") from exc
    probe = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "stream=codec_type,codec_name,width,height",
            "-of",
            "json",
            str(representative),
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=60,
        check=False,
    )
    if probe.returncode != 0 or len(probe.stdout) > 64 * 1024:
        raise GateError("representative render failed ffprobe validation")
    try:
        streams = json.loads(probe.stdout).get("streams", [])
    except json.JSONDecodeError as exc:
        raise GateError("ffprobe returned malformed JSON") from exc
    video = next((stream for stream in streams if stream.get("codec_type") == "video"), {})
    audio = next((stream for stream in streams if stream.get("codec_type") == "audio"), {})
    if video.get("codec_name") != "h264" or video.get("width") != 1080 or video.get("height") != 1920 or audio.get("codec_name") != "aac":
        raise GateError("representative render does not match the 1080x1920 H.264/AAC contract")


def docker_service_state(project: str, service: str) -> tuple[str, bool, bool, int, int]:
    listed = subprocess.run(
        [
            "docker",
            "ps",
            "--filter",
            f"label=com.docker.compose.project={project}",
            "--filter",
            f"label=com.docker.compose.service={service}",
            "--format",
            "{{.ID}}",
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=15,
        check=False,
    )
    ids = [line for line in listed.stdout.splitlines() if re.fullmatch(r"[0-9a-f]{12,64}", line)]
    if listed.returncode != 0 or len(ids) != 1:
        raise GateError(f"exactly one healthy {service} container is required")
    inspected = subprocess.run(
        ["docker", "inspect", ids[0]],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=15,
        check=False,
    )
    try:
        value = json.loads(inspected.stdout)[0]
        running = value["State"]["Running"] is True and value["State"].get("Health", {}).get("Status") == "healthy"
        oom = value["State"]["OOMKilled"] is True
        restarts = value["RestartCount"]
        pid = value["State"]["Pid"]
    except (json.JSONDecodeError, KeyError, IndexError, TypeError) as exc:
        raise GateError(f"{service} container state is unreadable") from exc
    if inspected.returncode != 0:
        raise GateError(f"{service} container inspection failed")
    if not isinstance(restarts, int) or restarts < 0:
        raise GateError(f"{service} restart count is invalid")
    if not isinstance(pid, int) or pid <= 0:
        raise GateError(f"{service} container PID is invalid")
    return ids[0], running, oom, restarts, pid


def docker_container_image(container_id: str) -> str:
    inspected = subprocess.run(
        ["docker", "inspect", container_id],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=15,
        check=False,
    )
    try:
        value = json.loads(inspected.stdout)[0]["Config"]["Image"]
    except (json.JSONDecodeError, KeyError, IndexError, TypeError) as exc:
        raise GateError("render worker image reference is unreadable") from exc
    if inspected.returncode != 0 or not re.fullmatch(
        r"[a-z0-9./_-]+@sha256:[0-9a-f]{64}", str(value)
    ):
        raise GateError("render worker does not use an immutable digest reference")
    return str(value)


def create_soak_container(image: str, commit: str) -> str:
    name = f"hook2stream-e2e-soak-{commit[:12]}-{uuid.uuid4().hex[:12]}"
    command = [
        "docker",
        "create",
        "--pull=never",
        "--name",
        name,
        "--label",
        "com.hook2stream.role=authenticated-e2e-soak",
        "--label",
        f"com.hook2stream.commit={commit}",
        "--network=none",
        "--read-only",
        "--cap-drop=ALL",
        "--security-opt=no-new-privileges",
        "--cpus=3",
        "--memory=1536m",
        "--pids-limit=256",
        "--entrypoint=timeout",
        image,
        "--foreground",
        "--signal=TERM",
        "--kill-after=10s",
        "3600s",
        "ffmpeg",
        "-nostdin",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "lavfi",
        "-i",
        "testsrc2=size=1080x1920:rate=30",
        "-an",
        "-c:v",
        "libx264",
        "-preset",
        "veryfast",
        "-threads",
        "3",
        "-f",
        "null",
        "-",
    ]
    created = subprocess.run(
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=60,
        check=False,
    )
    if created.returncode != 0 or not re.fullmatch(r"[0-9a-f]{64}\n?", created.stdout):
        raise GateError("isolated synthetic FFmpeg container could not be created")
    return name


def soak_container_state(name: str, image: str, commit: str) -> tuple[bool, bool, int, int]:
    inspected = subprocess.run(
        ["docker", "inspect", name],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=15,
        check=False,
    )
    try:
        value = json.loads(inspected.stdout)[0]
        labels = value["Config"]["Labels"]
        host = value["HostConfig"]
        state = value["State"]
        valid_identity = (
            value["Name"] == f"/{name}"
            and value["Config"]["Image"] == image
            and labels.get("com.hook2stream.role") == "authenticated-e2e-soak"
            and labels.get("com.hook2stream.commit") == commit
            and host["NetworkMode"] == "none"
            and host["ReadonlyRootfs"] is True
            and set(host.get("CapDrop") or []) == {"ALL"}
            and any(option.startswith("no-new-privileges") for option in host.get("SecurityOpt") or [])
            and host["NanoCpus"] == 3_000_000_000
            and host["Memory"] == 1536 * 1024 * 1024
            and host["PidsLimit"] == 256
        )
        running = state["Running"] is True
        oom = state["OOMKilled"] is True
        exit_code = state["ExitCode"]
        pid = state["Pid"]
    except (json.JSONDecodeError, KeyError, IndexError, TypeError) as exc:
        raise GateError("synthetic FFmpeg container state is unreadable") from exc
    if inspected.returncode != 0 or not valid_identity:
        raise GateError("synthetic FFmpeg container identity or isolation changed")
    if not isinstance(exit_code, int) or not isinstance(pid, int):
        raise GateError("synthetic FFmpeg container process state is invalid")
    return running, oom, exit_code, pid


def remove_soak_container(name: str, image: str, commit: str) -> None:
    soak_container_state(name, image, commit)
    removed = subprocess.run(
        ["docker", "rm", "--force", "--volumes", name],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=30,
        check=False,
    )
    if removed.returncode != 0:
        raise GateError("isolated synthetic FFmpeg container cleanup failed")


def container_cpu_stat(pid: int) -> tuple[int, int]:
    try:
        entries = Path(f"/proc/{pid}/cgroup").read_text(encoding="ascii").splitlines()
        unified = [line.split("::", 1)[1] for line in entries if line.startswith("0::")]
        if len(unified) != 1 or ".." in Path(unified[0]).parts:
            raise GateError("render worker unified cgroup is unavailable")
        cgroup_root = Path("/sys/fs/cgroup").resolve(strict=True)
        cgroup_path = (cgroup_root / unified[0].lstrip("/")).resolve(strict=True)
        cgroup_path.relative_to(cgroup_root)
        fields = {}
        for line in (cgroup_path / "cpu.stat").read_text(encoding="ascii").splitlines():
            name, raw = line.split()
            fields[name] = int(raw)
    except GateError:
        raise
    except (OSError, UnicodeError, ValueError, IndexError) as exc:
        raise GateError("render worker cgroup cpu.stat is unreadable") from exc
    nr_throttled = fields.get("nr_throttled")
    throttled_usec = fields.get("throttled_usec")
    if (
        not isinstance(nr_throttled, int)
        or nr_throttled < 0
        or not isinstance(throttled_usec, int)
        or throttled_usec < 0
    ):
        raise GateError("render worker cgroup lacks throttling counters")
    return nr_throttled, throttled_usec


def verify_egress_deny(project: str) -> None:
    container_id, running, oom, _, _ = docker_service_state(project, "egress-api")
    if not running or oom:
        raise GateError("API egress proxy is unhealthy or OOM-killed")
    inspected = subprocess.run(
        ["docker", "inspect", container_id],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=15,
        check=False,
    )
    try:
        networks = json.loads(inspected.stdout)[0]["NetworkSettings"]["Networks"]
        addresses = [
            entry.get("IPAddress")
            for name, entry in networks.items()
            if name.endswith("_api-egress") and entry.get("IPAddress")
        ]
    except (json.JSONDecodeError, KeyError, IndexError, TypeError) as exc:
        raise GateError("API egress proxy network state is unreadable") from exc
    if inspected.returncode != 0 or len(addresses) != 1:
        raise GateError("API egress proxy must have one private address")
    try:
        with socket.create_connection((addresses[0], 3128), timeout=5) as connection:
            connection.sendall(
                b"CONNECT hook2stream-denied.invalid:443 HTTP/1.1\r\n"
                b"Host: hook2stream-denied.invalid:443\r\nConnection: close\r\n\r\n"
            )
            response = connection.recv(4096)
    except OSError as exc:
        raise GateError("API egress proxy deny probe failed") from exc
    first_line = response.split(b"\r\n", 1)[0]
    if not re.fullmatch(rb"HTTP/1\.[01] 403(?: .*)?", first_line):
        raise GateError("API egress proxy did not reject an unrelated HTTPS origin")


def atomic_state(path: Path, state: dict[str, Any]) -> None:
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(state, output, separators=(",", ":"), sort_keys=True)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def state_directory(work_root: Path) -> Path:
    state_dir = work_root / "state"
    state_dir.mkdir(mode=0o700, exist_ok=True)
    info = state_dir.stat()
    if (
        state_dir.is_symlink()
        or info.st_uid != 0
        or stat.S_IMODE(info.st_mode) != 0o700
        or not stat.S_ISDIR(info.st_mode)
    ):
        raise GateError("E2E state directory is unsafe")
    return state_dir


def checkpoint(
    work_root: Path,
    environment: str,
    commit: str,
    phase: str,
    evidence: dict[str, Any],
) -> None:
    if phase not in {
        "authenticated", "approved", "uploaded", "pipeline", "entitled", "rendered", "verified"
    }:
        raise GateError("E2E checkpoint phase is invalid")
    if len(json.dumps(evidence, separators=(",", ":"))) > 64 * 1024:
        raise GateError("E2E checkpoint evidence exceeds its safe bound")
    atomic_state(
        state_directory(work_root) / f"{environment}-{commit}.checkpoint.json",
        {
            "schema": CHECKPOINT_SCHEMA,
            "environment": environment,
            "commit": commit,
            "phase": phase,
            "evidence": evidence,
            "updatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        },
    )


def prepare(config: dict[str, str], environment: str) -> tuple[ApiClient, Path, Path]:
    origin = required(config, "PUBLIC_ORIGIN")
    exact = "https://staging.hook2stream.com" if environment == "staging" else "https://hook2stream.com"
    if origin != exact:
        raise GateError("PUBLIC_ORIGIN does not match the environment")
    work_root = Path(required(config, "HOOK2STREAM_E2E_WORK_DIR"))
    if work_root != HOST_ROOT / "e2e" or work_root.is_symlink():
        raise GateError("HOOK2STREAM_E2E_WORK_DIR must be /srv/hook2stream/e2e")
    try:
        info = work_root.stat()
    except OSError as exc:
        raise GateError("encrypted E2E work directory is unavailable") from exc
    if info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o700 or not stat.S_ISDIR(info.st_mode):
        raise GateError("encrypted E2E work directory must be root-owned mode 0700")
    auth = private_e2e_file(config, "HOOK2STREAM_E2E_AUTH_FILE", 1024 * 1024, "E2E auth file")
    client = ApiClient(origin, required(config, "HOOK2STREAM_E2E_AUTH_KIND"), auth)
    run = Path(tempfile.mkdtemp(prefix="run.", dir=work_root))
    os.chmod(run, 0o700)
    return client, work_root, run


def release_gate(environment: str, config: dict[str, str], commit: str) -> None:
    client, work_root, run = prepare(config, environment)
    try:
        workspace_id, _ = verify_auth(client, config)
        checkpoint(work_root, environment, commit, "authenticated", {"workspaceId": workspace_id})

        project_id, audio_asset_id, content_path = upload_audio(client, config, commit)
        checkpoint(
            work_root,
            environment,
            commit,
            "uploaded",
            {
                "workspaceId": workspace_id,
                "projectId": project_id,
                "audioAssetId": audio_asset_id,
                "contentPath": content_path,
            },
        )
        item_ids, preview_path = advance_pipeline(client, project_id, commit)
        checkpoint(
            work_root,
            environment,
            commit,
            "pipeline",
            {
                "workspaceId": workspace_id,
                "projectId": project_id,
                "audioAssetId": audio_asset_id,
                "contentPath": content_path,
                "previewPath": preview_path,
                "itemIds": item_ids,
            },
        )

        if environment == "production":
            verify_egress_deny(required(config, "COMPOSE_PROJECT_NAME"))
            checkpoint(
                work_root,
                environment,
                commit,
                "verified",
                {
                    "workspaceId": workspace_id,
                    "projectId": project_id,
                    "audioAssetId": audio_asset_id,
                    "contentPath": content_path,
                    "previewPath": preview_path,
                    "itemIds": item_ids,
                },
            )
            print(PRODUCTION_GATE)
            return

        entitlement_id = staging_billing_entitlement(
            client, config, workspace_id, project_id, item_ids, commit
        )
        checkpoint(
            work_root,
            environment,
            commit,
            "entitled",
            {
                "workspaceId": workspace_id,
                "projectId": project_id,
                "entitlementId": entitlement_id,
                "itemIds": item_ids,
                "renderKind": "initial",
            },
        )
        batch_id, export_path, initial_render_seconds = render_and_export(
            client,
            project_id,
            entitlement_id,
            item_ids,
            commit,
            run,
        )
        checkpoint(
            work_root,
            environment,
            commit,
            "rendered",
            {
                "workspaceId": workspace_id,
                "projectId": project_id,
                "entitlementId": entitlement_id,
                "batchId": batch_id,
                "exportPath": export_path,
            },
        )
        verify_egress_deny(required(config, "COMPOSE_PROJECT_NAME"))
        atomic_state(
            state_directory(work_root) / f"{environment}-{commit}.json",
            {
                "schema": STATE_SCHEMA,
                "environment": environment,
                "commit": commit,
                "projectId": project_id,
                "audioAssetId": audio_asset_id,
                "contentPath": content_path,
                "previewPath": preview_path,
                "entitlementId": entitlement_id,
                "itemIds": item_ids,
                "batchId": batch_id,
                "exportPath": export_path,
                "initialRenderSeconds": initial_render_seconds,
                "completedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            },
        )
        print(STAGING_GATE)
    finally:
        shutil.rmtree(run, ignore_errors=True)


def cpu_sample() -> tuple[int, int]:
    try:
        fields = Path("/proc/stat").read_text(encoding="ascii").splitlines()[0].split()
        values = [int(value) for value in fields[1:]]
    except (OSError, ValueError, IndexError) as exc:
        raise GateError("CPU accounting sample is unavailable") from exc
    if len(values) < 8:
        raise GateError("CPU accounting lacks steal-time data")
    # Linux documents the first eight aggregate fields through steal. Do not
    # count guest/guest_nice twice: they are already included in user/nice.
    return sum(values[:8]), values[7]


def load_soak_state(config: dict[str, str], environment: str, commit: str) -> dict[str, Any]:
    work_root = Path(required(config, "HOOK2STREAM_E2E_WORK_DIR"))
    path = trusted_file(
        str(work_root / "state" / f"{environment}-{commit}.json"),
        {0o600},
        128 * 1024,
        "E2E release state",
    )
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise GateError("E2E release state is invalid") from exc
    expected_keys = {
        "schema", "environment", "commit", "projectId", "audioAssetId", "contentPath",
        "previewPath", "entitlementId", "itemIds", "batchId", "exportPath",
        "initialRenderSeconds", "completedAt",
    }
    if not isinstance(value, dict) or set(value) != expected_keys or value.get("schema") != STATE_SCHEMA or value.get("environment") != environment or value.get("commit") != commit:
        raise GateError("E2E release state is not bound to this release")
    if not isinstance(value.get("itemIds"), list) or len(value["itemIds"]) != 18:
        raise GateError("E2E release state does not bind 18 campaign items")
    for name in ("projectId", "audioAssetId", "entitlementId", "batchId"):
        safe_uuid(value.get(name), name)
    if not isinstance(value.get("initialRenderSeconds"), int) or value["initialRenderSeconds"] <= 0:
        raise GateError("E2E release state lacks measured initial render duration")
    return value


def load_baseline(config: dict[str, str]) -> dict[str, Any]:
    path = private_e2e_file(config, "HOOK2STREAM_E2E_SOAK_BASELINE_FILE", 64 * 1024, "soak baseline")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise GateError("soak baseline is invalid") from exc
    if (
        not isinstance(value, dict)
        or set(value) != {"schema", "environment", "providerSku", "renderSecondsPerItem", "tolerancePercent", "recordedAt"}
        or value.get("schema") != BASELINE_SCHEMA
        or value.get("environment") != "staging"
        or not isinstance(value.get("providerSku"), str)
        or not value["providerSku"]
        or not isinstance(value.get("renderSecondsPerItem"), int)
        or value["renderSecondsPerItem"] <= 0
        or value.get("tolerancePercent") != 20
    ):
        raise GateError("soak baseline schema or policy is invalid")
    if value["providerSku"] != required(config, "HOOK2STREAM_E2E_PROVIDER_SKU"):
        raise GateError("soak baseline was measured on a different provider SKU")
    try:
        recorded = datetime.fromisoformat(str(value["recordedAt"]).replace("Z", "+00:00"))
    except ValueError as exc:
        raise GateError("soak baseline timestamp is invalid") from exc
    if recorded.tzinfo is None:
        raise GateError("soak baseline timestamp must include a UTC offset")
    now = datetime.now(timezone.utc)
    recorded = recorded.astimezone(timezone.utc)
    if recorded > now + timedelta(minutes=5) or now - recorded > timedelta(days=90):
        raise GateError("soak baseline is missing or older than 90 days")
    return value


def soak_gate(environment: str, config: dict[str, str], commit: str) -> None:
    if environment != "staging":
        raise GateError("sustained soak is staging-only")
    client, _, run = prepare(config, environment)
    soak_name: str | None = None
    soak_image: str | None = None
    try:
        verify_auth(client, config)
        state = load_soak_state(config, environment, commit)
        baseline = load_baseline(config)
        real_seconds = state["initialRenderSeconds"]
        if real_seconds / 18 > baseline["renderSecondsPerItem"] * 1.2:
            raise GateError(
                "real 18-item render throughput is over 20 percent slower than the accepted same-SKU baseline"
            )
        project = required(config, "COMPOSE_PROJECT_NAME")
        (
            render_container,
            render_running,
            render_oom,
            initial_restarts,
            render_pid,
        ) = docker_service_state(project, "worker-render")
        if not render_running or render_oom:
            raise GateError("render worker is unhealthy before soak")
        soak_image = docker_container_image(render_container)
        soak_name = create_soak_container(soak_image, commit)
        running, oom, _, pid = soak_container_state(soak_name, soak_image, commit)
        if running or oom or pid != 0:
            raise GateError("synthetic FFmpeg container started before its isolation was verified")
        start = time.monotonic()
        deadline = start + 3660
        started = subprocess.run(
            ["docker", "start", soak_name],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            timeout=30,
            check=False,
        )
        if started.returncode != 0 or started.stdout.strip() != soak_name:
            raise GateError("isolated synthetic FFmpeg container could not start")
        running, oom, _, soak_pid = soak_container_state(soak_name, soak_image, commit)
        if not running or oom or soak_pid <= 0:
            raise GateError("isolated synthetic FFmpeg container did not become healthy")
        initial_cgroup = container_cpu_stat(soak_pid)
        latest_cgroup = initial_cgroup
        next_check = start + 1
        previous_cpu = cpu_sample()
        windows: deque[tuple[int, int]] = deque(maxlen=5)
        network_checks = 0
        network_failures = 0
        oom_killed = False
        while True:
            running, soak_oom, _, current_soak_pid = soak_container_state(
                soak_name, soak_image, commit
            )
            oom_killed = oom_killed or soak_oom
            if not running:
                break
            if soak_oom or current_soak_pid != soak_pid:
                raise GateError("synthetic FFmpeg container was OOM-killed or replaced")
            now = time.monotonic()
            if now >= deadline:
                raise GateError("synthetic FFmpeg soak exceeded its 3660-second safety deadline")
            if now < next_check:
                time.sleep(min(next_check - now, 5))
                continue
            current_cpu = cpu_sample()
            total_delta = current_cpu[0] - previous_cpu[0]
            steal_delta = current_cpu[1] - previous_cpu[1]
            previous_cpu = current_cpu
            if total_delta <= 0 or steal_delta < 0:
                raise GateError("CPU accounting delta is invalid")
            windows.append((total_delta, steal_delta))
            if len(windows) == 5:
                total = sum(item[0] for item in windows)
                steal = sum(item[1] for item in windows)
                if total <= 0 or steal * 100 > total * 10:
                    raise GateError("five-minute CPU steal exceeded ten percent")
            try:
                client.request("GET", "/", {200}, max_body=1024 * 1024)
                client.request("GET", "/health/api-ready", {200}, max_body=1024 * 1024)
                client.request("HEAD", state["contentPath"], {200})
                ranged = client.request(
                    "GET", state["contentPath"], {206}, headers={"Range": "bytes=0-31"}, max_body=64
                )
                if len(ranged.body) != 32:
                    raise GateError("soak storage range was truncated")
                container, running, oom, restarts, pid = docker_service_state(
                    project, "worker-render"
                )
                oom_killed = oom_killed or oom
                if (
                    not running
                    or oom
                    or container != render_container
                    or restarts != initial_restarts
                    or pid != render_pid
                ):
                    raise GateError("render worker became unhealthy or OOM-killed")
                soak_running, soak_oom, _, checked_soak_pid = soak_container_state(
                    soak_name, soak_image, commit
                )
                if not soak_running or soak_oom or checked_soak_pid != soak_pid:
                    raise GateError("synthetic FFmpeg container stopped during a soak check")
                current_cgroup = container_cpu_stat(checked_soak_pid)
                if (
                    current_cgroup[0] < initial_cgroup[0]
                    or current_cgroup[1] < initial_cgroup[1]
                ):
                    raise GateError("synthetic FFmpeg cgroup throttling counters moved backwards")
                latest_cgroup = current_cgroup
                network_checks += 1
            except GateError:
                network_failures += 1
                raise
            next_check = time.monotonic() + 60

        _, final_oom, return_code, _ = soak_container_state(soak_name, soak_image, commit)
        oom_killed = oom_killed or final_oom
        elapsed = int(time.monotonic() - start)
        if return_code != 124 or final_oom:
            raise GateError("synthetic FFmpeg did not end through the expected timeout")
        if elapsed < 3590 or elapsed > 3660 or network_checks < 60 or network_failures != 0:
            raise GateError("soak elapsed/network contract was not met")
        throttled_periods = latest_cgroup[0] - initial_cgroup[0]
        throttled_usec = latest_cgroup[1] - initial_cgroup[1]
        if throttled_periods < 0 or throttled_usec < 0:
            raise GateError("render worker cgroup throttling delta is invalid")
        if throttled_usec > elapsed * 100_000:
            raise GateError("render worker cgroup throttling exceeded ten percent of soak time")
        result = {
            "schema": SOAK_SCHEMA,
            "completedRenderCount": 18,
            "renderActiveSeconds": elapsed,
            "maxConcurrentRenderJobs": 1,
            "networkChecks": network_checks,
            "networkFailures": network_failures,
            "cpuThrottled": False,
            "oomKilled": oom_killed,
        }
        print(json.dumps(result, separators=(",", ":"), sort_keys=True))
    finally:
        if soak_name is not None and soak_image is not None:
            remove_soak_container(soak_name, soak_image, commit)
        shutil.rmtree(run, ignore_errors=True)


def main(argv: list[str]) -> None:
    if len(argv) not in {4, 5} or (len(argv) == 5 and argv[4] != "soak-60m"):
        fail("usage: authenticated-e2e.sh staging|production ENV_FILE COMMIT_SHA [soak-60m]")
    environment, env_path, commit = argv[1:4]
    if environment not in {"staging", "production"}:
        fail("invalid environment")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        fail("invalid commit")
    try:
        environment_path = trusted_file(
            env_path, {0o600}, 4 * 1024 * 1024, "release environment file"
        )
        try:
            environment_path.relative_to(HOST_ROOT / "release-state")
        except ValueError as exc:
            raise GateError("release environment file must be below encrypted release state") from exc
        config = parse_env_file(environment_path)
        if config.get("DEPLOYMENT_ENVIRONMENT") != environment:
            raise GateError("environment file does not match the requested environment")
        if len(argv) == 5:
            soak_gate(environment, config, commit)
        else:
            release_gate(environment, config, commit)
    except GateError as exc:
        fail(str(exc))


if __name__ == "__main__":
    main(sys.argv)
