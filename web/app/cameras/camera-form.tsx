"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { api, type Camera } from "@/lib/api";

interface Fields {
  campusId: string;
  deviceId: string;
  mode: string;
  host: string;
  port: number;
  path: string;
}

const empty: Fields = { campusId: "", deviceId: "", mode: "manual", host: "", port: 554, path: "" };

export function CameraForm({ initial, editId }: { initial?: Camera; editId?: string }) {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const { register, handleSubmit, reset, formState } = useForm<Fields>({
    defaultValues: initial
      ? {
          campusId: initial.campusId,
          deviceId: initial.deviceId ?? "",
          mode: initial.mode,
          host: initial.host,
          port: initial.port,
          path: initial.path,
        }
      : empty,
  });

  async function onSubmit(v: Fields) {
    setError(null);
    const body = {
      campusId: v.campusId.trim(),
      deviceId: v.deviceId.trim() || undefined,
      mode: v.mode,
      host: v.host.trim(),
      port: Number(v.port),
      path: v.path,
    };
    try {
      if (editId) await api.updateCamera(editId, body);
      else await api.createCamera(body);
      reset(empty);
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Save failed.");
    }
  }

  return (
    <form
      onSubmit={handleSubmit(onSubmit)}
      className="mt-6 rounded-lg border border-stone-200 bg-white p-4"
    >
      <h2 className="font-medium">{editId ? `Edit camera ${editId}` : "Add a camera"}</h2>
      <div className="mt-3 grid grid-cols-2 gap-3 text-sm md:grid-cols-3">
        <label className="block">
          Campus
          <input {...register("campusId", { required: true })} className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5" />
        </label>
        <label className="block">
          Edge device (optional)
          <input {...register("deviceId")} placeholder="dev-1" className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5" />
        </label>
        <label className="block">
          Mode
          <select {...register("mode")} className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5">
            <option value="manual">manual RTSP</option>
            <option value="onvif">ONVIF</option>
          </select>
        </label>
        <label className="block">
          Host
          <input {...register("host", { required: true })} placeholder="192.168.1.20" className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5" />
        </label>
        <label className="block">
          Port
          <input {...register("port", { valueAsNumber: true })} type="number" className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5" />
        </label>
        <label className="block">
          Path
          <input {...register("path")} placeholder="/stream1" className="mt-1 w-full rounded-md border border-stone-300 px-2 py-1.5" />
        </label>
      </div>
      {formState.errors.campusId && <p className="mt-1 text-sm text-red-700">Campus is required.</p>}
      {formState.errors.host && <p className="mt-1 text-sm text-red-700">Host is required.</p>}
      {error && (
        <p role="alert" className="mt-2 text-sm text-red-700">
          {error}
        </p>
      )}
      <button
        type="submit"
        disabled={formState.isSubmitting}
        className="mt-3 rounded-md bg-stone-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
      >
        {formState.isSubmitting ? "Saving…" : editId ? "Save changes" : "Add camera"}
      </button>
    </form>
  );
}

export function CameraRowActions({ camera }: { camera: Camera }) {
  const router = useRouter();
  const [editing, setEditing] = useState(false);

  async function remove() {
    if (!window.confirm(`Delete camera ${camera.id}?`)) return;
    try {
      await api.deleteCamera(camera.id);
      router.refresh();
    } catch (e) {
      window.alert(e instanceof Error ? e.message : "Delete failed.");
    }
  }

  return (
    <>
      <span className="flex gap-2">
        <button onClick={() => setEditing((v) => !v)} className="underline">
          {editing ? "Close" : "Edit"}
        </button>
        <button onClick={remove} className="text-red-700 underline">
          Delete
        </button>
      </span>
      {editing && (
        <div className="mt-2">
          <CameraForm initial={camera} editId={camera.id} />
        </div>
      )}
    </>
  );
}
