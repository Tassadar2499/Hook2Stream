import { ConfigurationRequired } from "@/components/configuration-required";
import { OnboardingClient } from "./onboarding-client";

export default function OnboardingPage() {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  return <OnboardingClient />;
}
