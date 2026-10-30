"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { api, type Incident } from "@/lib/api";

export function TransitionButtons({
  id,
  version,
  next,
}: {
  id: string;
  version: number;
  next: Incident["state"][];
}) {
  const router = useRouter();
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  if (next.length === 0) return null;

  async function go(target: Incident["state"]) {
    if (target === "Dismissed" && reason.trim() === "") {
      setError("A reason is required to dismiss.");
      return;
    }
    setBusy(target);
    setError(null);
    try {
      await api.transition(id, target, reason.trim() || undefined, version);
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Transition failed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <section aria-label="Respond" className="mt-6 rounded-lg border border-stone-200 bg-white p-4">
      <h2 className="font-medium">Respond</h2>
      <div className="mt-3 flex flex-wrap gap-2">
        {next.map((s) => (
          <button
            key={s}
            onClick={() => go(s)}
            disabled={busy !== null}
            className="rounded-md bg-stone-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
          >
            {busy === s ? "Saving…" : s === "Acknowledged" ? "Acknowledge" : s}
          </button>
        ))}
      </div>
      {(next.includes("Dismissed") || next.includes("Investigating")) && (
        <div className="mt-3">
          <label htmlFor="reason" className="block text-sm font-medium">
            Note / reason
          </label>
          <input
            id="reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="Required when dismissing"
            className="mt-1 w-full rounded-md border border-stone-300 px-3 py-2 text-sm"
          />
        </div>
      )}
      {error && (
        <p role="alert" className="mt-2 text-sm text-red-700">
          {error}
        </p>
      )}
    </section>
  );
}
