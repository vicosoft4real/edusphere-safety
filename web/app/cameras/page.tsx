import Link from "next/link";
import { redirect } from "next/navigation";
import type { Camera } from "@/lib/api";
import { safetyFetch, signedIn } from "@/lib/server";
import { CameraForm, CameraRowActions } from "./camera-form";

export default async function CamerasPage() {
  if (!(await signedIn())) redirect("/login");
  const res = await safetyFetch<{ items: Camera[] }>("/v1/cameras");
  if (res.status === 401) redirect("/login");

  return (
    <main className="mx-auto max-w-4xl px-4 py-8">
      <header className="flex items-baseline justify-between">
        <h1 className="text-2xl font-semibold">Cameras</h1>
        <Link href="/" className="text-sm underline">
          Overview
        </Link>
      </header>
      <CameraForm />
      {!res.data ? (
        <p role="alert" className="mt-6 rounded-md bg-red-50 p-4 text-sm text-red-800">
          Could not load cameras. Check connectivity and try again.
        </p>
      ) : res.data.items.length === 0 ? (
        <p className="mt-6 text-sm text-stone-600">
          No cameras configured. Add one above — use manual RTSP details or ONVIF discovery at the
          edge. Camera passwords stay on the edge appliance and are never shown here.
        </p>
      ) : (
        <ul className="mt-6 divide-y divide-stone-200 rounded-lg border border-stone-200 bg-white">
          {res.data.items.map((c) => (
            <li key={c.id} className="px-4 py-3 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span>
                  <span className="font-mono font-medium">{c.id}</span>
                  <span className="ml-2 text-stone-500">
                    {c.campusId} · {c.mode} · rtsp://{c.host}:{c.port}
                    {c.path}
                  </span>
                </span>
                <CameraRowActions camera={c} />
              </div>
              <p className="mt-1 text-xs text-stone-500">
                {c.lastTestAt
                  ? `Last edge connectivity test: ${c.lastTestOk ? "ok" : "failed"} — ${c.lastTestDetail ?? ""}`
                  : "No connectivity test reported yet by the edge device."}
              </p>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
