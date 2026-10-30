import { cookies } from "next/headers";

const base = () => process.env.SAFETY_API_URL ?? "http://localhost:5000";

export async function safetyFetch<T>(path: string): Promise<{ status: number; data: T | null }> {
  const token = (await cookies()).get("safety_token")?.value;
  if (!token) return { status: 401, data: null };
  const res = await fetch(`${base()}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  });
  if (!res.ok) return { status: res.status, data: null };
  return { status: res.status, data: (await res.json()) as T };
}

export async function signedIn(): Promise<boolean> {
  return Boolean((await cookies()).get("safety_token")?.value);
}
