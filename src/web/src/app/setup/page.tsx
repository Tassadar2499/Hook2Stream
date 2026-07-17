import { ConfigurationRequired } from "@/components/configuration-required";
import { SiteHeader } from "@/components/site-header";
import { redirect } from "next/navigation";
import { isAppAuthConfigured } from "@/lib/auth-config";

export default function SetupPage() {
  if (isAppAuthConfigured()) {
    redirect("/dashboard");
  }

  return (
    <>
      <SiteHeader />
      <ConfigurationRequired />
    </>
  );
}
