import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { TranscriptReviewClient } from "./transcript-review-client";

export default async function TranscriptPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  if (!isAppAuthConfigured()) return <ConfigurationRequired />;
  const { id } = await params;
  return <TranscriptReviewClient projectId={id} />;
}
