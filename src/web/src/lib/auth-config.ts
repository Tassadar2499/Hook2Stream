export type AppAuthMode = "oauth" | "local" | "unconfigured";

export function getAppAuthMode(): AppAuthMode {
  const configuredMode = process.env.NEXT_PUBLIC_AUTH_MODE?.toLowerCase();
  const hasLocalToken = Boolean(process.env.NEXT_PUBLIC_LOCAL_AUTH_TOKEN);

  if (configuredMode === "local") {
    return hasLocalToken ? "local" : "unconfigured";
  }

  if (configuredMode === "oauth") {
    return "oauth";
  }

  return hasLocalToken ? "local" : "unconfigured";
}

export function isAppAuthConfigured() {
  return getAppAuthMode() !== "unconfigured";
}

export function buildOAuthLoginUrl(returnPath?: string) {
  const apiBaseUrl =
    process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ??
    "http://localhost:5000";
  const query = returnPath ? `?returnPath=${encodeURIComponent(returnPath)}` : "";
  return `${apiBaseUrl}/api/v1/auth/login${query}`;
}

export function buildOAuthLogoutUrl() {
  const apiBaseUrl =
    process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ??
    "http://localhost:5000";
  return `${apiBaseUrl}/api/v1/auth/logout`;
}
