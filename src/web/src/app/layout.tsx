import type { Metadata } from "next";
import { AppAuthProvider } from "@/components/app-auth-provider";
import { getAppAuthMode } from "@/lib/auth-config";
import "./globals.css";

export const metadata: Metadata = {
  title: "Hook2Stream — one song, three weeks of shorts",
  description:
    "Turn one song into a coherent 21-day campaign of ready-to-post lyric shorts.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  const authMode = getAppAuthMode();
  const localToken = process.env.NEXT_PUBLIC_LOCAL_AUTH_TOKEN;

  return (
    <html lang="en" data-scroll-behavior="smooth">
      <body>
        <AppAuthProvider mode={authMode} localToken={localToken}>
          <div className="noise" aria-hidden="true" />
          {children}
        </AppAuthProvider>
      </body>
    </html>
  );
}
