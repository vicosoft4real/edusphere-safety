export interface Incident {
  id: string;
  tenantId: string;
  campusId: string;
  eventType: string;
  cameraId: string;
  edgeEventId: string | null;
  state: "New" | "Acknowledged" | "Investigating" | "Resolved" | "Dismissed";
  version: number;
}

export interface DeviceItem {
  id: string;
  tenantId: string;
  campusId: string | null;
  revoked: boolean;
  softwareVersion: string;
  modelVersion: string;
  configVersion: number;
  lastHeartbeatAt: string | null;
  staleConfig: boolean;
}

export interface AccessLogItem {
  objectKey: string;
  actorId: string;
  accessedAt: string;
}

export interface ApiError {
  code: string;
  message: string;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`/api${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
  });
  if (res.status === 401) {
    window.location.href = "/login";
    throw new Error("Signed out");
  }
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as ApiError | null;
    throw new Error(body?.message ?? `Request failed (${res.status})`);
  }
  return (await res.json()) as T;
}

export const api = {
  incidents: (limit = 50, offset = 0) =>
    request<{ items: Incident[]; limit: number; offset: number }>(
      `/v1/incidents?limit=${limit}&offset=${offset}`
    ),
  incident: (id: string) => request<Incident>(`/v1/incidents/${id}`),
  transition: (id: string, next: Incident["state"], reason: string | undefined, version: number) =>
    fetch(`/api/v1/incidents/${id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json", "If-Match": `W/"${version}"` },
      body: JSON.stringify({ next, reason }),
    }).then(async (res) => {
      if (res.status === 401) {
        window.location.href = "/login";
        throw new Error("Signed out");
      }
      if (!res.ok) {
        const body = (await res.json().catch(() => null)) as ApiError | null;
        throw new Error(body?.message ?? `Request failed (${res.status})`);
      }
      return (await res.json()) as { id: string; version?: number };
    }),
  devices: () => request<{ items: DeviceItem[] }>(`/v1/devices`),
  accessLog: () => request<{ items: AccessLogItem[] }>(`/v1/evidence/access-log`),
};
