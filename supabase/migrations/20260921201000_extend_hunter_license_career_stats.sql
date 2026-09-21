-- Expose the authoritative whole-career aggregate on Hunter License.
-- Existing pre-projection participations remain a zero-safe fallback.

CREATE OR REPLACE FUNCTION public.project_prime_hunter_license(p_display_name text DEFAULT 'Hunter'::text, p_favorite_hunter integer DEFAULT 0)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'prime', 'pg_temp'
AS $function$
declare
    v_user uuid := auth.uid();
    v_name text;
    v_hunter smallint;
    v_username text;
    v_profile jsonb;
    v_stats jsonb;
    v_matches jsonb;
    v_cosmetics jsonb;
    v_tier integer;
begin
    if v_user is null then
        raise exception 'Authentication required';
    end if;

    v_name := left(coalesce(nullif(btrim(p_display_name), ''), 'Hunter'), 24);
    v_hunter := greatest(0, least(coalesce(p_favorite_hunter, 0), 6))::smallint;
    v_username := 'supa_' || replace(v_user::text, '-', '');

    insert into prime.players (
        "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
        "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
        "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled",
        "LockoutEnd", "LockoutEnabled", "AccessFailedCount"
    )
    values (
        v_user, v_username, upper(v_username), null, null,
        false, null, null, null, null, false, false, null, false, 0
    )
    on conflict ("Id") do nothing;

    insert into prime.player_profiles ("PlayerId", "DisplayName", "FavoriteHunter")
    values (v_user, v_name, v_hunter)
    on conflict ("PlayerId") do nothing;

    insert into prime.hunter_licenses ("PlayerId", "CreatedAt", "RatingPoints")
    values (v_user, now(), 0)
    on conflict ("PlayerId") do nothing;

    select rt."TierAfter"
      into v_tier
      from prime.rating_transactions rt
     where rt."PlayerId" = v_user
     order by rt."ProcessingOrder" desc
     limit 1;

    select jsonb_build_object(
        'player_id', p."PlayerId",
        'display_name', p."DisplayName",
        'favorite_hunter', p."FavoriteHunter",
        'created_at', l."CreatedAt",
        'rating_points', l."RatingPoints",
        'rating_tier', v_tier
    )
      into v_profile
      from prime.player_profiles p
      join prime.hunter_licenses l on l."PlayerId" = p."PlayerId"
     where p."PlayerId" = v_user;

    select jsonb_build_object(
        'games_played', a."Matches",
        'wins', a."Wins",
        'ties', a."Ties",
        'losses', a."Losses",
        'kills', a."Kills",
        'deaths', a."Deaths",
        'assists', a."Assists",
        'damage', a."Damage",
        'played_ticks', a."PlayedTicks",
        'headshots', a."HeadshotKills",
        'longest_kill_streak', a."LongestKillStreak",
        'current_win_streak', a."CurrentWinStreak",
        'longest_win_streak', a."LongestWinStreak",
        'octolith_scores', a."OctolithScores",
        'nodes_captured', a."NodesCaptured",
        'kills_as_prime', a."KillsAsPrime"
    )
      into v_stats
      from prime.career_aggregates a
     where a."PlayerId" = v_user
       and a."TrustClass" = -1
       and a."Dimension" = 'career'
       and a."Key" = 'all';

    -- A profile can predate the authoritative reporter migration. Keep the
    -- old participation sum as a fallback instead of making that player look
    -- empty until their first new accepted match.
    if v_stats is null then
        select jsonb_build_object(
            'games_played', count(*) filter (where c."Eligible"),
            'wins', count(*) filter (where c."Eligible" and c."Won"),
            'ties', count(*) filter (where c."Eligible" and c."Tied"),
            'losses', count(*) filter (where c."Eligible" and not c."Won" and not c."Tied"),
            'kills', coalesce(sum(c."Kills") filter (where c."Eligible"), 0),
            'deaths', coalesce(sum(c."Deaths") filter (where c."Eligible"), 0),
            'assists', coalesce(sum(c."Assists") filter (where c."Eligible"), 0),
            'damage', coalesce(sum(c."Damage") filter (where c."Eligible"), 0),
            'played_ticks', coalesce(sum(c."PlayedTicks") filter (where c."Eligible"), 0),
            'headshots', 0,
            'longest_kill_streak', 0,
            'current_win_streak', 0,
            'longest_win_streak', 0,
            'octolith_scores', 0,
            'nodes_captured', 0,
            'kills_as_prime', 0
        )
          into v_stats
          from prime.career_participations c
         where c."PlayerId" = v_user;
    end if;

    select coalesce(jsonb_agg(to_jsonb(m) order by m.played_at desc), '[]'::jsonb)
      into v_matches
      from (
        select
            a."MatchId" as match_id,
            a."EndedAt" as played_at,
            a."RoomKey" as room_key,
            a."Mode" as mode,
            a."TrustClass" as trust_class,
            a."CareerEligible" as career_eligible,
            a."RatingStatus" as rating_status,
            c."Eligible" as eligible,
            c."Won" as won,
            c."Tied" as tied,
            c."Outcome" as outcome,
            c."PlayedTicks" as played_ticks,
            c."Kills" as kills,
            c."Deaths" as deaths,
            c."Assists" as assists,
            c."Damage" as damage
        from prime.career_participations c
        join prime.accepted_matches a on a."MatchId" = c."MatchId"
        where c."PlayerId" = v_user
        order by a."EndedAt" desc
        limit 25
      ) m;

    select coalesce(jsonb_agg(jsonb_build_object(
        'hunter', c.hunter,
        'skin_key', c.skin_key,
        'armor_effect_key', c.armor_effect_key,
        'death_effect_key', c.death_effect_key,
        'updated_at', c.updated_at
    ) order by c.hunter), '[]'::jsonb)
      into v_cosmetics
      from prime.player_cosmetic_loadouts c
     where c.player_id = v_user;

    return jsonb_build_object(
        'profile', coalesce(v_profile, '{}'::jsonb),
        'stats', coalesce(v_stats, '{}'::jsonb),
        'matches', coalesce(v_matches, '[]'::jsonb),
        'cosmetics', coalesce(v_cosmetics, '[]'::jsonb)
    );
end;
$function$


revoke all on function public.project_prime_hunter_license(text,integer)
    from public, anon;
grant execute on function public.project_prime_hunter_license(text,integer)
    to authenticated;
