import fs from "node:fs";

const modelPath = process.argv[2];
if (!modelPath) {
  process.stderr.write(
    "MinIO Compose validation: pass the rendered Compose JSON path\n",
  );
  process.exit(2);
}

let model;
try {
  model = JSON.parse(fs.readFileSync(modelPath, "utf8"));
} catch (error) {
  process.stderr.write(
    `MinIO Compose validation: could not parse ${modelPath}: ${error.message}\n`,
  );
  process.exit(1);
}

function fail(message) {
  process.stderr.write(`MinIO Compose validation: ${message}\n`);
  process.exit(1);
}

function assert(condition, message) {
  if (!condition) {
    fail(message);
  }
}

function namedMounts(entries = []) {
  return entries
    .map((entry) => (typeof entry === "string" ? entry : entry.source))
    .filter(Boolean)
    .sort();
}

function networkNames(service) {
  if (Array.isArray(service.networks)) {
    return [...service.networks].sort();
  }
  return Object.keys(service.networks ?? {}).sort();
}

function environmentValue(service, name) {
  const environment = service.environment ?? {};
  if (!Array.isArray(environment)) {
    return environment[name];
  }
  const item = environment.find((value) => value === name || value.startsWith(`${name}=`));
  return item?.includes("=") ? item.slice(item.indexOf("=") + 1) : undefined;
}

function bytes(value) {
  if (typeof value === "number") {
    return value;
  }
  if (typeof value !== "string") {
    return Number.NaN;
  }
  if (/^\d+$/.test(value)) {
    return Number(value);
  }
  const match = value.match(/^(\d+(?:\.\d+)?)\s*(B|K|KB|KIB|M|MB|MIB|G|GB|GIB)$/i);
  if (!match) {
    return Number.NaN;
  }
  const factors = {
    B: 1,
    K: 1024,
    KB: 1024,
    KIB: 1024,
    M: 1024 ** 2,
    MB: 1024 ** 2,
    MIB: 1024 ** 2,
    G: 1024 ** 3,
    GB: 1024 ** 3,
    GIB: 1024 ** 3,
  };
  return Number(match[1]) * factors[match[2].toUpperCase()];
}

function assertHardened(serviceName, expectedUser) {
  const service = services[serviceName];
  assert(service.read_only === true, `${serviceName} must have a read-only root filesystem`);
  if (expectedUser) {
    assert(service.user === expectedUser, `${serviceName} must run as ${expectedUser}`);
  } else {
    const runtimeUser = String(service.user ?? "").split(":", 1)[0];
    assert(runtimeUser !== "" && runtimeUser !== "0", `${serviceName} must run as a non-root user`);
  }
  assert(
    (service.cap_drop ?? []).map((capability) => capability.toUpperCase()).includes("ALL"),
    `${serviceName} must drop every Linux capability`,
  );
  assert(
    (service.security_opt ?? []).some((option) =>
      /^no-new-privileges[:=]true$/i.test(option),
    ),
    `${serviceName} must enable no-new-privileges`,
  );
}

function assertDigestImage(serviceName) {
  const image = services[serviceName].image ?? "";
  assert(
    /^[^\s@]+@sha256:[0-9a-f]{64}$/.test(image),
    `${serviceName} image must be digest-pinned; received ${image || "<empty>"}`,
  );
}

function configFile(configName, expectedSuffix) {
  const file = model.configs?.[configName]?.file;
  assert(typeof file === "string", `missing file-backed config ${configName}`);
  assert(
    file.endsWith(expectedSuffix),
    `${configName} must use ${expectedSuffix}; received ${file}`,
  );
  return file;
}

const services = model.services ?? {};
const minio = services.minio;
const minioInit = services["minio-init"];
assert(minio, "missing persistent minio service");
assert(minioInit, "missing minio-init tools service");

assertDigestImage("minio");
assertDigestImage("minio-init");
assertHardened("minio", "10001:10001");
assertHardened("minio-init");

