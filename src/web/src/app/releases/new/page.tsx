import { ConfigurationRequired } from "@/components/configuration-required";
import { NewReleaseClient } from "./new-release-client";

export default function NewReleasePage() {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  return <NewReleaseClient />;
}
