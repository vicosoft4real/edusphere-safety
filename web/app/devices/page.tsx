import Link from "next/link";
import { redirect } from "next/navigation";
import type { DeviceItem } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";
import { DeviceActions, RevokeClient } from "./device-actions";

export default async function DevicesPage() {
  if (!(await signedIn())) redirect("/login");
  const res = await safetyFetch<{ items: DeviceItem[] }>("/v1/devices");
  if (res.status === 401) redirect("/login");
  if (res.status === 403) redirect("/denied");

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Edge devices</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      <DeviceActions />
      {!res.data ? (
        <p role="alert" className="mt-6 rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load devices. Check connectivity and try again.
        </p>
      ) : res.data.items.length === 0 ? (
        <p className="mt-6 text-sm text-stone-600">
          No devices enrolled. Create an enrolment code below and enter it on the edge appliance —
          codes are single-use and expire after 15 minutes.
        </p>
      ) : (
        <ul className="mt-6 divide-y divide-stone-200 rounded-lg border border-stone-200 bg-white">
          {res.data.items.map((d) => (
            <li key={d.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-sm">
              <span>
                <span className="font-mono font-medium">{d.id}</span>
                <span className="ml-2 text-stone-500">
                  {d.campusId ?? "no campus"} · {d.softwareVersion || "unknown software"} · config v
                  {d.configVersion}
                  {d.staleConfig ? " (stale)" : ""}
                  {d.revoked ? " · revoked" : ""}
                </span>
              </span>
              {!d.revoked && <RevokeButton id={d.id} />}
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}

function RevokeButton({ id }: { id: string }) {
  return <RevokeClient id={id} />;
}
