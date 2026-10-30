import Link from "next/link";
import { redirect } from "next/navigation";
import type { Incident } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";

const STATES = ["New", "Acknowledged", "Investigating", "Resolved", "Dismissed"] as const;

export default async function InboxPage({
  searchParams,
}: {
  searchParams: Promise<{ state?: string }>;
}) {
  if (!(await signedIn())) redirect("/login");
  const { state } = await searchParams;
  const active = STATES.includes(state as (typeof STATES)[number]) ? state! : "New";
  const res = await safetyFetch<{ items: Incident[] }>("/v1/incidents?limit=200&offset=0");
  if (res.status === 401) redirect("/login");
  if (res.status === 403) redirect("/denied");

  if (!res.data) {
    return (
      <main className="mx-auto max-w-4xl px-4 py-8">
        <p role="alert" className="rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load incidents. Check connectivity and try again.
        </p>
      </main>
    );
  }
  const items = res.data.items.filter((i) => i.state === active);

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Incidents</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      <div className="mt-4 flex flex-wrap gap-2" role="tablist" aria-label="Filter by state">
        {STATES.map((s) => (
          <Link
            key={s}
            role="tab"
            aria-selected={s === active}
            href={`/incidents?state=${s}`}
            className={`rounded-full px-3 py-1 text-sm ${
              s === active ? "bg-stone-900 font-medium text-white" : "bg-white text-stone-700 ring-1 ring-stone-300"
            }`}
          >
            {s}
          </Link>
        ))}
      </div>
      {items.length === 0 ? (
        <p className="mt-6 text-sm text-stone-600">No {active.toLowerCase()} incidents.</p>
      ) : (
        <ul className="mt-4 divide-y divide-stone-200 rounded-lg border border-stone-200 bg-white">
          {items.map((i) => (
            <li key={i.id}>
              <Link href={`/incidents/${i.id}`} className="block px-4 py-3 hover:bg-stone-50">
                <span className="font-medium">{i.eventType}</span>
                <span className="ml-2 text-sm text-stone-500">
                  {i.cameraId} · {i.campusId} · v{i.version}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
