export type AppAuthMode = "clerk" | "local" | "unconfigured";

export function getAppAuthMode(): AppAuthMode {
  const configuredMode = process.env.NEXT_PUBLIC_AUTH_MODE?.toLowerCase();
  const hasClerkKey = Boolean(process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY);
  const hasLocalToken = Boolean(process.env.NEXT_PUBLIC_LOCAL_AUTH_TOKEN);

  if (configuredMode === "local") {
    return hasLocalToken ? "local" : "unconfigured";
  }

  if (configuredMode === "clerk") {
    return hasClerkKey ? "clerk" : "unconfigured";
  }

  return hasClerkKey ? "clerk" : "unconfigured";
}

export function isAppAuthConfigured() {
  return getAppAuthMode() !== "unconfigured";
}
