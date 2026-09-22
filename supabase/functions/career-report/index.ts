import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import postgres from "npm:postgres@3.4.7";

const enc = new TextEncoder();
const dec = new TextDecoder();
const MAX_BYTES = 512 * 1024;
const sql = postgres(Deno.env.get("SUPABASE_DB_URL")!, {
  prepare: false,
  max: 1,
  idle_timeout: 20,
});

function json(status: number, value: unknown) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function fromB64url(value: string) {
  const padded = value.replaceAll("-", "+").replaceAll("_", "/")
    + "=".repeat((4 - value.length % 4) % 4);
  const binary = atob(padded);
  const result = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) result[i] = binary.charCodeAt(i);
  return result;
}

function hex(bytes: Uint8Array) {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function sha256(text: string) {
  return hex(new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(text))));
}

async function ticketKey() {
  const service = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!service) throw new Error("Supabase service role is unavailable");
  return await crypto.subtle.importKey(
    "raw",
    enc.encode("project-prime-career-ticket:v1:" + service),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["verify"],
  );
}

type Ticket = { v: number; sub: string; cid: number; iat: number; exp: number };

async function verifyTicket(token: unknown, clientId: number, matchEpoch: number): Promise<string | null> {
  if (typeof token !== "string" || token.length > 768) return null;
  const parts = token.split(".");
  if (parts.length !== 3 || parts[0] !== "pp1") return null;
  try {
    const payloadBytes = fromB64url(parts[1]);
    const signature = fromB64url(parts[2]);
    const key = await ticketKey();
    const ok = await crypto.subtle.verify("HMAC", key, signature, enc.encode(parts[1]));
    if (!ok) return null;
    const payload = JSON.parse(dec.decode(payloadBytes)) as Ticket;
    if (payload.v !== 1 || payload.cid !== clientId
      || !Number.isInteger(payload.iat) || !Number.isInteger(payload.exp)
      // Outbox delivery may happen well after a network outage. The ticket
      // must have covered the authoritative match end, not the later retry.
      || payload.exp < matchEpoch - 300 || payload.iat > matchEpoch + 300
      || payload.exp - payload.iat > 2 * 60 * 60
      || typeof payload.sub !== "string"
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(payload.sub)) {
      return null;
    }
    return payload.sub;
  } catch {
    return null;
  }
}

