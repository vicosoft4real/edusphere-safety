import Link from "next/link";
import { redirect } from "next/navigation";
import type { Policy } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";
import { PolicyEditor } from "./policy-editor";

export default async function PoliciesPage() {
  if (!(await signedIn())) redirect("/login");
  const res = await safetyFetch<{ items: Policy[] }>("/v1/config/policies");
  if (res.status === 401) redirect("/login");

  const current = res.data?.items.find((p) => p.id === "default") ?? null;

  return (
    <main className="mx-auto max-w-3xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Notifications & escalation</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      <p className="mt-2 text-sm text-stone-600">
        The default policy decides who is emailed first and who is escalated to, and after how
        long without acknowledgement. Acknowledging an incident cancels pending escalation.
        Delivery is at-least-once: a retry can occasionally duplicate an email.
      </p>
      {!res.data ? (
        <p role="alert" className="mt-4 rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load policies. Check connectivity and try again.
        </p>
      ) : (
        <PolicyEditor initial={current} />
      )}
    </main>
  );
}
