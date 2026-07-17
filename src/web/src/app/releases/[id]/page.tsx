import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { ReleaseSetupClient } from "./release-setup-client";

export default async function ReleasePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  if (!isAppAuthConfigured()) {
    return <ConfigurationRequired />;
  }

  const { id } = await params;
  return <ReleaseSetupClient projectId={id} />;
}
