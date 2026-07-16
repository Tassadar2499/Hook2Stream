import { ConfigurationRequired } from "@/components/configuration-required";
import { SiteHeader } from "@/components/site-header";

export default function SetupPage() {
  return (
    <>
      <SiteHeader />
      <ConfigurationRequired />
    </>
  );
}
