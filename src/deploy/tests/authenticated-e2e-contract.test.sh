#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
hook=$deployment_dir/host/authenticated-e2e.sh
legacy=$deployment_dir/host/authenticated-e2e.example
fail() { printf '%s\n' "authenticated E2E contract test: $*" >&2; exit 1; }

[ -x "$hook" ] || fail "canonical authenticated hook is missing or not executable"
[ ! -e "$legacy" ] || fail "fail-closed placeholder must not coexist with the canonical hook"
command -v python3 >/dev/null 2>&1 || fail "Python 3 is required"

for contract in \
  '/api/v1/auth/session' \
  '/api/v1/account/me' \
  '/api/v1/releases/audio-uploads' \
  '/parts/1' \
  'bytes=0-1,4-5' \
  '/api/v1/billing/stripe/webhook' \
  'HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview,billing-disabled,stripe-egress-deny,egress-deny' \
  'previewVideo' \
  'completed_lanes = {"audio", "analysis", "transcript", "artwork", "hooks", "campaign", "preview"}' \
  'len(videos) != 18' \
  'testsrc2=size=1080x1920:rate=30' \
  '"3600s"' \
  'hook2stream-soak-hook-result-v1' \
  'five-minute CPU steal exceeded ten percent' \
  'render worker cgroup throttling exceeded ten percent of soak time' \
  'real 18-item render throughput is over 20 percent slower than the accepted same-SKU baseline' \
  'hook2stream-denied.invalid'; do
  grep -Fq "$contract" "$hook" || fail "hook omits live contract: $contract"
done

for secret_boundary in \
  'HOOK2STREAM_E2E_AUTH_FILE' \
  'HOOK2STREAM_E2E_EXPECTED_EMAIL_FILE' \
  'HOOK2STREAM_E2E_MP3_FILE' \
  'HOOK2STREAM_E2E_SOAK_BASELINE_FILE' \
  'MozillaCookieJar' \
  'cookie.domain_specified' \
  'O_NOFOLLOW' \
  'root-owned mode 0700' \
  'sk_test_' \
  'cs_test_'; do
  grep -Fq "$secret_boundary" "$hook" || fail "hook omits secret boundary: $secret_boundary"
done

if grep -Eq 'sk_live_|cs_live_|art_credits_5|technicalRetry|contentChange|HOOK2STREAM_E2E_(LIVE_ENTITLEMENT|PRODUCTION_QA)_FILE' "$hook"; then
  fail "production hook may not create a live Checkout or consume an unbound entitlement"
fi

if grep -Eq 'shell[[:space:]]*=[[:space:]]*True|os\.system\(|subprocess\.(call|run|Popen)\([^]]*shell' "$hook"; then
  fail "hook may not pass secret-bearing state through a shell"
fi

post_gate=$deployment_dir/scripts/post-deploy-e2e.sh
production_gate='HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview,billing-disabled,stripe-egress-deny,egress-deny'
grep -Fq "$production_gate" "$post_gate" \
  || fail "post-deploy gate accepts a stale production capability"
for orphan_boundary in \
  'child_pid=$!' \
  'kill -TERM "$child_pid"' \
  'wait "$child_pid"' \
  'authenticated-e2e.stdout'; do
  grep -Fq "$orphan_boundary" "$post_gate" \
    || fail "post-deploy shell omits authenticated-child cancellation boundary: $orphan_boundary"
done

python3 - "$hook" <<'PY'
import ast
import hashlib
import hmac
import http.cookiejar
import json
import sys
import tempfile
import types
from pathlib import Path

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8")
tree = ast.parse(source, filename=str(path))
compile(tree, str(path), "exec")

wait_calls = [
    node
    for node in ast.walk(tree)
    if isinstance(node, ast.Call)
    and isinstance(node.func, ast.Name)
    and node.func.id == "wait_json"
]
if len(wait_calls) != 6:
    raise SystemExit("authenticated hook must retain the six bounded wait_json calls")
for call in wait_calls:
    if (
        len(call.args) not in {5, 6}
        or not isinstance(call.args[0], ast.Name)
        or call.args[0].id != "client"
        or call.keywords
    ):
        raise SystemExit(
            f"wait_json call on line {call.lineno} does not pass client first or changed shape"
        )

