"use client";

import { ClerkProvider, useAuth as useClerkAuth } from "@clerk/nextjs";
import { createContext, useContext, useMemo } from "react";
import type { AppAuthMode } from "@/lib/auth-config";

type AppAuthContextValue = {
  mode: AppAuthMode;
  isLoaded: boolean;
  isSignedIn: boolean;
  getToken: () => Promise<string | null>;
};

const AppAuthContext = createContext<AppAuthContextValue | undefined>(undefined);

type AppAuthProviderProps = {
  children: React.ReactNode;
  mode: AppAuthMode;
  clerkPublishableKey?: string;
  localToken?: string;
};

export function AppAuthProvider({
  children,
  mode,
  clerkPublishableKey,
  localToken,
}: AppAuthProviderProps) {
  if (mode === "clerk" && clerkPublishableKey) {
    return (
      <ClerkProvider publishableKey={clerkPublishableKey}>
        <ClerkAuthBridge>{children}</ClerkAuthBridge>
      </ClerkProvider>
    );
  }

  const value: AppAuthContextValue =
    mode === "local" && localToken
      ? {
          mode: "local",
          isLoaded: true,
          isSignedIn: true,
          getToken: async () => localToken,
        }
      : {
          mode: "unconfigured",
          isLoaded: true,
          isSignedIn: false,
          getToken: async () => null,
        };

  return <AppAuthContext.Provider value={value}>{children}</AppAuthContext.Provider>;
}

function ClerkAuthBridge({ children }: { children: React.ReactNode }) {
  const { getToken, isLoaded, isSignedIn } = useClerkAuth();
  const value = useMemo<AppAuthContextValue>(
    () => ({
      mode: "clerk",
      isLoaded,
      isSignedIn: Boolean(isSignedIn),
      getToken: () => getToken(),
    }),
    [getToken, isLoaded, isSignedIn],
  );

  return <AppAuthContext.Provider value={value}>{children}</AppAuthContext.Provider>;
}

export function useAppAuth() {
  const value = useContext(AppAuthContext);
  if (!value) {
    throw new Error("useAppAuth must be used within AppAuthProvider.");
  }
  return value;
}
