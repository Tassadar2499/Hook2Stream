import { ConfigurationRequired } from "@/components/configuration-required";
import { isAppAuthConfigured } from "@/lib/auth-config";
import { BrandKitClient } from "./brand-kit-client";

export default function BrandKitPage() {
  if (!isAppAuthConfigured()) {
    return <ConfigurationRequired />;
  }

  return <BrandKitClient />;
}
