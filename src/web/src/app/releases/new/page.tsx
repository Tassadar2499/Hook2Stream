import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { NewReleaseClient } from "./new-release-client";

export default function NewReleasePage() {
  if (!isAppAuthConfigured()) {
    return <ConfigurationRequired />;
  }

  return <NewReleaseClient />;
}
