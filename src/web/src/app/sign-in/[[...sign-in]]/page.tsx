import { SignIn } from "@clerk/nextjs";
import { redirect } from "next/navigation";
import { ConfigurationRequired } from "@/components/configuration-required";
import { getAppAuthMode } from "@/lib/auth-config";

export default function SignInPage() {
  const authMode = getAppAuthMode();
  if (authMode === "local") {
    redirect("/dashboard");
  }
  if (authMode === "unconfigured") {
    return <ConfigurationRequired />;
  }

  return (
    <main className="shell grid min-h-screen place-items-center py-10">
      <SignIn />
    </main>
  );
}
