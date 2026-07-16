import { ConfigurationRequired } from "@/components/configuration-required";
import { DashboardClient } from "./dashboard-client";

export default function DashboardPage() {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  return <DashboardClient />;
}
