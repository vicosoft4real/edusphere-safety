import Link from "next/link";
import { redirect } from "next/navigation";
import { safetyFetch, signedIn } from "@/lib/server";
import { BundleEditor } from "./bundle-editor";

export default async function RulesPage() {
  if (!(await signedIn())) redirect("/login");
  const res = await safetyFetch<{ version: number; document: string }>(
    "/v1/config/bundles/latest"
  );
  if (res.status === 401) redirect("/login");

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Detection rules</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      <p className="mt-2 text-sm text-stone-600">
        Zones are normalised coordinates (0–1). Draw on the grid or type coordinates directly —
        both edit the same polygon. Publishing creates a new numbered bundle; edge devices pull
        it on their next check-in. A rule change discards in-progress episodes for that rule.
      </p>
      {res.status === 404 || !res.data ? (
        <BundleEditor initial={null} />
      ) : (
        <BundleEditor initial={{ version: res.data.version, document: res.data.document }} />
      )}
    </main>
  );
}
