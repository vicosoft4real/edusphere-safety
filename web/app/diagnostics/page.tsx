import Link from "next/link";
import { redirect } from "next/navigation";
import type { AccessLogItem, Camera, DeviceItem } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";

function ageOf(iso: string | null): string {
  if (!iso) return "never";
  const min = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min} min ago`;
  return `${Math.round(min / 60)} h ago`;
}

export default async function DiagnosticsPage() {
  if (!(await signedIn())) redirect("/login");
  const [devicesRes, camerasRes, logRes] = await Promise.all([
    safetyFetch<{ items: DeviceItem[] }>("/v1/devices"),
    safetyFetch<{ items: Camera[] }>("/v1/cameras"),
    safetyFetch<{ items: AccessLogItem[] }>("/v1/evidence/access-log"),
  ]);
  if (
    devicesRes.status === 401 ||
    camerasRes.status === 401 ||
    logRes.status === 401
  )
    redirect("/login");
  if (
    devicesRes.status === 403 ||
    camerasRes.status === 403 ||
    logRes.status === 403
  )
    redirect("/denied");

  const failed = [devicesRes, camerasRes, logRes].some((r) => !r.data);

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Diagnostics</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      {failed ? (
        <p role="alert" className="mt-6 rounded-md bg-red-50 p-4 text-sm text-red-800">
          Some diagnostics could not be loaded. Check connectivity and try again.
        </p>
      ) : (
        <>
          <section aria-label="Edge devices" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
            <h2 className="font-medium">Edge devices</h2>
            {(devicesRes.data!.items.length === 0) ? (
              <p className="mt-2 text-sm text-stone-600">No devices enrolled.</p>
            ) : (
              <dl className="mt-2 divide-y divide-stone-100 text-sm">
                {devicesRes.data!.items.map((d) => (
                  <div key={d.id} className="grid grid-cols-2 gap-1 py-2 md:grid-cols-4">
                    <dt className="font-mono font-medium">{d.id}</dt>
                    <dd>heartbeat {ageOf(d.lastHeartbeatAt)}</dd>
                    <dd>
                      {d.softwareVersion || "?"} / {d.modelVersion || "?"} / cfg v{d.configVersion}
                      {d.staleConfig ? " (stale)" : ""}
                    </dd>
                    <dd>{d.revoked ? "revoked" : "active"}</dd>
                  </div>
                ))}
              </dl>
            )}
          </section>

          <section aria-label="Cameras" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
            <h2 className="font-medium">Cameras</h2>
            {camerasRes.data!.items.length === 0 ? (
              <p className="mt-2 text-sm text-stone-600">No cameras configured.</p>
            ) : (
              <ul className="mt-2 divide-y divide-stone-100 text-sm">
                {camerasRes.data!.items.map((c) => (
                  <li key={c.id} className="py-2">
                    <span className="font-mono font-medium">{c.id}</span>
                    <span className="ml-2 text-stone-500">
                      {c.lastTestAt
                        ? `last edge test ${c.lastTestOk ? "passed" : "FAILED"} (${ageOf(c.lastTestAt)}): ${c.lastTestDetail ?? ""}`
                        : "no connectivity test reported — treat as unmonitored"}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section aria-label="Evidence access log" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
            <h2 className="font-medium">Evidence access log</h2>
            {logRes.data!.items.length === 0 ? (
              <p className="mt-2 text-sm text-stone-600">No evidence views recorded yet.</p>
            ) : (
              <ul className="mt-2 divide-y divide-stone-100 text-sm">
                {logRes.data!.items.slice(-20).reverse().map((r, i) => (
                  <li key={i} className="py-1 font-mono text-xs">
                    {r.accessedAt} · {r.actorId} · {r.objectKey}
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section aria-label="Retention" className="mt-6 rounded-lg border border-stone-200 bg-white p-4 text-sm">
            <h2 className="font-medium">Retention</h2>
            <p className="mt-2 text-stone-600">
              Media retention is set per deployment (Worker <code>Retention:ClipDays</code> /{" "}
              <code>SnapshotDays</code> / <code>AuditDays</code>, defaults 30 / 90 / 365 days) and
              cannot be changed from this console — per-tenant retention is a later release. Expired
              media is deleted by the nightly job; incident records are kept. See operator notes for
              details.
            </p>
          </section>
        </>
      )}
    </main>
  );
}
