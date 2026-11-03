import Link from "next/link";
import { redirect } from "next/navigation";
import type { Incident } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";
import { TransitionButtons } from "./transition-buttons";
import { IncidentBriefing } from "./incident-briefing";

const NEXT: Record<Incident["state"], Incident["state"][]> = {
  New: ["Acknowledged"],
  Acknowledged: ["Investigating", "Dismissed"],
  Investigating: ["Resolved", "Dismissed"],
  Resolved: [],
  Dismissed: [],
};

export default async function IncidentPage({ params }: { params: Promise<{ id: string }> }) {
  if (!(await signedIn())) redirect("/login");
  const { id } = await params;
  const res = await safetyFetch<Incident>(`/v1/incidents/${id}`);
  if (res.status === 401) redirect("/login");
  if (res.status === 403) redirect("/denied");
  if (res.status === 404 || !res.data) {
    return (
      <main className="mx-auto max-w-2xl px-4 py-8">
        <p className="rounded-md bg-stone-100 p-4 text-sm">Incident not found.</p>
        <Link href="/incidents" className="mt-4 inline-block text-sm underline">
          Back to inbox
        </Link>
      </main>
    );
  }
  if (!res.data) {
    return (
      <main className="mx-auto max-w-2xl px-4 py-8">
        <p role="alert" className="rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load this incident. Try again.
        </p>
      </main>
    );
  }
  const incident = res.data;

  return (
    <main className="mx-auto max-w-2xl px-4 py-8">
      <Link href="/incidents" className="text-sm underline">
        Back to inbox
      </Link>
      <h1 className="mt-2 text-2xl font-semibold">{incident.eventType}</h1>
      <dl className="mt-4 grid grid-cols-2 gap-2 rounded-lg border border-stone-200 bg-white p-4 text-sm">
        <dt className="text-stone-500">State</dt>
        <dd className="font-medium">{incident.state}</dd>
        <dt className="text-stone-500">Camera</dt>
        <dd className="font-mono">{incident.cameraId}</dd>
        <dt className="text-stone-500">Campus</dt>
        <dd>{incident.campusId}</dd>
        <dt className="text-stone-500">Version</dt>
        <dd>#{incident.version}</dd>
      </dl>
      <p className="mt-3 text-xs text-stone-500">
        Detection is assistive: it cannot guarantee prevention of injury or replace supervision.
      </p>
      <TransitionButtons id={incident.id} version={incident.version} next={NEXT[incident.state]} />
      <IncidentBriefing key={incident.id} id={incident.id} version={incident.version} />
      <section aria-label="Evidence" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
        <h2 className="font-medium">Evidence</h2>
        <p className="mt-1 text-sm text-stone-600">
          Snapshots and clips appear here once the edge device uploads them. Playback opens the
          short-lived mediated link; every view is audit-logged.
        </p>
        {incident.edgeEventId ? (
          <div className="mt-3 flex gap-3 text-sm">
            <a className="underline" href={`/api/v1/evidence/${encodeURIComponent(incident.edgeEventId)}/clip`}>
              Play clip
            </a>
            <a className="underline" href={`/api/v1/evidence/${encodeURIComponent(incident.edgeEventId)}/snapshot`}>
              View snapshot
            </a>
          </div>
        ) : (
          <p className="mt-3 text-sm text-stone-500">No evidence reference recorded for this incident.</p>
        )}
      </section>
    </main>
  );
}
