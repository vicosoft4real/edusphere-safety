"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

const schema = z.object({
  tenantId: z.string().min(1, "Tenant is required"),
  userId: z.string().min(1, "User is required"),
});

type Form = z.infer<typeof schema>;

export default function LoginPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const { register, handleSubmit, formState } = useForm<Form>();

  async function onSubmit(values: Form) {
    setError(null);
    const parsed = schema.safeParse(values);
    if (!parsed.success) {
      setError(parsed.error.issues[0]?.message ?? "Check the form.");
      return;
    }
    const res = await fetch("/api/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(parsed.data),
    });
    if (!res.ok) {
      const body = (await res.json().catch(() => null)) as { message?: string } | null;
      setError(body?.message ?? "Sign-in failed.");
      return;
    }
    router.push("/");
  }

  return (
    <main className="mx-auto mt-24 w-full max-w-sm px-4">
      <h1 className="text-2xl font-semibold">EduSphere Safety</h1>
      <p className="mt-1 text-sm text-stone-600">Sign in to review incidents for your school.</p>
      <form onSubmit={handleSubmit(onSubmit)} className="mt-6 space-y-4">
        <div>
          <label htmlFor="tenantId" className="block text-sm font-medium">
            School tenant ID
          </label>
          <input
            id="tenantId"
            {...register("tenantId")}
            autoComplete="organization"
            className="mt-1 w-full rounded-md border border-stone-300 px-3 py-2"
          />
        </div>
        <div>
          <label htmlFor="userId" className="block text-sm font-medium">
            Staff user ID
          </label>
          <input
            id="userId"
            {...register("userId")}
            autoComplete="username"
            className="mt-1 w-full rounded-md border border-stone-300 px-3 py-2"
          />
        </div>
        {formState.errors.tenantId && <p className="text-sm text-red-700">{formState.errors.tenantId.message}</p>}
        {formState.errors.userId && <p className="text-sm text-red-700">{formState.errors.userId.message}</p>}
        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}
        <button
          type="submit"
          disabled={formState.isSubmitting}
          className="w-full rounded-md bg-emerald-800 px-3 py-2 font-medium text-white disabled:opacity-50"
        >
          {formState.isSubmitting ? "Signing in…" : "Sign in"}
        </button>
      </form>
      <p className="mt-4 text-xs text-stone-500">
        Local sign-in only. Production uses single sign-on; the sign-in token is kept in a
        server-side session cookie, never in browser storage.
      </p>
    </main>
  );
}
