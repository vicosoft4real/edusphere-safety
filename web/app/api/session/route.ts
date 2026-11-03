import { cookies } from "next/headers";
import { NextRequest, NextResponse } from "next/server";

const COOKIE = "safety_token";
const apiUrl = () => process.env.SAFETY_API_URL ?? "http://localhost:5000";

// POST /api/session { tenantId, userId } — local/dev sign-in via the Api's
// dev-token stub. When the stub is disabled (production OIDC), this returns
// 404 and the UI shows the SSO-required state instead of fabricating auth.
export async function POST(req: NextRequest) {
  const { tenantId, userId } = (await req.json().catch(() => ({}))) as {
    tenantId?: string;
    userId?: string;
  };
  if (!tenantId || !userId) {
    return NextResponse.json({ code: "auth.missing", message: "Tenant and user are required." }, { status: 400 });
  }
  const upstream = await fetch(`${apiUrl()}/v1/auth/dev-token`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ tenantId, userId }),
  });
  if (!upstream.ok) {
    return NextResponse.json(
      { code: "auth.sso_required", message: "Local sign-in is disabled. Use single sign-on." },
      { status: upstream.status }
    );
  }
  const body = (await upstream.json()) as { accessToken?: string };
  if (!body.accessToken) {
    return NextResponse.json({ code: "auth.failed", message: "Sign-in failed." }, { status: 500 });
  }
  const res = NextResponse.json({ ok: true });
  res.cookies.set(COOKIE, body.accessToken, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
  });
  return res;
}

export async function GET() {
  const token = (await cookies()).get(COOKIE)?.value;
  return NextResponse.json({ signedIn: Boolean(token) });
}

export async function DELETE() {
  const res = NextResponse.json({ ok: true });
  res.cookies.delete(COOKIE);
  return res;
}
