"use client";

import { useRouter } from "next/navigation";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useSyncExternalStore,
} from "react";
import {
  buildOAuthLoginUrl,
  type AppAuthMode,
} from "@/lib/auth-config";
import {
  COOKIE_SESSION_MARKER,
  logoutOAuthSession,
  readOAuthSession,
  readServerOAuthSession,
  refreshOAuthSession,
  subscribeOAuthSession,
} from "@/lib/auth-session";

type AppAuthContextValue = {
  mode: AppAuthMode;
  isLoaded: boolean;
  isSignedIn: boolean;
  getToken: () => Promise<string | null>;
  signIn: (returnPath?: string) => void;
  signOut: () => void;
};

const AppAuthContext = createContext<AppAuthContextValue | undefined>(undefined);

const noopSubscribe = () => () => {};
const localSnapshot = { loaded: true, authenticated: false };
const noopSnapshot = () => localSnapshot;

type AppAuthProviderProps = {
  children: React.ReactNode;
  mode: AppAuthMode;
  localToken?: string;
};

export function AppAuthProvider({ children, mode, localToken }: AppAuthProviderProps) {
  const router = useRouter();

  const subscribe = mode === "oauth" ? subscribeOAuthSession : noopSubscribe;
  const getSnapshot = mode === "oauth" ? readOAuthSession : noopSnapshot;
  const oauthSession = useSyncExternalStore(
    subscribe,
    getSnapshot,
    mode === "oauth" ? readServerOAuthSession : noopSnapshot,
  );

  useEffect(() => {
    if (mode === "oauth") void refreshOAuthSession();
  }, [mode]);

  const getToken = useCallback(async (): Promise<string | null> => {
    if (mode === "local") return localToken ?? null;
    if (mode === "oauth") {
      const current = readOAuthSession().loaded
        ? readOAuthSession()
        : await refreshOAuthSession();
      return current.authenticated ? COOKIE_SESSION_MARKER : null;
    }
    return null;
  }, [mode, localToken]);

  const signIn = useCallback((returnPath?: string) => {
    const target = returnPath ?? defaultReturnPath();
    window.location.href = buildOAuthLoginUrl(target);
  }, []);

  const signOut = useCallback(() => {
    if (mode === "oauth") {
      void logoutOAuthSession().finally(() => {
        router.push("/?auth=signed_out");
        router.refresh();
      });
    } else {
      router.push("/");
    }
  }, [mode, router]);

  const isSignedIn =
    mode === "local"
      ? Boolean(localToken)
      : mode === "oauth"
        ? oauthSession.authenticated
        : false;

  const value = useMemo<AppAuthContextValue>(
    () => ({
      mode,
      isLoaded: mode !== "oauth" || oauthSession.loaded,
      isSignedIn,
      getToken,
      signIn,
      signOut,
    }),
    [mode, oauthSession.loaded, isSignedIn, getToken, signIn, signOut],
  );

  return <AppAuthContext.Provider value={value}>{children}</AppAuthContext.Provider>;
}

function defaultReturnPath() {
  if (typeof window === "undefined") return "/dashboard";
  return window.location.pathname + window.location.search;
}

export function useAppAuth() {
  const value = useContext(AppAuthContext);
  if (!value) {
    throw new Error("useAppAuth must be used within AppAuthProvider.");
  }
  return value;
}