assert(
  bytes(minio.deploy?.resources?.limits?.memory) === 1536 * 1024 ** 2,
  "minio memory limit must be exactly 1536 MiB",
);
assert(
  Number(minio.deploy?.resources?.limits?.cpus) === 1,
  "minio CPU limit must be exactly 1.00",
);
assert(
  bytes(services.postgres?.deploy?.resources?.limits?.memory) === 2 * 1024 ** 3,
  "postgres memory limit must be exactly 2 GiB in the staging profile",
);
const postgresSharedBuffers = (services.postgres?.command ?? [])
  .filter((argument) => String(argument).startsWith("shared_buffers="));
assert(
  JSON.stringify(postgresSharedBuffers) === JSON.stringify(["shared_buffers=512MB"]),
  "postgres command must set shared_buffers exactly once to 512MB",
);
assert(
  bytes(services["worker-render"]?.deploy?.resources?.limits?.memory) === 3 * 1024 ** 3,
  "worker-render memory limit must be exactly 3 GiB",
);

const largeServiceExceptions = new Set(["minio", "postgres", "worker-render"]);
for (const [serviceName, service] of Object.entries(services)) {
  if (largeServiceExceptions.has(serviceName)) {
    continue;
  }
  const memoryLimit = bytes(service.deploy?.resources?.limits?.memory);
  assert(
    Number.isFinite(memoryLimit) && memoryLimit > 0 && memoryLimit <= 1024 ** 3,
    `${serviceName} is persistent and must have a memory limit no greater than 1 GiB`,
  );
}

for (const workerName of [
  "worker-media",
  "worker-analysis",
  "worker-control",
  "worker-render",
  "worker-export",
]) {
  const worker = services[workerName];
  assert(worker, `missing worker pool ${workerName}`);
  assert(
    worker.scale == null && worker.deploy?.replicas == null,
    `${workerName} must remain a single instance without scale or replicas`,
  );
}
const initMemory = bytes(minioInit.deploy?.resources?.limits?.memory);
assert(
  Number.isFinite(initMemory) && initMemory > 0 && initMemory <= 1024 ** 3,
  "minio-init memory limit must be set and no greater than 1 GiB",
);
assert(minioInit.restart === "no", "minio-init must never restart automatically");
assert(
  (minioInit.profiles ?? []).includes("tools"),
  "minio-init must be isolated behind the tools profile",
);

const expectedRootConsumers = ["minio", "minio-init"];
for (const secretName of ["minio_root_user", "minio_root_password"]) {
  const consumers = Object.entries(services)
    .filter(([, service]) => namedMounts(service.secrets).includes(secretName))
    .map(([serviceName]) => serviceName)
    .sort();
  assert(
    JSON.stringify(consumers) === JSON.stringify(expectedRootConsumers),
    `${secretName} must be mounted only by ${expectedRootConsumers.join(" and ")}; ` +
      `received ${consumers.join(",") || "none"}`,
  );
}

const expectedMinioSecrets = ["minio_root_password", "minio_root_user"];
assert(
  JSON.stringify(namedMounts(minio.secrets)) === JSON.stringify(expectedMinioSecrets),
  `minio secrets must be exactly ${expectedMinioSecrets.join(",")}`,
);
const expectedInitSecrets = [
  "backup_s3_access_key",
  "backup_s3_secret_key",
  "minio_root_password",
  "minio_root_user",
  "s3_bootstrap_access_key",
  "s3_bootstrap_secret_key",
  "s3_runtime_access_key",
  "s3_runtime_secret_key",
].sort();
assert(
  JSON.stringify(namedMounts(minioInit.secrets)) === JSON.stringify(expectedInitSecrets),
  `minio-init secrets differ from the least-privilege bootstrap contract`,
);

assert(model.networks?.storage?.internal === true, "storage network must be internal");
assert(
  JSON.stringify(networkNames(minio)) === JSON.stringify(["storage"]),
  "minio must connect only to the internal storage network",
);
assert(
  JSON.stringify(networkNames(minioInit)) === JSON.stringify(["storage"]),
  "minio-init must connect only to the internal storage network",
);
assert(
  networkNames(services.caddy ?? {}).includes("storage"),
  "caddy must join the internal storage network",
);