function integer(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER) {
  return Number.isInteger(value) && (value as number) >= min && (value as number) <= max
    ? value as number : null;
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST" && req.method !== "GET")
    return json(405, { error: "method_not_allowed" });

  const authorization = req.headers.get("authorization") ?? "";
  if (!authorization.startsWith("Bearer ")) return json(401, { error: "server_auth_required" });
  const serverKey = authorization.slice(7).trim();
  if (serverKey.length < 32 || serverKey.length > 256) return json(401, { error: "invalid_server_key" });

  if (req.method === "GET") {
    const serverHash = await sha256(serverKey);
    try {
      const rows = await sql`
        select server_id, display_name, trust_class, enabled
        from public.project_prime_career_reporters
        where key_hash = ${serverHash}
        limit 1
      `;
      const reporter = rows[0] as any;
      if (!reporter?.enabled) return json(401, { error: "unknown_server" });
      await sql`
        update public.project_prime_career_reporters
           set last_seen_at = now(), last_result = 'probe',
               updated_at = updated_at
         where server_id = ${reporter.server_id}::uuid
      `;
      return json(200, {
        ok: true,
        server_id: reporter.server_id,
        display_name: reporter.display_name,
        trust_class: reporter.trust_class,
      });
    } catch (error) {
      console.error("career reporter probe failed", error);
      return json(500, { error: "reporter_probe_failed" });
    }
  }

  const raw = await req.text();
  if (enc.encode(raw).length > MAX_BYTES) return json(413, { error: "report_too_large" });

  let incoming: any;
  try {
    incoming = JSON.parse(raw);
  } catch {
    return json(400, { error: "invalid_json" });
  }

  const serverHash = await sha256(serverKey);
  let reporter: { server_id: string; trust_class: number; enabled: boolean } | undefined;
  try {
    const rows = await sql`
      select server_id, trust_class, enabled
      from public.project_prime_career_reporters
      where key_hash = ${serverHash}
      limit 1
    `;
    reporter = rows[0] as typeof reporter;
  } catch (error) {
    console.error("career reporter lookup failed", error);
    return json(500, { error: "reporter_lookup_failed" });
  }
  if (!reporter?.enabled) return json(401, { error: "unknown_server" });

  const mark = async (result: string, accepted = false) => {
    try {
      if (accepted) {
        await sql`
          update public.project_prime_career_reporters
             set last_seen_at = now(), last_report_at = now(), last_result = ${result},
                 updated_at = updated_at
           where server_id = ${reporter!.server_id}::uuid
        `;
      } else {
        await sql`
          update public.project_prime_career_reporters
             set last_seen_at = now(), last_result = ${result},
                 updated_at = updated_at
           where server_id = ${reporter!.server_id}::uuid
        `;
      }
    } catch (error) {
      console.error("career reporter status update failed", error);
    }
  };
  await mark("received");

  if (incoming?.version !== 1 || typeof incoming.match_id !== "string"
    || typeof incoming.server_incarnation !== "string"
    || typeof incoming.room_key !== "string"
    || !Array.isArray(incoming.participants)
    || incoming.participants.length < 1 || incoming.participants.length > 8) {
    return json(400, { error: "invalid_report" });
  }

  const matchEpoch = Math.floor(Date.parse(incoming.ended_at_utc) / 1000);
  if (!Number.isFinite(matchEpoch) || matchEpoch > Math.floor(Date.now() / 1000) + 300) {
    return json(400, { error: "invalid_match_time" });
  }

  const normalizedParticipants: any[] = [];
  const players = new Set<string>();
  const duplicatePlayers = new Set<string>();
  let missingStartedIdentity = false;

  for (const p of incoming.participants) {
    const clientId = integer(p?.client_id, 1, 0xffffffff);
    const hunter = integer(p?.hunter, 0, 6);
    const team = integer(p?.team, 0, 7);
    const standing = integer(p?.standing, 0, 7);
    const teamStanding = integer(p?.team_standing, 0, 7);
    const ticks = integer(p?.played_ticks, 0, 5184000);
    const metrics = p?.metrics;
    if (clientId == null || hunter == null || team == null || standing == null
      || teamStanding == null || ticks == null || !metrics
      || typeof p.display_name !== "string" || p.display_name.length < 1 || p.display_name.length > 64
      || !Array.isArray(metrics.beam_kills) || metrics.beam_kills.length !== 9) {
      return json(400, { error: "invalid_participant" });
    }

    const numeric = [
      metrics.kills, metrics.deaths, metrics.assists, metrics.damage,
      metrics.headshots, metrics.octolith_scores, metrics.nodes_captured,
      metrics.kills_as_prime, metrics.longest_kill_streak,
      ...metrics.beam_kills,
    ];
    if (numeric.some((x: unknown) => integer(x, 0, 1_000_000) == null)
      || metrics.headshots > metrics.kills) {
      return json(400, { error: "invalid_metrics" });
    }

    const playerId = await verifyTicket(p.career_ticket, clientId, matchEpoch);
    if (p.started_match && !playerId) missingStartedIdentity = true;
    if (playerId) {
      if (players.has(playerId)) duplicatePlayers.add(playerId);
      players.add(playerId);
    }

    normalizedParticipants.push({
      participant_id: p.participant_id,
      client_id: clientId,
      player_id: playerId,
      display_name: p.display_name,
      hunter,
      single_hunter: p.single_hunter !== false,
      team,
      started_match: p.started_match === true,
      departed: p.departed === true,
      played_ticks: ticks,
      standing,
      team_standing: teamStanding,
      won: p.won === true,
      tied: p.tied === true,
      metrics: {
        kills: metrics.kills,
        deaths: metrics.deaths,
        assists: metrics.assists,
        damage: metrics.damage,
        headshots: metrics.headshots,
        octolith_scores: metrics.octolith_scores,
        nodes_captured: metrics.nodes_captured,
        kills_as_prime: metrics.kills_as_prime,
        longest_kill_streak: metrics.longest_kill_streak,
        beam_kills: metrics.beam_kills,
      },
    });
  }

  // Two simultaneous participants claiming one Hunter License are never
  // allowed to create two durable rows for that account. Treat the whole
  // match as Practice rather than rejecting the server's immutable report;
  // this also handles a process restart/rejoin that briefly overlaps the old
  // connection without turning an operational race into a lost report.
  if (duplicatePlayers.size > 0) {
    for (const p of normalizedParticipants) {
      if (p.player_id && duplicatePlayers.has(p.player_id)) {
        if (p.started_match) missingStartedIdentity = true;
        p.player_id = null;
      }
    }
  }

  const effectiveTrust = missingStartedIdentity ? 5 : reporter.trust_class;
  const normalized = {
    version: 1,
    match_id: incoming.match_id,
    wire_match_id: incoming.wire_match_id,
    server_incarnation: incoming.server_incarnation,
    build_version: incoming.build_version,
    protocol_version: incoming.protocol_version,
    started_at_utc: incoming.started_at_utc,
    ended_at_utc: incoming.ended_at_utc,
    played_ticks: incoming.played_ticks,
    end_reason: incoming.end_reason,
    room_key: incoming.room_key,
    mode: incoming.mode,
    teams: incoming.teams === true,
    team_count: incoming.team_count,
    rating_eligible: incoming.rating_eligible === true && !missingStartedIdentity,
    participants: normalizedParticipants,
  };
  const normalizedText = JSON.stringify(normalized);
  const payloadHash = (await sha256(normalizedText)).toUpperCase();

  try {
    const rows = await sql`
      select public.ingest_project_prime_career_match(
        ${sql.json(normalized)}::jsonb,
        ${reporter.server_id}::uuid,
        ${effectiveTrust}::integer,
        ${payloadHash}::text,
        ${normalizedText}::text
      ) as value
    `;
    const data = rows[0]?.value as any;
    const status = data?.status === "duplicate" ? "duplicate" : "accepted";
    await mark(status, true);
    return json(status === "duplicate" ? 200 : 201, data);
  } catch (error) {
    const message = String((error as any)?.message ?? error ?? "ingestion_failed");
    console.error("career ingestion failed", error);
    await mark(("error: " + message).slice(0, 240));
    if (message.toLowerCase().includes("match id conflict")) {
      return json(409, { error: "match_id_conflict" });
    }
    if (message.toLowerCase().includes("invalid")
      || message.toLowerCase().includes("unknown hunter")
      || message.toLowerCase().includes("duplicate")) {
      return json(400, { error: message });
    }
    return json(500, { error: "ingestion_failed" });
  }
});