# Compile-time Python accepts a missing positional argument. Validate every
# direct call to a top-level hook function against its declared signature so a
# duplicated/malformed wait or helper call cannot become a deploy-time failure.
functions = {
    node.name: node
    for node in tree.body
    if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
}
for call in ast.walk(tree):
    if not isinstance(call, ast.Call) or not isinstance(call.func, ast.Name):
        continue
    definition = functions.get(call.func.id)
    if definition is None:
        continue
    positional = [*definition.args.posonlyargs, *definition.args.args]
    positional_names = [argument.arg for argument in positional]
    required_count = len(positional) - len(definition.args.defaults)
    if any(isinstance(argument, ast.Starred) for argument in call.args):
        raise SystemExit(f"starred local call on line {call.lineno} cannot be checked")
    if definition.args.vararg is None and len(call.args) > len(positional):
        raise SystemExit(f"too many arguments for {call.func.id} on line {call.lineno}")
    keyword_names = [keyword.arg for keyword in call.keywords]
    if any(name is None for name in keyword_names):
        raise SystemExit(f"expanded keyword call on line {call.lineno} cannot be checked")
    if len(keyword_names) != len(set(keyword_names)):
        raise SystemExit(f"duplicate keyword for {call.func.id} on line {call.lineno}")
    supplied_positionally = set(positional_names[:len(call.args)])
    if supplied_positionally.intersection(keyword_names):
        raise SystemExit(f"argument supplied twice for {call.func.id} on line {call.lineno}")
    allowed_keywords = set(positional_names) | {
        argument.arg for argument in definition.args.kwonlyargs
    }
    if definition.args.kwarg is None and not set(keyword_names).issubset(allowed_keywords):
        raise SystemExit(f"unknown keyword for {call.func.id} on line {call.lineno}")
    supplied = supplied_positionally | set(keyword_names)
    missing = set(positional_names[:required_count]) - supplied
    missing.update(
        argument.arg
        for argument, default in zip(
            definition.args.kwonlyargs, definition.args.kw_defaults, strict=True
        )
        if default is None and argument.arg not in supplied
    )
    if missing:
        raise SystemExit(
            f"missing argument(s) {sorted(missing)} for {call.func.id} on line {call.lineno}"
        )

module = types.ModuleType("hook2stream_authenticated_e2e_contract")
module.__file__ = str(path)
sys.modules[module.__name__] = module
exec(compile(tree, str(path), "exec"), module.__dict__)

with tempfile.TemporaryDirectory() as directory:
    env = Path(directory) / "gate.env"
    env.write_text("PUBLIC_ORIGIN=https://hook2stream.com\nPUBLIC_ORIGIN=duplicate\n", encoding="utf-8")
    try:
        module.parse_env_file(env)
    except module.GateError:
        pass
    else:
        raise SystemExit("duplicate environment keys unexpectedly passed")

    secret = Path(directory) / "webhook-secret"
    secret.write_text("whsec_contract_only\n", encoding="utf-8")
    payload = b'{"bounded":true}'
    signed = module.sign_test_webhook(secret, payload, 123456)
    expected = hmac.new(
        b"whsec_contract_only", b"123456." + payload, hashlib.sha256
    ).hexdigest()
    if signed != f"t=123456,v1={expected}":
        raise SystemExit("file-based webhook signing changed bytes")

    cookies = Path(directory) / "session.cookies"
    jar = http.cookiejar.MozillaCookieJar(str(cookies))
    for name, value, http_only in (
        ("__Host-h2s_session", "session-contract-value", True),
        ("__Host-h2s_csrf", "csrf-contract-value-that-is-long-enough", False),
    ):
        jar.set_cookie(http.cookiejar.Cookie(
            version=0, name=name, value=value, port=None, port_specified=False,
            domain="staging.hook2stream.com", domain_specified=False,
            domain_initial_dot=False, path="/", path_specified=True,
            secure=True, expires=4102444800, discard=False, comment=None,
            comment_url=None, rest={"HttpOnly": None} if http_only else {},
            rfc2109=False,
        ))
    jar.save(ignore_discard=True, ignore_expires=True)
    client = module.ApiClient("https://staging.hook2stream.com", "oauth-session", cookies)
    if client.file_csrf != "csrf-contract-value-that-is-long-enough":
        raise SystemExit("OAuth cookie jar did not load CSRF from the file")

    scoped = Path(directory) / "scoped.cookies"
    scoped_jar = http.cookiejar.MozillaCookieJar(str(scoped))
    for name in ("__Host-h2s_session", "__Host-h2s_csrf"):
        scoped_jar.set_cookie(http.cookiejar.Cookie(
            version=0, name=name, value="domain-scoped-cookie-must-fail", port=None,
            port_specified=False, domain=".staging.hook2stream.com", domain_specified=True,
            domain_initial_dot=True, path="/", path_specified=True, secure=True,
            expires=4102444800, discard=False, comment=None, comment_url=None, rest={},
            rfc2109=False,
        ))
    scoped_jar.save(ignore_discard=True, ignore_expires=True)
    try:
        module.ApiClient("https://staging.hook2stream.com", "oauth-session", scoped)
    except module.GateError:
        pass
    else:
        raise SystemExit("domain-scoped __Host- cookies unexpectedly passed")