for (const [serviceName, service] of Object.entries(services)) {
  for (const port of service.ports ?? []) {
    const serializedPort = JSON.stringify(port);
    const target = typeof port === "object" ? Number(port.target) : Number.NaN;
    const published = typeof port === "object" ? Number(port.published) : Number.NaN;
    assert(
      target !== 9000 && target !== 9001 && published !== 9000 && published !== 9001 &&
        !/(^|[^0-9])900[01]([^0-9]|$)/.test(serializedPort),
      `${serviceName} must not publish MinIO API or console ports`,
    );
  }
}
assert(
  String(environmentValue(minio, "MINIO_BROWSER")).toLowerCase() === "off",
  "MinIO browser console must be disabled",
);
assert(
  !(minio.command ?? []).join(" ").includes("--console-address"),
  "MinIO command must not enable a console listener",
);
assert(
  String(environmentValue(minio, "MINIO_API_CORS_ALLOW_ORIGIN")).toLowerCase() === "off",
  "MinIO global CORS must be disabled in favor of the bucket-scoped Caddy policy",
);
assert(
  environmentValue(services.caddy ?? {}, "S3_MEDIA_BUCKET") ===
    "hook2stream-staging-media" &&
    environmentValue(services.api ?? {}, "Storage__Bucket") ===
    "hook2stream-staging-media",
  "caddy must receive the exact media bucket for its scoped CORS route",
);
assert(
  String(environmentValue(services.bootstrapper ?? {},
    "Storage__ConfigureMultipartAbortLifecycle")).toLowerCase() === "false",
  "MinIO bootstrapper must not submit the unsupported abort-multipart lifecycle rule",
);

const dataVolume = (minio.volumes ?? []).find((volume) =>
  typeof volume === "object" &&
  volume.type === "volume" &&
  volume.source === "minio_data" &&
  volume.target === "/data",
);
assert(dataVolume, "minio must persist /data in the named minio_data volume");
assert(
  Object.hasOwn(model.volumes ?? {}, "minio_data"),
  "top-level minio_data volume is missing",
);

const expectedInitConfigs = [
  "minio_backup_lifecycle",
  "minio_backup_policy",
  "minio_bootstrap_policy",
  "minio_init",
  "minio_runtime_policy",
].sort();
assert(
  JSON.stringify(namedMounts(minioInit.configs)) === JSON.stringify(expectedInitConfigs),
  "minio-init must mount only the audited init script, policies, and lifecycle",
);
configFile("minio_init", "/minio/minio-init.sh");
for (const [configName, suffix] of [
  ["minio_runtime_policy", "/minio/policies/runtime-media.json"],
  ["minio_bootstrap_policy", "/minio/policies/bootstrap-media.json"],
  ["minio_backup_policy", "/minio/policies/postgres-backup.json"],
  ["minio_backup_lifecycle", "/minio/backup-lifecycle.json"],
]) {
  const path = configFile(configName, suffix);
  let document;
  try {
    document = JSON.parse(fs.readFileSync(path, "utf8"));
  } catch (error) {
    fail(`${configName} is not valid JSON: ${error.message}`);
  }
  assert(document && typeof document === "object", `${configName} must contain a JSON object`);
  if (configName.endsWith("_policy")) {
    const statements = Array.isArray(document.Statement) ? document.Statement : [];
    assert(statements.length > 0, `${configName} must contain at least one statement`);
    const actions = statements.flatMap((statement) =>
      Array.isArray(statement.Action) ? statement.Action : [statement.Action],
    );
    assert(
      !actions.includes("s3:*") && !actions.includes("*"),
      `${configName} must not grant wildcard actions`,
    );
  } else {
    const rules = Array.isArray(document.Rules) ? document.Rules : [];
    const rule = rules[0];
    assert(
      rules.length === 1 &&
        rule?.ID === "hook2stream-staging-backup-retention-7d" &&
        rule?.Status === "Enabled" &&
        Number(rule?.Expiration?.Days) === 6 &&
        Number(rule?.NoncurrentVersionExpiration?.NoncurrentDays) === 1,
      "MinIO backup lifecycle must cap current plus noncurrent retention at 7 days",
    );
  }
}

