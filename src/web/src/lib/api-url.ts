export function normalizeApiBaseUrl(value?: string) {
  return value?.trim().replace(/\/+$/, "") ?? "";
}

export function joinApiUrl(baseUrl: string, path: string) {
  if (!path.startsWith("/")) {
    throw new Error("API paths must start with '/'.");
  }

  return `${normalizeApiBaseUrl(baseUrl)}${path}`;
}

const apiBaseUrl = normalizeApiBaseUrl(
  process.env.NEXT_PUBLIC_API_BASE_URL,
);

export function buildApiUrl(path: string) {
  return joinApiUrl(apiBaseUrl, path);
}
