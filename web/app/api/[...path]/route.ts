import { cookies } from "next/headers";
import { NextRequest, NextResponse } from "next/server";

const COOKIE_NAME = "safety_token";

const apiUrl = () => process.env.SAFETY_API_URL ?? "http://localhost:5000";

async function forward(req: NextRequest, path: string[]) {
  const token = (await cookies()).get(COOKIE_NAME)?.value;
  if (!token) {
    return NextResponse.json({ code: "auth.required", message: "Sign in first." }, { status: 401 });
  }
  const target = `${apiUrl()}/v1/${path.join("/")}${req.nextUrl.search}`;
  const headers = new Headers({ Authorization: `Bearer ${token}` });
  const ifMatch = req.headers.get("if-match");
  if (ifMatch) headers.set("If-Match", ifMatch);
  const hasBody = req.method !== "GET" && req.method !== "HEAD";
  if (hasBody) headers.set("Content-Type", req.headers.get("content-type") ?? "application/json");
  const upstream = await fetch(target, {
    method: req.method,
    headers,
    body: hasBody ? await req.text() : undefined,
  });
  // Preserve mediated-download redirects, rewritten to stay same-origin so
  // the browser never talks to the Api directly.
  if (upstream.status === 302) {
    const location = upstream.headers.get("location") ?? "/";
    const sameOrigin = location.startsWith("/api/")
      ? location
      : location.startsWith("/")
        ? `/api${location}`
        : location;
    return NextResponse.redirect(new URL(sameOrigin, req.url), 302);
  }
  const text = await upstream.text();
  return new NextResponse(text, {
    status: upstream.status,
    headers: { "Content-Type": upstream.headers.get("content-type") ?? "application/json" },
  });
}

export async function GET(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }) {
  return forward(req, (await ctx.params).path);
}
export async function POST(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }) {
  return forward(req, (await ctx.params).path);
}
export async function PATCH(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }) {
  return forward(req, (await ctx.params).path);
}
