"use client";

import { useRouter } from "next/navigation";
import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useSyncExternalStore,
} from "react";
import {
  buildOAuthLoginUrl,
  buildOAuthLogoutUrl,
  type AppAuthMode,
} from "@/lib/auth-config";
import {
  clearSessionToken,
  readSessionToken,
  subscribeSessionToken,
  writeSessionToken,
} from "@/lib/auth-storage";

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
const noopSnapshot = () => null;

type AppAuthProviderProps = {
  children: React.ReactNode;
  mode: AppAuthMode;
  localToken?: string;
};

export function AppAuthProvider({ children, mode, localToken }: AppAuthProviderProps) {
  const router = useRouter();

  const subscribe = mode === "oauth" ? subscribeSessionToken : noopSubscribe;
  const getSnapshot = mode === "oauth" ? readSessionToken : noopSnapshot;
  const oauthToken = useSyncExternalStore(
    subscribe,
    getSnapshot,
    noopSnapshot,
  );

  const getToken = useCallback(async (): Promise<string | null> => {
    if (mode === "local") return localToken ?? null;
    if (mode === "oauth") return readSessionToken();
    return null;
  }, [mode, localToken]);

  const signIn = useCallback((returnPath?: string) => {
    const target = returnPath ?? defaultReturnPath();
    window.location.href = buildOAuthLoginUrl(target);
  }, []);

  const signOut = useCallback(() => {
    if (mode === "oauth") {
      clearSessionToken();
      window.location.href = buildOAuthLogoutUrl();
    } else {
      router.push("/");
    }
  }, [mode, router]);

  const isSignedIn =
    mode === "local"
      ? Boolean(localToken)
      : mode === "oauth"
        ? Boolean(oauthToken)
        : false;

  const value = useMemo<AppAuthContextValue>(
    () => ({
      mode,
      isLoaded: true,
      isSignedIn,
      getToken,
      signIn,
      signOut,
    }),
    [mode, isSignedIn, getToken, signIn, signOut],
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

export { writeSessionToken };
