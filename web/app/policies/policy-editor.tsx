"use client";

import { useState } from "react";
import { api, type Policy } from "@/lib/api";

interface Level {
  afterSeconds: number;
  recipients: string[];
}

export function PolicyEditor({ initial }: { initial: Policy | null }) {
  const [recipients, setRecipients] = useState((initial?.recipients ?? []).join("\n"));
  const [levels, setLevels] = useState<Level[]>(
    initial?.levels.map((l) => ({ afterSeconds: l.afterSeconds, recipients: [...l.recipients] })) ?? [
      { afterSeconds: 300, recipients: [] },
    ]
  );
  const [error, setError] = useState<string | null>(null);
  const [ok, setOk] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function setLevel(i: number, patch: Partial<Level>) {
    setLevels((ls) => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  }

  async function save() {
    setError(null);
    setOk(null);
    const to = recipients
      .split("\n")
      .map((s) => s.trim())
      .filter(Boolean);
    if (to.length === 0) {
      setError("At least one first-contact recipient is required.");
      return;
    }
    for (const [i, l] of levels.entries()) {
      if (!Number.isFinite(l.afterSeconds) || l.afterSeconds < 0) {
        setError(`Level ${i + 1}: delay must be 0 or more seconds.`);
        return;
      }
    }
    setBusy(true);
    try {
      await api.setPolicy({ id: "default", levels, recipients: to });
      setOk("Default policy saved.");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Save failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mt-4 rounded-lg border border-stone-200 bg-white p-4 text-sm">
      <h2 className="font-medium">Default policy</h2>
      <label className="mt-3 block">
        First-contact recipients, one email per line
        <textarea
          rows={3}
          value={recipients}
          onChange={(e) => setRecipients(e.target.value)}
          placeholder="security@school.example"
          className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
        />
      </label>
      <h3 className="mt-4 font-medium">Escalation levels</h3>
      {levels.map((l, i) => (
        <div key={i} className="mt-2 grid gap-2 rounded-md bg-stone-50 p-3 md:grid-cols-[140px_1fr_auto]">
          <label className="block">
            After (seconds)
            <input
              type="number"
              min="0"
              value={l.afterSeconds}
              onChange={(e) => setLevel(i, { afterSeconds: Number(e.target.value) })}
              className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
            />
          </label>
          <label className="block">
            Recipients (comma-separated emails)
            <input
              value={l.recipients.join(", ")}
              onChange={(e) =>
                setLevel(i, {
                  recipients: e.target.value.split(",").map((s) => s.trim()).filter(Boolean),
                })
              }
              className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
            />
          </label>
          <button
            onClick={() => setLevels((ls) => ls.filter((_, j) => j !== i))}
            className="self-end text-red-700 underline"
          >
            Remove
          </button>
        </div>
      ))}
      <button
        onClick={() => setLevels((ls) => [...ls, { afterSeconds: 900, recipients: [] }])}
        className="mt-2 underline"
      >
        + Add level
      </button>
      {error && (
        <p role="alert" className="mt-2 text-red-700">
          {error}
        </p>
      )}
      {ok && <p className="mt-2 text-emerald-800">{ok}</p>}
      <button
        onClick={save}
        disabled={busy}
        className="mt-3 rounded-md bg-stone-900 px-3 py-1.5 font-medium text-white disabled:opacity-50"
      >
        {busy ? "Saving…" : "Save policy"}
      </button>
    </div>
  );
}