operation_one = "1" * 32
operation_two = "2" * 32
if module.idempotency("render-initial", "a" * 40, operation_one) != module.idempotency(
    "render-initial", "a" * 40, operation_one
):
    raise SystemExit("release operation idempotency key is not deterministic")
if module.idempotency("audio", "a" * 40, operation_one) == module.idempotency(
    "audio", "a" * 40, operation_two
):
    raise SystemExit("same-SHA E2E attempts unexpectedly share an idempotency key")

checkout_session_id = "cs_test_" + "a1B2" * 16
if module.stripe_test_checkout_session_id(
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}#opaque-client-state"
) != checkout_session_id:
    raise SystemExit("real Stripe test Checkout Session ID was not recovered from its URL")
for invalid_checkout_url in (
    f"http://checkout.stripe.com/c/pay/{checkout_session_id}",
    f"https://checkout.stripe.com:444/c/pay/{checkout_session_id}",
    f"https://checkout.stripe.com:/c/pay/{checkout_session_id}",
    f"https://user@checkout.stripe.com/c/pay/{checkout_session_id}",
    f"https://checkout.stripe.com/c/pay/cs_live_{'a' * 32}",
    "https://checkout.stripe.com/c/pay/missing-session-id",
    f"https://checkout.stripe.com/other/{checkout_session_id}",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}/{checkout_session_id}",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}/",
    f"https://checkout.stripe.com/c/pay/cs%5Ftest%5F{'a' * 32}",
    f"https://checkout.stripe.com/c/pay/%63s_test_{'a' * 32}",
    f"https://checkout.stripe.com/c/pay/cs_test_{'a' * 248}",
    f"https://checkout.stripe.com/c/pay/cs_test_{'a' * 16}-invalid",
    f"https://checkout.stripe.com/c/pay/missing?session={checkout_session_id}",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}?x=1",
    f"https://checkout.stripe.com/c/pay/missing#{checkout_session_id}",
    f"https://checkout.stripe.com.evil.invalid/c/pay/{checkout_session_id}",
    f" https://checkout.stripe.com/c/pay/{checkout_session_id}",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}\n",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}#opaque state",
    f"https://checkout.stripe.com/c/pay/{checkout_session_id}#" + "x" * 2048,
):
    try:
        module.stripe_test_checkout_session_id(invalid_checkout_url)
    except module.GateError:
        pass
    else:
        raise SystemExit(f"unsafe Stripe Checkout URL unexpectedly passed: {invalid_checkout_url}")

billing_gate = ast.get_source_segment(
    source,
    next(
        node for node in tree.body
        if isinstance(node, ast.FunctionDef) and node.name == "staging_billing_entitlement"
    ),
)
if (
    "checkout_session_id = stripe_test_checkout_session_id(checkout_url)" not in billing_gate
    or '"id": checkout_session_id' not in billing_gate
    or "cs_test_h2s_e2e_" in billing_gate
    or '"payment_intent"' in billing_gate
):
    raise SystemExit("synthetic Stripe webhook is not safely bound to the real Checkout Session ID")

