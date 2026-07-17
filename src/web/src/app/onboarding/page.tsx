import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { OnboardingClient } from "./onboarding-client";

export default function OnboardingPage() {
  if (!isAppAuthConfigured()) {
    return <ConfigurationRequired />;
  }

  return <OnboardingClient />;
}
