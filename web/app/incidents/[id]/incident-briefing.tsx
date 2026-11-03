"use client";

import { useState } from "react";

interface Briefing {
  summary: string;
  reviewSteps: string[];
  unknowns: string[];
  model: string;
  basis: string;
  incidentVersion: number;
}

export function IncidentBriefing({ id, version }: { id: string; version: number }) {
  const [briefing, setBriefing] = useState<Briefing | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function generate() {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(`/api/briefing/${id}`, { method: "POST" });
      const body = await res.json();
      if (!res.ok) throw new Error(body.message ?? "Briefing unavailable.");
      setBriefing(body as Briefing);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Briefing unavailable.");
    } finally {
      setBusy(false);
    }
  }
  return (
    <section aria-label="Llama incident briefing" className="mt-6 rounded-lg border border-emerald-200 bg-emerald-50 p-4 text-sm">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="font-medium">Incident briefing</h2>
          <p className="mt-0.5 text-xs text-emerald-900">Built with Meta Llama · local inference</p>
        </div>
        <button onClick={generate} disabled={busy} className="rounded-md bg-emerald-900 px-3 py-2 font-medium text-white disabled:opacity-50">
          {busy ? "Llama is drafting…" : briefing ? "Regenerate briefing" : "Generate briefing"}
        </button>
      </div>
      <p className="mt-2 text-xs text-stone-600">Drafts a summary and review checklist from incident metadata. Staff verify the evidence and decide the response.</p>
      {busy && <p role="status" className="mt-3 text-emerald-900">Running the local model. The first request may take longer while it loads.</p>}
      {error && <p role="alert" className="mt-3 text-red-800">{error}</p>}
      {briefing && (
        <div className="mt-4 space-y-3" aria-live="polite">
          {briefing.incidentVersion !== version && <p className="font-medium text-amber-900">The incident has changed. Regenerate to use its current state.</p>}
          <p>{briefing.summary}</p>
          <h3 className="font-medium">Review checklist</h3>
          <ol className="list-decimal space-y-1 pl-5">{briefing.reviewSteps.map((step,i)=><li key={i}>{step}</li>)}</ol>
          <h3 className="font-medium">What is unknown</h3>
          <ul className="list-disc space-y-1 pl-5">{briefing.unknowns.map((item,i)=><li key={i}>{item}</li>)}</ul>
          <p className="border-t border-emerald-200 pt-2 text-xs text-stone-600">{briefing.model} · incident v{briefing.incidentVersion}. {briefing.basis} AI-generated draft; verify before use.</p>
        </div>
      )}
    </section>
  );
}