# Model the API's completed-upload behavior: replaying one operation would
# receive the completed session and PUT=>409, while two persisted wrapper
# attempts for the same release must allocate independent sessions.
class ReplayUploadClient:
    def __init__(self):
        self.by_key = {}
        self.sessions = {}

    def request(self, method, path, expected, **_kwargs):
        if method == "GET" and path.startswith("/api/v1/uploads/"):
            session = path.rsplit("/", 1)[-1]
            state = self.sessions[session]
            if state["completed"]:
                return module.Response(409, {}, b'{}')
            body = json.dumps({
                "completedParts": [] if state["receipt"] is None else [state["receipt"]]
            }).encode()
            return module.Response(200, {}, body)
        raise AssertionError((method, path))

    def json(self, method, path, expected, **kwargs):
        if method == "POST" and path == "/api/v1/releases/audio-uploads":
            key = kwargs["headers"]["Idempotency-Key"]
            if key not in self.by_key:
                number = len(self.by_key) + 1
                project = f"00000000-0000-0000-0000-{number:012d}"
                session = f"10000000-0000-0000-0000-{number:012d}"
                asset = f"20000000-0000-0000-0000-{number:012d}"
                self.by_key[key] = (project, session, asset)
                self.sessions[session] = {"completed": False, "receipt": None}
            project, session, asset = self.by_key[key]
            return ({
                "project": {"id": project},
                "upload": {"sessionId": session, "assetId": asset, "partCount": 1},
            }, None)
        if method == "PUT" and path.endswith("/parts/1"):
            session = path.split("/")[4]
            state = self.sessions[session]
            if state["completed"]:
                raise module.GateError("modeled HTTP 409 for completed upload")
            receipt = {
                "partNumber": 1,
                "plaintextLength": kwargs["data_file"].stat().st_size,
                "sha256": hashlib.sha256(kwargs["data_file"].read_bytes()).hexdigest(),
                "eTag": "modeled-etag",
            }
            state["receipt"] = receipt
            return receipt, None
        if method == "GET" and path.startswith("/api/v1/uploads/"):
            session = path.rsplit("/", 1)[-1]
            state = self.sessions[session]
            if state["completed"]:
                raise module.GateError("modeled upload.not_resumable 409")
            return {"completedParts": [state["receipt"]]}, None
        if method == "POST" and path.endswith("/complete"):
            session = path.split("/")[4]
            self.sessions[session]["completed"] = True
            number = int(session[-12:])
            return {"jobId": f"30000000-0000-0000-0000-{number:012d}"}, None
        raise AssertionError((method, path))

with tempfile.TemporaryDirectory() as directory:
    media = Path(directory) / "fixture.mp3"
    media.write_bytes(b"ID3" + b"same-sha-replay" * 100)
    original_private_file = module.private_e2e_file
    original_wait_job = module.wait_job
    original_ranges = module.head_and_ranges
    module.private_e2e_file = lambda *_args, **_kwargs: media
    module.wait_job = lambda *_args, **_kwargs: {}
    module.head_and_ranges = lambda *_args, **_kwargs: None
    try:
        replay_client = ReplayUploadClient()
        first = module.upload_audio(replay_client, {}, "a" * 40, operation_one)
        resumed_first = module.upload_audio(
            replay_client, {}, "a" * 40, operation_one
        )
        second = module.upload_audio(replay_client, {}, "a" * 40, operation_two)
    finally:
        module.private_e2e_file = original_private_file
        module.wait_job = original_wait_job
        module.head_and_ranges = original_ranges
    if first != resumed_first or first == second or len(replay_client.by_key) != 2:
        raise SystemExit("same-SHA repeated E2E did not allocate attempt-scoped upload state")

gate_names = {
    target.id
    for node in tree.body
    if isinstance(node, ast.Assign)
    for target in node.targets
    if isinstance(target, ast.Name) and target.id.endswith("_GATE")
}
if gate_names != {"STAGING_GATE", "PRODUCTION_GATE", "ROLLBACK_GATE"}:
    raise SystemExit("release and bounded rollback paths must publish distinct truthful capabilities")

release = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "release_gate"),
)
if not (
    release.index("upload_audio(")
    < release.index("advance_pipeline(")
):
    raise SystemExit("authenticated media ingest no longer precedes the pipeline")
staging_release = release[release.index("entitlement_id = staging_billing_entitlement("):]
ordered_release_checks = [
    "staging_billing_entitlement(",
    "render_and_export(",
    "verify_egress_deny(",
    "atomic_state(",
    "print(STAGING_GATE)",
]
positions = [staging_release.index(check) for check in ordered_release_checks]
if positions != sorted(positions) or staging_release.count("print(STAGING_GATE)") != 1:
    raise SystemExit("capability output is not strictly after every live release check")

