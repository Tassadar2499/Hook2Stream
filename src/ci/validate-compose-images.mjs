import fs from "node:fs";

const modelPath = process.argv[2];
if (!modelPath) {
  process.stderr.write("Compose image validation: pass the rendered Compose JSON path\n");
  process.exit(2);
}

let model;
try {
  model = JSON.parse(fs.readFileSync(modelPath, "utf8"));
} catch (error) {
  process.stderr.write(
    `Compose image validation: could not parse ${modelPath}: ${error.message}\n`,
  );
  process.exit(1);
}

const services = Object.entries(model.services ?? {});
if (services.length === 0) {
  process.stderr.write("Compose image validation: rendered model has no services\n");
  process.exit(1);
}

for (const [serviceName, service] of services) {
  const image = service.image ?? "";
  if (!/^[^\s@]+@sha256:[0-9a-f]{64}$/.test(image)) {
    process.stderr.write(
      `Compose image validation: ${serviceName} must use image@sha256; ` +
        `received ${image || "<empty>"}\n`,
    );
    process.exit(1);
  }
}

process.stdout.write(
  `Compose image validation: ${services.length} services are digest-pinned\n`,
);
