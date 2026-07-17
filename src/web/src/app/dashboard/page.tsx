import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { DashboardClient } from "./dashboard-client";

export default function DashboardPage() {
  if (!isAppAuthConfigured()) {
    return <ConfigurationRequired />;
  }

  return <DashboardClient />;
}