advance = next(
    node for node in tree.body
    if isinstance(node, ast.FunctionDef) and node.name == "advance_pipeline"
)
transcript_puts = [
    node
    for node in ast.walk(advance)
    if isinstance(node, ast.Call)
    and isinstance(node.func, ast.Name)
    and node.func.id == "release_mutation"
    and len(node.args) >= 6
    and isinstance(node.args[2], ast.Constant)
    and node.args[2].value == "PUT"
    and "/transcript" in (ast.get_source_segment(source, node.args[3]) or "")
]
if len(transcript_puts) != 1 or not isinstance(transcript_puts[0].args[5], ast.Dict):
    raise SystemExit("authenticated pipeline must retain one transcript revision PUT")
transcript_payload = {
    key.value: value
    for key, value in zip(
        transcript_puts[0].args[5].keys,
        transcript_puts[0].args[5].values,
        strict=True,
    )
    if isinstance(key, ast.Constant) and isinstance(key.value, str)
}
revision_source = transcript_payload.get("source")
if not isinstance(revision_source, ast.Constant) or revision_source.value != "manual":
    raise SystemExit(
        "user transcript revision must use manual source, never replay worker-owned automatic source"
    )
instrumental = transcript_payload.get("isInstrumental")
if not isinstance(instrumental, ast.Constant) or instrumental.value is not False:
    raise SystemExit("non-instrumental E2E transcript revision changed semantics")

production_branch = next(
    node
    for node in ast.walk(next(
        node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "release_gate"
    ))
    if isinstance(node, ast.If)
    and isinstance(node.test, ast.Compare)
    and ast.get_source_segment(source, node.test) == 'environment == "production"'
)
production_source = ast.get_source_segment(source, production_branch)
if (
    "print(PRODUCTION_GATE)" not in production_source
    or 'required(config, "BILLING_MODE") != "disabled"' not in production_source
    or "verify_billing_disabled(client, project_id, item_ids, commit, operation_id)" not in production_source
    or not any(isinstance(node, ast.Return) for node in ast.walk(production_branch))
    or "render_and_export(" in production_source
    or "staging_billing_entitlement(" in production_source
):
    raise SystemExit("production gate does not stop before billing/final render")

billing_disabled = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify_billing_disabled"),
)
for disabled_contract in (
    'client.json("GET", "/api/v1/billing/summary", {200})',
    'summary.get("checkoutEnabled") is not False',
    '"/api/v1/billing/checkouts"',
    '"/api/v1/billing/stripe/webhook"',
    'problem.get("status") != 503',
    'problem.get("code") != "billing.disabled"',
):
    if disabled_contract not in billing_disabled:
        raise SystemExit(f"production billing-disabled gate omits: {disabled_contract}")

egress_deny = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify_egress_deny"),
)
if (
    'denied_hosts.append("api.stripe.com")' not in egress_deny
    or 'environment == "production"' not in egress_deny
    or "HTTP/1\\.[01] 403" not in egress_deny
):
    raise SystemExit("production egress gate does not prove api.stripe.com is denied")

rollback = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "rollback_gate"),
)
for check in (
    "verify_auth(client, config)",
    'load_rollback_state(config, environment, commit)',
    'head_and_ranges(client, state["contentPath"])',
    'head_and_ranges(client, state["previewPath"])',
    "verify_egress_deny(",
    "print(ROLLBACK_GATE)",
):
    if check not in rollback:
        raise SystemExit(f"bounded rollback gate omits live read-only evidence: {check}")
if 'client.json("POST"' in rollback:
    raise SystemExit("bounded rollback verification may not mutate billing/render state")

auth = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify_auth"),
)
for check in (
    'required(config, "GOOGLE_CLIENT_ID")',
    '"/api/v1/auth/callback"',
    '{"openid", "email", "profile"}',
    'query.get("response_type") != ["code"]',
):
    if check not in auth:
        raise SystemExit(f"OAuth login redirect is not bound to runtime config: {check}")

upload = next(
    node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "upload_audio"
)
upload_calls = {
    node.func.id: node.lineno
    for node in ast.walk(upload)
    if isinstance(node, ast.Call)
    and isinstance(node.func, ast.Name)
    and node.func.id in {"wait_job", "head_and_ranges"}
}
if set(upload_calls) != {"wait_job", "head_and_ranges"} or not (
    upload_calls["wait_job"] < upload_calls["head_and_ranges"]
):
    raise SystemExit("media content was read before the ingest job completed")

