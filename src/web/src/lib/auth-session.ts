const apiBaseUrl =
  process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ??
  "http://localhost:5000";

export const COOKIE_SESSION_MARKER = "__hook2stream_cookie_session__";

export type OAuthSessionSnapshot = {
  loaded: boolean;
  authenticated: boolean;
  subject?: string;
  email?: string;
  displayName?: string;
  expiresAt?: string;
  csrfToken?: string;
};

const anonymousSession: OAuthSessionSnapshot = {
  loaded: true,
  authenticated: false,
};
const serverSession: OAuthSessionSnapshot = {
  loaded: false,
  authenticated: false,
};

let session: OAuthSessionSnapshot = serverSession;
let refreshPromise: Promise<OAuthSessionSnapshot> | undefined;
const listeners = new Set<() => void>();

function publish(next: OAuthSessionSnapshot) {
  session = next;
  for (const listener of listeners) listener();
}

export function subscribeOAuthSession(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function readOAuthSession() {
  return session;
}

export function readServerOAuthSession() {
  return serverSession;
}

export function readOAuthCsrfToken() {
  return session.authenticated ? session.csrfToken : undefined;
}

export function markOAuthSessionSignedOut() {
  publish(anonymousSession);
}

export async function refreshOAuthSession(): Promise<OAuthSessionSnapshot> {
  if (refreshPromise) return refreshPromise;

  refreshPromise = fetch(`${apiBaseUrl}/api/v1/auth/session`, {
    method: "GET",
    headers: { Accept: "application/json" },
    credentials: "include",
    cache: "no-store",
  })
    .then(async (response) => {
      if (!response.ok) return anonymousSession;
      const payload = (await response.json()) as {
        authenticated?: boolean;
        subject?: string | null;
        email?: string | null;
        displayName?: string | null;
        expiresAt?: string | null;
        csrfToken?: string | null;
      };
      if (!payload.authenticated || !payload.csrfToken) return anonymousSession;
      return {
        loaded: true,
        authenticated: true,
        subject: payload.subject ?? undefined,
        email: payload.email ?? undefined,
        displayName: payload.displayName ?? undefined,
        expiresAt: payload.expiresAt ?? undefined,
        csrfToken: payload.csrfToken,
      };
    })
    .catch(() => anonymousSession)
    .then((next) => {
      publish(next);
      return next;
    })
    .finally(() => {
      refreshPromise = undefined;
    });

  return refreshPromise;
}

export async function logoutOAuthSession() {
  const headers = new Headers({ Accept: "application/json" });
  if (session.csrfToken) headers.set("X-CSRF-Token", session.csrfToken);

  try {
    await fetch(`${apiBaseUrl}/api/v1/auth/logout`, {
      method: "POST",
      headers,
      credentials: "include",
      cache: "no-store",
    });
  } finally {
    publish(anonymousSession);
  }
}
