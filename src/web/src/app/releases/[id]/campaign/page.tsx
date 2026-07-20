import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { CampaignReviewClient } from "./campaign-review-client";

export default async function CampaignPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  if (!isAppAuthConfigured()) return <ConfigurationRequired />;
  const { id } = await params;
  return <CampaignReviewClient projectId={id} />;
}
