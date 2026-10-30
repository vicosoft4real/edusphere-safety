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

export interface Camera {
  id: string;
  tenantId: string;
  campusId: string;
  deviceId: string | null;
  mode: string;
  host: string;
  port: number;
  path: string;
  lastTestOk: boolean | null;
  lastTestDetail: string | null;
  lastTestAt: string | null;
}

export interface RuleBundle {
  format: string;
  bundleVersion: number;
  modelVersion?: string;
  schedules: { scheduleId: string; ianaTimezone?: string }[];
  rules: {
    ruleId: string;
    type: string;
    version: number;
    confidenceThreshold?: number;
    persistenceFrames?: number;
    cooldownSeconds?: number;
    polygon?: [number, number][];
    scheduleId: string;
    [key: string]: unknown;
  }[];
}

export interface Policy {
  id: string;
  levels: { afterSeconds: number; recipients: string[] }[];
  recipients: string[];
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
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
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
  enrolCode: (campusId?: string) =>
    request<{ code: string; expiresInSeconds: number }>(`/v1/devices/enrol-codes`, {
      method: "POST",
      body: JSON.stringify({ campusId: campusId || undefined }),
    }),
  revoke: (id: string) =>
    request<{ id: string; revoked: boolean }>(`/v1/devices/${id}/revoke`, { method: "POST" }),
  cameras: () => request<{ items: Camera[] }>(`/v1/cameras`),
  createCamera: (body: {
    campusId: string;
    deviceId?: string;
    mode: string;
    host: string;
    port: number;
    path: string;
  }) => request<{ id: string }>(`/v1/cameras`, { method: "POST", body: JSON.stringify(body) }),
  updateCamera: (
    id: string,
    body: { campusId: string; deviceId?: string; mode: string; host: string; port: number; path: string }
  ) => request<{ id: string }>(`/v1/cameras/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteCamera: (id: string) => request<unknown>(`/v1/cameras/${id}`, { method: "DELETE" }),
  latestBundle: () => request<{ version: number; document: string }>(`/v1/config/bundles/latest`),
  publishBundle: (document: string) =>
    request<{ version: number }>(`/v1/config/bundles`, {
      method: "POST",
      body: JSON.stringify({ document }),
    }),
  policies: () => request<{ items: Policy[] }>(`/v1/config/policies`),
  setPolicy: (body: { id: string; levels: unknown; recipients: unknown }) =>
    request<unknown>(`/v1/config/policies`, { method: "POST", body: JSON.stringify(body) }),
};