for (const [name, expected] of [
  ["MINIO_REGION", "us-east-1"],
  ["MINIO_MEDIA_BUCKET", "hook2stream-staging-media"],
  ["MINIO_BACKUP_BUCKET", "hook2stream-staging-pg-backups"],
  ["MINIO_BACKUP_PREFIX", "hook2stream/staging/postgres"],
  ["MINIO_MEDIA_QUOTA_GIB", "180"],
  ["MINIO_BACKUP_QUOTA_GIB", "20"],
]) {
  assert(
    String(environmentValue(minioInit, name)) === expected,
    `minio-init ${name} must be exactly ${expected}`,
  );
}
assert(
  environmentValue(services["postgres-backup"] ?? {}, "BACKUP_S3_BUCKET") ===
    "hook2stream-staging-pg-backups",
  "postgres-backup must use the staging backup bucket",
);
assert(
  environmentValue(services["postgres-backup"] ?? {}, "BACKUP_S3_REGION") ===
    "us-east-1",
  "postgres-backup region must be exactly us-east-1",
);
assert(
  environmentValue(services["postgres-backup"] ?? {}, "BACKUP_S3_PREFIX") ===
    "hook2stream/staging/postgres",
  "postgres-backup prefix must be exactly hook2stream/staging/postgres",
);
assert(
  String(environmentValue(services["postgres-backup"] ?? {}, "BACKUP_RETENTION_DAYS")) === "7",
  "postgres-backup local retention contract must be 7 days",
);

const internalEndpoint = "http://minio:9000";
const publicEndpoint = "https://s3-staging.example.invalid";
for (const serviceName of [
  "api",
  "worker-media",
  "worker-analysis",
  "worker-control",
  "worker-render",
  "worker-export",
  "bootstrapper",
]) {
  const service = services[serviceName];
  assert(service, `missing storage consumer ${serviceName}`);
  assert(
    environmentValue(service, "Storage__ServiceUrl") === internalEndpoint,
    `${serviceName} internal S3 endpoint must be exactly ${internalEndpoint}`,
  );
  assert(
    environmentValue(service, "Storage__PublicServiceUrl") === publicEndpoint,
    `${serviceName} public S3 endpoint must be exactly ${publicEndpoint}`,
  );
  assert(
    String(environmentValue(service, "Storage__ForcePathStyle")).toLowerCase() === "true",
    `${serviceName} must use path-style S3 addressing in MinIO mode`,
  );
  assert(
    environmentValue(service, "NO_PROXY") ===
      "127.0.0.1,localhost,postgres,pgbouncer,api,web,minio",
    `${serviceName} must bypass Squid only for the local Compose peers including MinIO`,
  );
}
assert(
  environmentValue(services["postgres-backup"] ?? {}, "BACKUP_S3_ENDPOINT") ===
    internalEndpoint,
  `postgres-backup endpoint must be exactly ${internalEndpoint}`,
);
assert(
  environmentValue(services["postgres-backup"] ?? {}, "NO_PROXY") ===
    "127.0.0.1,localhost,postgres,minio",
  "postgres-backup must bypass Squid for local PostgreSQL and MinIO",
);
assert(
  environmentValue(services["storage-probe"] ?? {}, "S3_ENDPOINT") === internalEndpoint &&
    environmentValue(services["storage-probe"] ?? {}, "NO_PROXY") ===
      "127.0.0.1,localhost,minio",
  "storage-probe must reach local MinIO directly instead of sending HTTP through Squid",
);
assert(
  String(environmentValue(services.bootstrapper, "Storage__ConfigureBucketCors")).toLowerCase() ===
    "false",
  "MinIO bootstrapper must disable unsupported PutBucketCors",
);

const caddyfile = configFile("caddyfile", "/Caddyfile.minio");
assert(fs.existsSync(caddyfile), "MinIO Caddy configuration must exist");

process.stdout.write("MinIO Compose validation: merged staging contract is valid\n");
