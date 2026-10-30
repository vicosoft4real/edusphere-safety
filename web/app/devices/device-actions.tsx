"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { api } from "@/lib/api";

export function DeviceActions() {
  const [campusId, setCampusId] = useState("");
  const [code, setCode] = useState<{ code: string; expiresInSeconds: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function createCode() {
    setBusy(true);
    setError(null);
    setCode(null);
    try {
      setCode(await api.enrolCode(campusId.trim() || undefined));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not create a code.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="Enrol a device" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
      <h2 className="font-medium">Enrol a new edge device</h2>
      <div className="mt-3 flex flex-wrap items-end gap-2">
        <div>
          <label htmlFor="campus" className="block text-sm font-medium">
            Campus (optional)
          </label>
          <input
            id="campus"
            value={campusId}
            onChange={(e) => setCampusId(e.target.value)}
            placeholder="campus-a"
            className="mt-1 rounded-md border border-stone-300 px-3 py-1.5 text-sm"
          />
        </div>
        <button
          onClick={createCode}
          disabled={busy}
          className="rounded-md bg-stone-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
        >
          {busy ? "Creating…" : "Create enrolment code"}
        </button>
      </div>
      {error && (
        <p role="alert" className="mt-2 text-sm text-red-700">
          {error}
        </p>
      )}
      {code && (
        <div className="mt-3 rounded-md bg-amber-50 p-3 text-sm">
          <p>
            Code (shown once — copy it now, expires in {Math.round(code.expiresInSeconds / 60)} min):
          </p>
          <p className="mt-1 font-mono text-base font-semibold break-all">{code.code}</p>
        </div>
      )}
    </section>
  );
}

export function RevokeClient({ id }: { id: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);

  async function revoke() {
    if (!window.confirm(`Revoke device ${id}? It will stop being trusted immediately.`)) return;
    setBusy(true);
    try {
      await api.revoke(id);
      router.refresh();
    } catch (e) {
      window.alert(e instanceof Error ? e.message : "Revoke failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <button
      onClick={revoke}
      disabled={busy}
      className="rounded-md px-2 py-1 text-sm text-red-700 underline disabled:opacity-50"
    >
      {busy ? "Revoking…" : "Revoke"}
    </button>
  );
}