soak = ast.get_source_segment(
    source,
    next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "soak_gate"),
)
for check in (
    'create_soak_container(soak_image, commit)',
    'soak_container_state(soak_name, soak_image, commit)',
    'remove_soak_container(soak_name, soak_image, commit)',
    'docker_service_state(project, "worker-render")',
    'container_cpu_stat(soak_pid)',
    'client.request("HEAD", state["contentPath"], {200})',
    'real_seconds / 18 > baseline["renderSecondsPerItem"] * 1.2',
    'return_code != 124',
    'advance_soak_probe_deadline(',
    'if network_checks >= 60:',
    'print(json.dumps(result',
):
    if check not in soak:
        raise SystemExit(f"soak omitted live evidence before output: {check}")
if '/renders' in soak or 'staging_billing_entitlement(' in soak or 'client.json("POST"' in soak:
    raise SystemExit("soak may not consume a billing or render entitlement")
if 'next_check = time.monotonic() + 60' in soak or 'next_check += 60' in soak:
    raise SystemExit("soak network cadence must not accumulate probe latency")
if soak.index('start = time.monotonic()') < soak.index('if started.returncode != 0'):
    raise SystemExit("soak schedule starts before Docker confirms its start request")
if soak.index('if network_checks >= 60:') > soak.index('if now < next_check:'):
    raise SystemExit("soak does not cap probes before scheduling another observation")

deadline = 1.0
for _ in range(60):
    started = deadline + 0.25
    deadline = module.advance_soak_probe_deadline(deadline, started, started + 2.5)
if deadline != 3601.0:
    raise SystemExit("anchored soak cadence does not schedule exactly 60 minute slots")
for invalid_schedule in (
    (1.0, 0.9, 1.0),
    (1.0, 1.0, 0.9),
    (1.0, 6.01, 6.02),
    (1.0, 1.0, 61.0),
    (1.0, 121.0, 121.1),
):
    try:
        module.advance_soak_probe_deadline(*invalid_schedule)
    except module.GateError:
        pass
    else:
        raise SystemExit(f"unsafe soak cadence passed: {invalid_schedule}")

creator = ast.get_source_segment(
    source,
    next(
        node for node in tree.body
        if isinstance(node, ast.FunctionDef) and node.name == "create_soak_container"
    ),
)
for boundary in (
    '"--pull=never"',
    '"--network=none"',
    '"--read-only"',
    '"--cap-drop=ALL"',
    '"--security-opt=no-new-privileges"',
    '"--cpus=3"',
    '"--memory=1536m"',
    '"--pids-limit=256"',
    '"--entrypoint=timeout"',
    '"testsrc2=size=1080x1920:rate=30"',
):
    if boundary not in creator:
        raise SystemExit(f"synthetic FFmpeg container omitted isolation boundary {boundary}")
PY

temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT
if "$hook" staging /does/not/exist deadbeef 11111111111111111111111111111111 >"$temporary_dir/stdout" 2>"$temporary_dir/stderr"; then
  fail "invalid invocation unexpectedly succeeded"
fi
[ ! -s "$temporary_dir/stdout" ] || fail "failed hook wrote a capability to stdout"
grep -Fq 'invalid commit' "$temporary_dir/stderr" || fail "invalid invocation did not fail closed"

for environment_file in \
  "$deployment_dir/environments/staging.env.example" \
  "$deployment_dir/environments/production.env.example"; do
  grep -Fxq 'HOOK2STREAM_E2E_AUTH_KIND=oauth-session' "$environment_file" \
    || fail "$(basename "$environment_file") does not select the OAuth session file contract"
  grep -Fxq 'HOOK2STREAM_E2E_WORK_DIR=/srv/hook2stream/e2e' "$environment_file" \
    || fail "$(basename "$environment_file") does not keep E2E scratch encrypted"
done
grep -Fxq 'HOOK2STREAM_E2E_STRIPE_MODE=test' \
  "$deployment_dir/environments/staging.env.example" \
  || fail "staging does not pin the automated transaction to Stripe test mode"
if grep -q '^HOOK2STREAM_E2E_STRIPE_MODE=' \
  "$deployment_dir/environments/production.env.example"; then
  fail "production may not configure an automated Stripe transaction mode"
fi

printf '%s\n' "authenticated E2E contract test: live public API, file credentials, render/export and soak boundaries passed"
