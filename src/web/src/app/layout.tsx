import type { Metadata } from "next";
import { ClerkProvider } from "@clerk/nextjs";
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
  const publishableKey = process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY;
  const content = (
    <>
      <div className="noise" aria-hidden="true" />
      {children}
    </>
  );

  return (
    <html lang="en" data-scroll-behavior="smooth">
      <body>
        {publishableKey ? (
          <ClerkProvider publishableKey={publishableKey}>{content}</ClerkProvider>
        ) : (
          content
        )}
      </body>
    </html>
  );
}
