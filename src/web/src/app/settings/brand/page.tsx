import { ConfigurationRequired } from "@/components/configuration-required";
import { BrandKitClient } from "./brand-kit-client";

export default function BrandKitPage() {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  return <BrandKitClient />;
}
