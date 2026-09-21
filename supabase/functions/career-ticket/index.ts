import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "npm:@supabase/supabase-js@2";

const enc = new TextEncoder();

function json(status: number, value: unknown) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function b64url(bytes: Uint8Array) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/g, "");
}

async function signingKey() {
  const service = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!service) throw new Error("Supabase service role is unavailable");
  return await crypto.subtle.importKey(
    "raw",
    enc.encode("project-prime-career-ticket:v1:" + service),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") return json(405, { error: "method_not_allowed" });

  const auth = req.headers.get("authorization") ?? "";
  if (!auth.startsWith("Bearer ")) return json(401, { error: "authentication_required" });
  const jwt = auth.slice(7).trim();
  if (!jwt) return json(401, { error: "authentication_required" });

  let body: { client_id?: number };
  try {
    body = await req.json();
  } catch {
    return json(400, { error: "invalid_json" });
  }
  const clientId = body.client_id;
  if (!Number.isInteger(clientId) || clientId! <= 0 || clientId! > 0xffffffff) {
    return json(400, { error: "invalid_client_id" });
  }

  const url = Deno.env.get("SUPABASE_URL")!;
  const service = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
  const admin = createClient(url, service, {
    auth: { persistSession: false, autoRefreshToken: false },
  });
  const { data: userData, error: userError } = await admin.auth.getUser(jwt);
  if (userError || !userData.user) return json(401, { error: "invalid_session" });

  const now = Math.floor(Date.now() / 1000);
  const exp = now + 2 * 60 * 60;
  const payload = enc.encode(JSON.stringify({
    v: 1,
    sub: userData.user.id,
    cid: clientId,
    iat: now,
    exp,
  }));
  const payloadText = b64url(payload);
  const key = await signingKey();
  const sig = new Uint8Array(await crypto.subtle.sign(
    "HMAC", key, enc.encode(payloadText),
  ));

  return json(200, {
    ticket: `pp1.${payloadText}.${b64url(sig)}`,
    expires_at: new Date(exp * 1000).toISOString(),
  });
});
