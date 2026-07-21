const TOKEN_STORAGE_KEY = "h2s.session.token";
const EXPIRES_AT_STORAGE_KEY = "h2s.session.expires_at";

const listeners = new Set<() => void>();

function notifyListeners() {
  for (const listener of listeners) {
    listener();
  }
}

export function subscribeSessionToken(listener: () => void) {
  listeners.add(listener);
  if (typeof window !== "undefined") {
    window.addEventListener("storage", handleStorage);
  }
  return () => {
    listeners.delete(listener);
    if (typeof window !== "undefined") {
      window.removeEventListener("storage", handleStorage);
    }
  };
}

function handleStorage(event: StorageEvent) {
  if (event.key === TOKEN_STORAGE_KEY || event.key === EXPIRES_AT_STORAGE_KEY) {
    notifyListeners();
  }
}

export function readSessionToken(): string | null {
  if (typeof window === "undefined") return null;
  const token = window.localStorage.getItem(TOKEN_STORAGE_KEY);
  if (!token) return null;
  if (isSessionExpired()) {
    clearSessionToken();
    return null;
  }
  return token;
}

export function writeSessionToken(token: string, expiresAt: string) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(TOKEN_STORAGE_KEY, token);
  window.localStorage.setItem(EXPIRES_AT_STORAGE_KEY, expiresAt);
  notifyListeners();
}

export function clearSessionToken() {
  if (typeof window === "undefined") return;
  window.localStorage.removeItem(TOKEN_STORAGE_KEY);
  window.localStorage.removeItem(EXPIRES_AT_STORAGE_KEY);
  notifyListeners();
}

function isSessionExpired(): boolean {
  const stored = window.localStorage.getItem(EXPIRES_AT_STORAGE_KEY);
  if (!stored) return true;
  const expiresAt = Date.parse(stored);
  if (Number.isNaN(expiresAt)) return true;
  return expiresAt <= Date.now();
}
