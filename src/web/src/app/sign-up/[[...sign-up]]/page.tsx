import { SignUp } from "@clerk/nextjs";
import { ConfigurationRequired } from "@/components/configuration-required";

export default function SignUpPage() {
  if (!process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY) {
    return <ConfigurationRequired />;
  }

  return (
    <main className="shell grid min-h-screen place-items-center py-10">
      <SignUp />
    </main>
  );
}
