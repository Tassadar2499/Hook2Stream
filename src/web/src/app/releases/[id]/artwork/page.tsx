import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { ArtworkReviewClient } from "./artwork-review-client";

export default async function ArtworkPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  if (!isAppAuthConfigured()) return <ConfigurationRequired />;
  const { id } = await params;
  return <ArtworkReviewClient projectId={id} />;
}
