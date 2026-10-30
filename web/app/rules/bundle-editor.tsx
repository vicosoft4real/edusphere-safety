"use client";

import { useState } from "react";
import { api, type RuleBundle } from "@/lib/api";

const TYPES = ["ZoneIntrusion", "BoundaryCrossing", "Dwell", "PoolZone"];

function starter(): RuleBundle {
  return {
    format: "edusphere-rule-bundle/1",
    bundleVersion: 1,
    modelVersion: "",
    schedules: [{ scheduleId: "night", ianaTimezone: "UTC" }],
    rules: [],
  };
}

function parse(document: string): RuleBundle | null {
  try {
    const b = JSON.parse(document) as RuleBundle;
    if (b.format !== "edusphere-rule-bundle/1" || !Array.isArray(b.rules)) return null;
    return b;
  } catch {
    return null;
  }
}

export function BundleEditor({ initial }: { initial: { version: number; document: string } | null }) {
  const [bundle, setBundle] = useState<RuleBundle>(() =>
    initial ? (parse(initial.document) ?? starter()) : starter()
  );
  const [selected, setSelected] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [ok, setOk] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const rule = bundle.rules[selected];
  const points = (rule?.polygon ?? []) as [number, number][];

  function updateRule(patch: Partial<RuleBundle["rules"][number]>) {
    setBundle((b) => ({
      ...b,
      rules: b.rules.map((r, i) => (i === selected ? { ...r, ...patch } : r)),
    }));
  }

  function addPoint(x: number, y: number) {
    const clamp = (v: number) => Math.min(1, Math.max(0, Math.round(v * 1000) / 1000));
    updateRule({ polygon: [...points, [clamp(x), clamp(y)]] });
  }

  function onCanvasClick(e: React.MouseEvent<SVGSVGElement>) {
    const rect = e.currentTarget.getBoundingClientRect();
    addPoint((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height);
  }

  function addRule() {
    const id = `rule-${bundle.rules.length + 1}`;
    setBundle((b) => ({
      ...b,
      rules: [
        ...b.rules,
        {
          ruleId: id,
          type: "ZoneIntrusion",
          version: 1,
          confidenceThreshold: 0.5,
          persistenceFrames: 3,
          cooldownSeconds: 60,
          polygon: [],
          scheduleId: b.schedules[0]?.scheduleId ?? "night",
        },
      ],
    }));
    setSelected(bundle.rules.length);
  }

  async function publish() {
    setError(null);
    setOk(null);
    if (bundle.rules.length === 0) {
      setError("A bundle needs at least one rule.");
      return;
    }
    for (const r of bundle.rules) {
      if (!r.ruleId.trim() || (r.version ?? 0) < 1) {
        setError(`Rule "${r.ruleId || "?"}": id and version ≥ 1 are required.`);
        return;
      }
      if (!bundle.schedules.some((s) => s.scheduleId === r.scheduleId)) {
        setError(`Rule "${r.ruleId}": unknown schedule "${r.scheduleId}".`);
        return;
      }
    }
    setBusy(true);
    try {
      const next = { ...bundle, bundleVersion: (initial?.version ?? 0) + 1 };
      const res = await api.publishBundle(JSON.stringify(next));
      setOk(`Published as bundle v${res.version}. Edge devices pick it up on next check-in.`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Publish failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mt-4">
      <p className="text-sm text-stone-600">
        {initial ? `Editing from published bundle v${initial.version}.` : "No bundle published yet — drafting the first one."}
      </p>
      <div className="mt-3 flex flex-wrap gap-2">
        {bundle.rules.map((r, i) => (
          <button
            key={i}
            onClick={() => setSelected(i)}
            className={`rounded-full px-3 py-1 text-sm ${
              i === selected ? "bg-stone-900 font-medium text-white" : "bg-white ring-1 ring-stone-300"
            }`}
          >
            {r.ruleId || `(rule ${i + 1})`}
          </button>
        ))}
        <button onClick={addRule} className="rounded-full px-3 py-1 text-sm underline">
          + Add rule
        </button>
      </div>

      {rule && (
        <div className="mt-4 grid gap-4 md:grid-cols-2">
          <div className="rounded-lg border border-stone-200 bg-white p-4">
            <h2 className="font-medium">Zone canvas (normalised 0–1)</h2>
            <svg
              viewBox="0 0 100 100"
              onClick={onCanvasClick}
              role="img"
              aria-label="Zone polygon editor. Click to add a point."
              className="mt-2 aspect-square w-full cursor-crosshair rounded border border-stone-300 bg-stone-50"
            >
              {[25, 50, 75].map((g) => (
                <g key={g} stroke="#d6d3d1" strokeWidth="0.3">
                  <line x1={g} y1="0" x2={g} y2="100" />
                  <line x1="0" y1={g} x2="100" y2={g} />
                </g>
              ))}
              {points.length > 0 && (
                <polygon
                  points={points.map(([x, y]) => `${x * 100},${y * 100}`).join(" ")}
                  fill="rgba(6,78,59,0.15)"
                  stroke="#065f46"
                  strokeWidth="0.8"
                />
              )}
              {points.map(([x, y], i) => (
                <circle
                  key={i}
                  cx={x * 100}
                  cy={y * 100}
                  r="2"
                  fill="#065f46"
                  onClick={(e) => {
                    e.stopPropagation();
                    updateRule({ polygon: points.filter((_, j) => j !== i) });
                  }}
                >
                  <title>{`Remove point ${i + 1}`}</title>
                </circle>
              ))}
            </svg>
            <p className="mt-1 text-xs text-stone-500">
              Click to add a point; click a point to remove it. Which tracked point counts as
              “inside” is defined by the detector contract, not by this drawing.
            </p>
          </div>

          <div className="rounded-lg border border-stone-200 bg-white p-4 text-sm">
            <h2 className="font-medium">Rule settings</h2>
            <div className="mt-2 space-y-2">
              <label className="block">
                Rule ID
                <input
                  value={rule.ruleId}
                  onChange={(e) => updateRule({ ruleId: e.target.value })}
                  className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                />
              </label>
              <div className="grid grid-cols-2 gap-2">
                <label className="block">
                  Type
                  <select
                    value={rule.type}
                    onChange={(e) => updateRule({ type: e.target.value })}
                    className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                  >
                    {TYPES.map((t) => (
                      <option key={t} value={t}>
                        {t}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="block">
                  Schedule
                  <select
                    value={rule.scheduleId}
                    onChange={(e) => updateRule({ scheduleId: e.target.value })}
                    className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                  >
                    {bundle.schedules.map((s) => (
                      <option key={s.scheduleId} value={s.scheduleId}>
                        {s.scheduleId}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
              <div className="grid grid-cols-3 gap-2">
                <label className="block">
                  Confidence
                  <input
                    type="number"
                    step="0.05"
                    min="0"
                    max="1"
                    value={rule.confidenceThreshold ?? 0.5}
                    onChange={(e) => updateRule({ confidenceThreshold: Number(e.target.value) })}
                    className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                  />
                </label>
                <label className="block">
                  Persistence
                  <input
                    type="number"
                    min="1"
                    value={rule.persistenceFrames ?? 3}
                    onChange={(e) => updateRule({ persistenceFrames: Number(e.target.value) })}
                    className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                  />
                </label>
                <label className="block">
                  Cooldown (s)
                  <input
                    type="number"
                    min="0"
                    value={rule.cooldownSeconds ?? 60}
                    onChange={(e) => updateRule({ cooldownSeconds: Number(e.target.value) })}
                    className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5"
                  />
                </label>
              </div>
              <label className="block">
                Polygon points, one “x, y” per line (accessible alternative to the canvas)
                <textarea
                  rows={4}
                  value={points.map(([x, y]) => `${x}, ${y}`).join("\n")}
                  onChange={(e) => {
                    const parsed = e.target.value
                      .split("\n")
                      .map((l) => l.split(",").map((v) => Number(v.trim())))
                      .filter((p) => p.length === 2 && p.every((v) => Number.isFinite(v)));
                    updateRule({ polygon: parsed as [number, number][] });
                  }}
                  className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5 font-mono text-xs"
                />
              </label>
              <p className="text-xs text-stone-500">
                A confidence score is a detector threshold, not a probability that an incident is
                dangerous.
              </p>
            </div>
          </div>
        </div>
      )}

      {error && (
        <p role="alert" className="mt-3 text-sm text-red-700">
          {error}
        </p>
      )}
      {ok && <p className="mt-3 text-sm text-emerald-800">{ok}</p>}
      <button
        onClick={publish}
        disabled={busy}
        className="mt-4 rounded-md bg-emerald-800 px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
      >
        {busy ? "Publishing…" : "Publish new bundle"}
      </button>
    </div>
  );
}
