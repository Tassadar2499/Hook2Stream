import { ConfigurationRequired } from "@/components/configuration-required";
import { ReleaseSetupClient } from "./release-setup-client";

export default async function ReleasePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  const { id } = await params;
  return <ReleaseSetupClient projectId={id} />;
}
