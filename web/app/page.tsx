import Link from "next/link";
import { redirect } from "next/navigation";
import type { DeviceItem, Incident } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";

function deviceStatus(d: DeviceItem): { label: string; tone: string } {
  if (d.revoked) return { label: "Revoked", tone: "bg-stone-200 text-stone-700" };
  if (!d.lastHeartbeatAt) return { label: "Never seen", tone: "bg-amber-100 text-amber-900" };
  const ageMin = (Date.now() - new Date(d.lastHeartbeatAt).getTime()) / 60000;
  if (ageMin > 10) return { label: "Offline?", tone: "bg-red-100 text-red-900" };
  if (d.staleConfig) return { label: "Config stale", tone: "bg-amber-100 text-amber-900" };
  return { label: "Healthy", tone: "bg-emerald-100 text-emerald-900" };
}

export default async function OverviewPage() {
  if (!(await signedIn())) redirect("/login");
  const [devicesRes, incidentsRes] = await Promise.all([
    safetyFetch<{ items: DeviceItem[] }>("/v1/devices"),
    safetyFetch<{ items: Incident[] }>("/v1/incidents?limit=50&offset=0"),
  ]);
  if (devicesRes.status === 401 || incidentsRes.status === 401) redirect("/login");
  if (devicesRes.status === 403 || incidentsRes.status === 403) redirect("/denied");

  const devices = devicesRes.data?.items ?? [];
  const incidents = incidentsRes.data?.items ?? [];
  const open = incidents.filter((i) => i.state === "New" || i.state === "Acknowledged" || i.state === "Investigating");
  const degraded = devices.filter((d) => deviceStatus(d).label !== "Healthy");

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Safety overview</h1>
        <nav className="space-x-4 text-sm">
          <Link href="/incidents" className="underline">
            Incidents
          </Link>
          <Link href="/devices" className="underline">
            Devices
          </Link>
          <Link href="/cameras" className="underline">
            Cameras
          </Link>
          <Link href="/rules" className="underline">
            Rules
          </Link>
          <Link href="/policies" className="underline">
            Notifications
          </Link>
          <Link href="/diagnostics" className="underline">
            Diagnostics
          </Link>
          <Link href="/login" className="text-stone-500">
            Switch user
          </Link>
        </nav>
      </header>

      {!devicesRes.data || !incidentsRes.data ? (
        <p role="alert" className="mt-6 rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load status from the safety service. Check connectivity and try again.
        </p>
      ) : (
        <>
          <section aria-label="Coverage" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
            <h2 className="font-medium">Monitoring coverage</h2>
            {devices.length === 0 ? (
              <p className="mt-2 text-sm text-stone-600">No edge devices enrolled. Nothing is monitored yet.</p>
            ) : degraded.length === devices.length ? (
              <p className="mt-2 text-sm font-medium text-red-800">
                No healthy device reporting — this school is not actively monitored right now.
              </p>
            ) : (
              <p className="mt-2 text-sm text-stone-700">
                {devices.length - degraded.length} of {devices.length} devices reporting.{" "}
                {open.length} open incident{open.length === 1 ? "" : "s"}.
              </p>
            )}
            <ul className="mt-3 divide-y divide-stone-100">
              {devices.map((d) => {
                const s = deviceStatus(d);
                return (
                  <li key={d.id} className="flex items-center justify-between py-2 text-sm">
                    <span>
                      <span className="font-mono">{d.id}</span>
                      <span className="ml-2 text-stone-500">{d.campusId ?? "no campus"}</span>
                    </span>
                    <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${s.tone}`}>{s.label}</span>
                  </li>
                );
              })}
            </ul>
            <p className="mt-2 text-xs text-stone-500">
              “Offline?” means no heartbeat for over 10 minutes. A quiet device is never shown as monitored.
            </p>
          </section>

          <section aria-label="Open incidents" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
            <div className="flex items-baseline justify-between">
              <h2 className="font-medium">Open incidents</h2>
              <Link href="/incidents" className="text-sm underline">
                View all
              </Link>
            </div>
            {open.length === 0 ? (
              <p className="mt-2 text-sm text-stone-600">No open incidents.</p>
            ) : (
              <ul className="mt-3 divide-y divide-stone-100">
                {open.slice(0, 5).map((i) => (
                  <li key={i.id} className="py-2 text-sm">
                    <Link href={`/incidents/${i.id}`} className="underline">
                      {i.eventType}
                    </Link>
                    <span className="ml-2 text-stone-500">
                      {i.cameraId} · {i.campusId} · {i.state}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </main>
  );
}
