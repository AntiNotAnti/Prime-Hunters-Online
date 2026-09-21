-- Authoritative Hunter License career ingestion.
--
-- No gameplay client can write these tables. A provisioned dedicated server
-- posts an authoritative end-of-match report to the career-report Edge
-- Function. The Edge Function verifies both the server credential and each
-- participant's short-lived career ticket, then invokes this service-role-only
-- transaction.

create table if not exists public.project_prime_career_reporters (
    server_id uuid primary key default gen_random_uuid(),
    display_name text not null check (char_length(display_name) between 1 and 80),
    key_hash text not null unique check (key_hash ~ '^[0-9a-f]{64}$'),
    trust_class integer not null default 0 check (trust_class between 0 and 5),
    enabled boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

alter table public.project_prime_career_reporters enable row level security;
revoke all on public.project_prime_career_reporters from public, anon, authenticated;
grant all on public.project_prime_career_reporters to service_role;

CREATE OR REPLACE FUNCTION public.project_prime_rating_tier(p_points integer)
 RETURNS integer
 LANGUAGE sql
 IMMUTABLE STRICT
 SET search_path TO 'pg_catalog'
AS $function$
    select case
        when p_points >= 750 then 5
        when p_points >= 390 then 4
        when p_points >= 140 then 3
        when p_points >= 40 then 2
        else 1
    end;
$function$


CREATE OR REPLACE FUNCTION public.project_prime_rating_pair_delta(p_self_points integer, p_opponent_points integer, p_result integer)
 RETURNS integer
 LANGUAGE plpgsql
 IMMUTABLE STRICT
 SET search_path TO 'pg_catalog'
AS $function$
declare
    s integer;
    o integer;
    gain integer;
    loss integer;
begin
    if p_result = 0 then return 0; end if;
    s := case
        when p_self_points >= 750 then 5
        when p_self_points >= 390 then 4
        when p_self_points >= 140 then 3
        when p_self_points >= 40 then 2
        else 1
    end;
    o := case
        when p_opponent_points >= 750 then 5
        when p_opponent_points >= 390 then 4
        when p_opponent_points >= 140 then 3
        when p_opponent_points >= 40 then 2
        else 1
    end;

    gain := case s
        when 1 then (array[2,4,7,10,15])[o]
        when 2 then (array[1,3,6,10,15])[o]
        when 3 then (array[1,2,4,8,12])[o]
        when 4 then (array[1,1,4,5,8])[o]
        else (array[1,1,2,4,6])[o]
    end;
    loss := case s
        when 1 then (array[1,1,1,0,0])[o]
        when 2 then (array[2,3,1,0,0])[o]
        when 3 then (array[4,3,4,3,2])[o]
        when 4 then (array[8,8,6,5,4])[o]
        else (array[15,12,10,8,6])[o]
    end;
    return case when p_result > 0 then gain else -loss end;
end;
$function$


CREATE OR REPLACE FUNCTION public.ingest_project_prime_career_match(p_report jsonb, p_server_id uuid, p_trust_class integer, p_payload_hash text, p_original_report text)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'prime', 'pg_temp'
AS $function$
declare
    v_match_id uuid;
    v_server_incarnation uuid;
    v_started_at timestamptz;
    v_ended_at timestamptz;
    v_room_key text;
    v_mode integer;
    v_teams boolean;
    v_played_ticks bigint;
    v_end_reason text;
    v_rating_requested boolean;
    v_processing_order bigint;
    v_existing_server uuid;
    v_existing_hash text;
    v_career_eligible boolean;
    v_rating_eligible boolean := false;
    v_rating_reason integer := null;
    v_rating_status text := 'ineligible';
    v_started_count integer;
    v_missing_identity boolean;
    v_distinct_players integer;
    v_distinct_teams integer;
    v_player_count integer;
    v_p jsonb;
    v_o jsonb;
    v_player uuid;
    v_opponent uuid;
    v_scope integer;
    v_eligible boolean;
    v_won boolean;
    v_tied boolean;
    v_lost boolean;
    v_departed boolean;
    v_started boolean;
    v_outcome integer;
    v_kills bigint;
    v_deaths bigint;
    v_assists bigint;
    v_damage bigint;
    v_headshots bigint;
    v_octolith bigint;
    v_nodes bigint;
    v_prime_kills bigint;
    v_longest bigint;
    v_ticks bigint;
    v_hunter integer;
    v_single_hunter boolean;
    v_team integer;
    v_rank integer;
    v_team_rank integer;
    v_beam jsonb;
    v_beam_index integer;
    v_beam_kills bigint;
    v_points_before integer;
    v_points_after integer;
    v_tier_before integer;
    v_tier_after integer;
    v_opp_points integer;
    v_opp_tier integer;
    v_pair_result integer;
    v_pair_delta integer;
    v_raw_delta integer;
    v_normalized_delta integer;
    v_applied_delta integer;
    v_opponent_count integer;
begin
    if p_report is null or jsonb_typeof(p_report) <> 'object' then
        raise exception 'invalid report';
    end if;
    if p_server_id is null or p_trust_class not between 0 and 5 then
        raise exception 'invalid reporter';
    end if;
    if p_payload_hash !~ '^[0-9A-F]{64}$' then
        raise exception 'invalid payload hash';
    end if;
    if octet_length(convert_to(p_original_report, 'UTF8')) > 524288 then
        raise exception 'report too large';
    end if;
    if coalesce((p_report->>'version')::integer, 0) <> 1 then
        raise exception 'unsupported report version';
    end if;

    v_match_id := (p_report->>'match_id')::uuid;
    v_server_incarnation := (p_report->>'server_incarnation')::uuid;
    v_started_at := (p_report->>'started_at_utc')::timestamptz;
    v_ended_at := (p_report->>'ended_at_utc')::timestamptz;
    v_room_key := p_report->>'room_key';
    v_mode := (p_report->>'mode')::integer;
    v_teams := coalesce((p_report->>'teams')::boolean, false);
    v_played_ticks := coalesce((p_report->>'played_ticks')::bigint, 0);
    v_end_reason := coalesce(p_report->>'end_reason', '');
    v_rating_requested := coalesce((p_report->>'rating_eligible')::boolean, false);

    if v_match_id is null or v_match_id = '00000000-0000-0000-0000-000000000000'::uuid
       or v_server_incarnation is null
       or v_server_incarnation = '00000000-0000-0000-0000-000000000000'::uuid
       or v_room_key is null or char_length(v_room_key) not between 1 and 128
       or v_started_at is null or v_ended_at is null or v_ended_at < v_started_at
       or v_ended_at > now() + interval '5 minutes'
       or v_ended_at - v_started_at > interval '24 hours'
       or v_played_ticks < 0 or v_played_ticks > 5184000
       or v_mode < 0 or v_mode > 255
       or jsonb_typeof(p_report->'participants') <> 'array'
       or jsonb_array_length(p_report->'participants') not between 1 and 8 then
        raise exception 'invalid authoritative report facts';
    end if;

    select m."ServerId", m."PayloadHash"
      into v_existing_server, v_existing_hash
      from prime.accepted_matches m
     where m."MatchId" = v_match_id;
    if found then
        if v_existing_server = p_server_id and v_existing_hash = p_payload_hash then
            return jsonb_build_object('status', 'duplicate', 'match_id', v_match_id);
        end if;
        raise exception 'match id conflict';
    end if;

    v_started_count := (
        select count(*)
        from jsonb_array_elements(p_report->'participants') x
        where coalesce((x->>'started_match')::boolean, false)
    );
    v_missing_identity := exists (
        select 1
        from jsonb_array_elements(p_report->'participants') x
        where coalesce((x->>'started_match')::boolean, false)
          and nullif(x->>'player_id', '') is null
    );
    v_distinct_players := (
        select count(distinct (x->>'player_id')::uuid)
        from jsonb_array_elements(p_report->'participants') x
        where nullif(x->>'player_id', '') is not null
    );
    v_player_count := (
        select count(*)
        from jsonb_array_elements(p_report->'participants') x
        where nullif(x->>'player_id', '') is not null
    );
    if v_distinct_players <> v_player_count then
        raise exception 'duplicate registered player';
    end if;

    if exists (
        select 1
        from jsonb_array_elements(p_report->'participants') x
        where nullif(x->>'player_id', '') is not null
          and not exists (
              select 1 from prime.hunter_licenses l
              where l."PlayerId" = (x->>'player_id')::uuid
          )
    ) then
        raise exception 'unknown hunter license';
    end if;

    -- Canonical row locks: overlapping matches for the same licenses serialize
    -- before rating reads/modifies balances.
    perform 1
      from prime.hunter_licenses l
     where l."PlayerId" in (
        select distinct (x->>'player_id')::uuid
        from jsonb_array_elements(p_report->'participants') x
        where nullif(x->>'player_id', '') is not null
     )
     order by l."PlayerId"
     for update;

    v_career_eligible := p_trust_class <> 5
        and not v_missing_identity
        and v_end_reason = 'completed';

    if not v_rating_requested then
        v_rating_reason := 8; -- NonOfficialRules
    elsif p_trust_class = 0 then
        v_rating_reason := 3; -- CommunityServer
    elsif p_trust_class = 1 then
        v_rating_reason := 4; -- PrivateServer
    elsif p_trust_class = 4 then
        v_rating_reason := 5; -- TournamentRatingDisabled
    elsif p_trust_class = 5 then
        v_rating_reason := 6; -- PracticeServer
    elsif p_trust_class not in (2, 3) then
        v_rating_reason := 7; -- UnsupportedTrustClass
    elsif v_end_reason <> 'completed' then
        v_rating_reason := 9; -- IncompleteMatch
    elsif v_started_count not between 2 and 8 then
        v_rating_reason := 12; -- InvalidRoster
    elsif v_missing_identity then
        v_rating_reason := 11; -- GuestParticipant
    else
        if v_teams then
            v_distinct_teams := (
                select count(distinct (x->>'team')::integer)
                from jsonb_array_elements(p_report->'participants') x
                where coalesce((x->>'started_match')::boolean, false)
            );
            if v_distinct_teams < 2 then v_rating_reason := 16; end if; -- NoOpposingPair
        end if;
        if v_rating_reason is null then
            v_rating_eligible := true;
            v_rating_status := 'applied';
        end if;
    end if;

    insert into prime.accepted_matches (
        "MatchId","ServerId","ServerIncarnation","PayloadHash","OriginalReport",
        "AcceptedAt","EndedAt","RoomKey","Mode","TrustClass","CareerEligible",
        "RatingStatus","RatingPolicyVersion","RatingIneligibilityReason"
    ) values (
        v_match_id,p_server_id,v_server_incarnation,p_payload_hash,
        convert_to(p_original_report,'UTF8'),now(),v_ended_at,v_room_key,v_mode,
        p_trust_class,v_career_eligible,v_rating_status,1,
        case when v_rating_eligible then null else v_rating_reason end
    )
    returning "ProcessingOrder" into v_processing_order;

    -- Rating is the Project Prime PairwiseNormalizedV1 policy copied from the
    -- existing backend model. Custom/lobby matches remain career-only.
    if v_rating_eligible then
        for v_p in
            select value from jsonb_array_elements(p_report->'participants')
            where coalesce((value->>'started_match')::boolean, false)
            order by value->>'player_id'
        loop
            v_player := (v_p->>'player_id')::uuid;
            select l."RatingPoints" into v_points_before
              from prime.hunter_licenses l where l."PlayerId" = v_player;
            v_tier_before := public.project_prime_rating_tier(v_points_before);
            v_raw_delta := 0;
            v_opponent_count := 0;

            for v_o in
                select value from jsonb_array_elements(p_report->'participants')
                where coalesce((value->>'started_match')::boolean, false)
                  and (value->>'player_id')::uuid <> v_player
            loop
                if v_teams and (v_o->>'team')::integer = (v_p->>'team')::integer then
                    continue;
                end if;
                v_opponent := (v_o->>'player_id')::uuid;
                select l."RatingPoints" into v_opp_points
                  from prime.hunter_licenses l where l."PlayerId" = v_opponent;
                v_opp_tier := public.project_prime_rating_tier(v_opp_points);

                if coalesce((v_p->>'departed')::boolean, false)
                   and coalesce((v_o->>'departed')::boolean, false) then
                    v_pair_result := 0;
                elsif coalesce((v_p->>'departed')::boolean, false) then
                    v_pair_result := -1;
                elsif coalesce((v_o->>'departed')::boolean, false) then
                    v_pair_result := 1;
                elsif (case when v_teams then (v_p->>'team_standing')::integer
                            else (v_p->>'standing')::integer end)
                    < (case when v_teams then (v_o->>'team_standing')::integer
                            else (v_o->>'standing')::integer end) then
                    v_pair_result := 1;
                elsif (case when v_teams then (v_p->>'team_standing')::integer
                            else (v_p->>'standing')::integer end)
                    > (case when v_teams then (v_o->>'team_standing')::integer
                            else (v_o->>'standing')::integer end) then
                    v_pair_result := -1;
                else
                    v_pair_result := 0;
                end if;

                v_pair_delta := public.project_prime_rating_pair_delta(
                    v_points_before, v_opp_points, v_pair_result);
                v_raw_delta := v_raw_delta + v_pair_delta;
                v_opponent_count := v_opponent_count + 1;
            end loop;

            if v_opponent_count = 0 then
                raise exception 'rating roster has no opposing pair';
            end if;
            v_normalized_delta := v_raw_delta * least(3, v_opponent_count) / v_opponent_count;
            v_points_after := greatest(0, least(850, v_points_before + v_normalized_delta));
            v_applied_delta := v_points_after - v_points_before;
            v_tier_after := public.project_prime_rating_tier(v_points_after);

            insert into prime.rating_transactions (
                "MatchId","PlayerId","ProcessingOrder","PointsBefore","TierBefore",
                "OpponentCount","RawDelta","NormalizedDelta","AppliedDelta",
                "PointsAfter","TierAfter","PolicyVersion"
            ) values (
                v_match_id,v_player,v_processing_order,v_points_before,v_tier_before,
                v_opponent_count,v_raw_delta,v_normalized_delta,v_applied_delta,
                v_points_after,v_tier_after,1
            );

            for v_o in
                select value from jsonb_array_elements(p_report->'participants')
                where coalesce((value->>'started_match')::boolean, false)
                  and (value->>'player_id')::uuid <> v_player
            loop
                if v_teams and (v_o->>'team')::integer = (v_p->>'team')::integer then
                    continue;
                end if;
                v_opponent := (v_o->>'player_id')::uuid;
                select l."RatingPoints" into v_opp_points
                  from prime.hunter_licenses l where l."PlayerId" = v_opponent;
                v_opp_tier := public.project_prime_rating_tier(v_opp_points);

                if coalesce((v_p->>'departed')::boolean, false)
                   and coalesce((v_o->>'departed')::boolean, false) then
                    v_pair_result := 0;
                elsif coalesce((v_p->>'departed')::boolean, false) then
                    v_pair_result := -1;
                elsif coalesce((v_o->>'departed')::boolean, false) then
                    v_pair_result := 1;
                elsif (case when v_teams then (v_p->>'team_standing')::integer
                            else (v_p->>'standing')::integer end)
                    < (case when v_teams then (v_o->>'team_standing')::integer
                            else (v_o->>'standing')::integer end) then
                    v_pair_result := 1;
                elsif (case when v_teams then (v_p->>'team_standing')::integer
                            else (v_p->>'standing')::integer end)
                    > (case when v_teams then (v_o->>'team_standing')::integer
                            else (v_o->>'standing')::integer end) then
                    v_pair_result := -1;
                else
                    v_pair_result := 0;
                end if;

                v_pair_delta := public.project_prime_rating_pair_delta(
                    v_points_before, v_opp_points, v_pair_result);
                insert into prime.rating_pair_contributions (
                    "MatchId","PlayerId","OpponentPlayerId","OpponentPointsBefore",
                    "OpponentTierBefore","Result","Delta"
                ) values (
                    v_match_id,v_player,v_opponent,v_opp_points,v_opp_tier,
                    case v_pair_result when 1 then 0 when -1 then 1 else 2 end,
                    v_pair_delta
                );
            end loop;

        end loop;

        -- Freeze every participant's pre-match balance for all pairwise
        -- calculations, then apply all new balances together. Updating one
        -- license inside the loop would let the next participant rate against
        -- a balance from the match currently being processed.
        update prime.hunter_licenses l
           set "RatingPoints" = rt."PointsAfter"
          from prime.rating_transactions rt
         where rt."MatchId" = v_match_id
           and rt."PlayerId" = l."PlayerId";
    end if;

    for v_p in select value from jsonb_array_elements(p_report->'participants')
    loop
        if nullif(v_p->>'player_id','') is null then continue; end if;
        v_player := (v_p->>'player_id')::uuid;
        v_started := coalesce((v_p->>'started_match')::boolean, false);
        v_departed := coalesce((v_p->>'departed')::boolean, false);
        v_won := coalesce((v_p->>'won')::boolean, false);
        v_tied := coalesce((v_p->>'tied')::boolean, false);
        v_eligible := v_career_eligible and v_started;
        v_lost := v_eligible and not v_won and not v_tied;
        v_outcome := case
            when not v_eligible then 5
            when v_departed then 3
            when v_won then 0
            when v_tied then 2
            else 1
        end;

        v_ticks := greatest(0, coalesce((v_p->>'played_ticks')::bigint,0));
        v_kills := greatest(0, coalesce((v_p->'metrics'->>'kills')::bigint,0));
        v_deaths := greatest(0, coalesce((v_p->'metrics'->>'deaths')::bigint,0));
        v_assists := greatest(0, coalesce((v_p->'metrics'->>'assists')::bigint,0));
        v_damage := greatest(0, coalesce((v_p->'metrics'->>'damage')::bigint,0));
        v_headshots := greatest(0, coalesce((v_p->'metrics'->>'headshots')::bigint,0));
        v_octolith := greatest(0, coalesce((v_p->'metrics'->>'octolith_scores')::bigint,0));
        v_nodes := greatest(0, coalesce((v_p->'metrics'->>'nodes_captured')::bigint,0));
        v_prime_kills := greatest(0, coalesce((v_p->'metrics'->>'kills_as_prime')::bigint,0));
        v_longest := greatest(0, coalesce((v_p->'metrics'->>'longest_kill_streak')::bigint,0));
        v_hunter := greatest(0, least(6, coalesce((v_p->>'hunter')::integer,0)));
        v_single_hunter := coalesce((v_p->>'single_hunter')::boolean,true);

        if v_headshots > v_kills then raise exception 'headshots exceed kills'; end if;
        if v_ticks > v_played_ticks then raise exception 'participant ticks exceed match'; end if;

        insert into prime.career_participations (
            "MatchId","PlayerId","ProcessingOrder","Eligible","Won","Tied","Outcome",
            "PlayedTicks","Kills","Deaths","Assists","Damage"
        ) values (
            v_match_id,v_player,v_processing_order,v_eligible,v_won,v_tied,v_outcome,
            v_ticks,v_kills,v_deaths,v_assists,v_damage
        );

        if not v_eligible then continue; end if;

        for v_scope in
            select p_trust_class
            union all
            select -1 where p_trust_class in (2,3)
        loop
            -- Career, map and mode all receive the same authoritative whole-match
            -- counters; only their grouping key differs.
            insert into prime.career_aggregates (
                "PlayerId","TrustClass","Dimension","Key","Matches","Wins","Ties","Losses",
                "PlayedTicks","Kills","Deaths","Assists","Damage","OctolithScores",
                "NodesCaptured","KillsAsPrime","HeadshotKills","BipedKills","AltFormKills",
                "LongestKillStreak","CurrentWinStreak","LongestWinStreak","OutcomeSamples"
            ) values (
                v_player,v_scope,'career','all',1,
                case when v_won then 1 else 0 end,case when v_tied then 1 else 0 end,
                case when v_lost then 1 else 0 end,v_ticks,v_kills,v_deaths,v_assists,v_damage,
                v_octolith,v_nodes,v_prime_kills,v_headshots,null,null,v_longest,
                case when v_won then 1 else 0 end,case when v_won then 1 else 0 end,1
            )
            on conflict ("PlayerId","TrustClass","Dimension","Key") do update set
                "Matches"=prime.career_aggregates."Matches"+1,
                "Wins"=prime.career_aggregates."Wins"+case when v_won then 1 else 0 end,
                "Ties"=prime.career_aggregates."Ties"+case when v_tied then 1 else 0 end,
                "Losses"=prime.career_aggregates."Losses"+case when v_lost then 1 else 0 end,
                "PlayedTicks"=prime.career_aggregates."PlayedTicks"+v_ticks,
                "Kills"=prime.career_aggregates."Kills"+v_kills,
                "Deaths"=prime.career_aggregates."Deaths"+v_deaths,
                "Assists"=prime.career_aggregates."Assists"+v_assists,
                "Damage"=prime.career_aggregates."Damage"+v_damage,
                "OctolithScores"=prime.career_aggregates."OctolithScores"+v_octolith,
                "NodesCaptured"=prime.career_aggregates."NodesCaptured"+v_nodes,
                "KillsAsPrime"=prime.career_aggregates."KillsAsPrime"+v_prime_kills,
                "HeadshotKills"=prime.career_aggregates."HeadshotKills"+v_headshots,
                "BipedKills"=null,
                "AltFormKills"=null,
                "LongestKillStreak"=greatest(prime.career_aggregates."LongestKillStreak",v_longest),
                "CurrentWinStreak"=case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end,
                "LongestWinStreak"=greatest(prime.career_aggregates."LongestWinStreak",
                    case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end),
                "OutcomeSamples"=prime.career_aggregates."OutcomeSamples"+1;

            insert into prime.career_aggregates (
                "PlayerId","TrustClass","Dimension","Key","Matches","Wins","Ties","Losses",
                "PlayedTicks","Kills","Deaths","Assists","Damage","OctolithScores",
                "NodesCaptured","KillsAsPrime","HeadshotKills","BipedKills","AltFormKills",
                "LongestKillStreak","CurrentWinStreak","LongestWinStreak","OutcomeSamples"
            )
            select v_player,v_scope,d.dimension,d.key,1,
                case when v_won then 1 else 0 end,case when v_tied then 1 else 0 end,
                case when v_lost then 1 else 0 end,v_ticks,v_kills,v_deaths,v_assists,v_damage,
                v_octolith,v_nodes,v_prime_kills,v_headshots,null,null,v_longest,
                case when v_won then 1 else 0 end,case when v_won then 1 else 0 end,1
            from (values ('map',v_room_key),('mode',v_mode::text)) d(dimension,key)
            on conflict ("PlayerId","TrustClass","Dimension","Key") do update set
                "Matches"=prime.career_aggregates."Matches"+1,
                "Wins"=prime.career_aggregates."Wins"+case when v_won then 1 else 0 end,
                "Ties"=prime.career_aggregates."Ties"+case when v_tied then 1 else 0 end,
                "Losses"=prime.career_aggregates."Losses"+case when v_lost then 1 else 0 end,
                "PlayedTicks"=prime.career_aggregates."PlayedTicks"+v_ticks,
                "Kills"=prime.career_aggregates."Kills"+v_kills,
                "Deaths"=prime.career_aggregates."Deaths"+v_deaths,
                "Assists"=prime.career_aggregates."Assists"+v_assists,
                "Damage"=prime.career_aggregates."Damage"+v_damage,
                "OctolithScores"=prime.career_aggregates."OctolithScores"+v_octolith,
                "NodesCaptured"=prime.career_aggregates."NodesCaptured"+v_nodes,
                "KillsAsPrime"=prime.career_aggregates."KillsAsPrime"+v_prime_kills,
                "HeadshotKills"=prime.career_aggregates."HeadshotKills"+v_headshots,
                "BipedKills"=null,"AltFormKills"=null,
                "LongestKillStreak"=greatest(prime.career_aggregates."LongestKillStreak",v_longest),
                "CurrentWinStreak"=case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end,
                "LongestWinStreak"=greatest(prime.career_aggregates."LongestWinStreak",
                    case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end),
                "OutcomeSamples"=prime.career_aggregates."OutcomeSamples"+1;

            if v_single_hunter then
                insert into prime.career_aggregates (
                    "PlayerId","TrustClass","Dimension","Key","Matches","Wins","Ties","Losses",
                    "PlayedTicks","Kills","Deaths","Assists","Damage","OctolithScores",
                    "NodesCaptured","KillsAsPrime","HeadshotKills","BipedKills","AltFormKills",
                    "LongestKillStreak","CurrentWinStreak","LongestWinStreak","OutcomeSamples"
                ) values (
                    v_player,v_scope,'hunter',v_hunter::text,1,
                    case when v_won then 1 else 0 end,case when v_tied then 1 else 0 end,
                    case when v_lost then 1 else 0 end,v_ticks,v_kills,v_deaths,v_assists,v_damage,
                    v_octolith,v_nodes,v_prime_kills,v_headshots,null,null,v_longest,
                    case when v_won then 1 else 0 end,case when v_won then 1 else 0 end,1
                )
                on conflict ("PlayerId","TrustClass","Dimension","Key") do update set
                    "Matches"=prime.career_aggregates."Matches"+1,
                    "Wins"=prime.career_aggregates."Wins"+case when v_won then 1 else 0 end,
                    "Ties"=prime.career_aggregates."Ties"+case when v_tied then 1 else 0 end,
                    "Losses"=prime.career_aggregates."Losses"+case when v_lost then 1 else 0 end,
                    "PlayedTicks"=prime.career_aggregates."PlayedTicks"+v_ticks,
                    "Kills"=prime.career_aggregates."Kills"+v_kills,
                    "Deaths"=prime.career_aggregates."Deaths"+v_deaths,
                    "Assists"=prime.career_aggregates."Assists"+v_assists,
                    "Damage"=prime.career_aggregates."Damage"+v_damage,
                    "OctolithScores"=prime.career_aggregates."OctolithScores"+v_octolith,
                    "NodesCaptured"=prime.career_aggregates."NodesCaptured"+v_nodes,
                    "KillsAsPrime"=prime.career_aggregates."KillsAsPrime"+v_prime_kills,
                    "HeadshotKills"=prime.career_aggregates."HeadshotKills"+v_headshots,
                    "BipedKills"=null,"AltFormKills"=null,
                    "LongestKillStreak"=greatest(prime.career_aggregates."LongestKillStreak",v_longest),
                    "CurrentWinStreak"=case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end,
                    "LongestWinStreak"=greatest(prime.career_aggregates."LongestWinStreak",
                        case when v_won then prime.career_aggregates."CurrentWinStreak"+1 else 0 end),
                    "OutcomeSamples"=prime.career_aggregates."OutcomeSamples"+1;
            end if;

            v_beam := coalesce(v_p->'metrics'->'beam_kills','[]'::jsonb);
            if jsonb_typeof(v_beam) = 'array' then
                for v_beam_index in 0..least(8,jsonb_array_length(v_beam)-1)
                loop
                    v_beam_kills := greatest(0,coalesce((v_beam->>v_beam_index)::bigint,0));
                    if v_beam_kills > 0 then
                        insert into prime.career_aggregates (
                            "PlayerId","TrustClass","Dimension","Key","Matches","Wins","Ties","Losses",
                            "PlayedTicks","Kills","Deaths","Assists","Damage","OctolithScores",
                            "NodesCaptured","KillsAsPrime","HeadshotKills","BipedKills","AltFormKills",
                            "LongestKillStreak","CurrentWinStreak","LongestWinStreak","OutcomeSamples"
                        ) values (
                            v_player,v_scope,'weapon',v_beam_index::text,1,0,0,0,
                            0,v_beam_kills,0,0,0,0,0,0,0,null,null,0,0,0,0
                        )
                        on conflict ("PlayerId","TrustClass","Dimension","Key") do update set
                            "Matches"=prime.career_aggregates."Matches"+1,
                            "Kills"=prime.career_aggregates."Kills"+v_beam_kills;
                    end if;
                end loop;
            end if;
        end loop;
    end loop;

    return jsonb_build_object(
        'status','accepted',
        'match_id',v_match_id,
        'processing_order',v_processing_order,
        'career_eligible',v_career_eligible,
        'rating_status',v_rating_status,
        'rating_ineligibility_reason',case when v_rating_eligible then null else v_rating_reason end
    );
end;
$function$


revoke all on function public.project_prime_rating_tier(integer)
    from public, anon, authenticated;
revoke all on function public.project_prime_rating_pair_delta(integer,integer,integer)
    from public, anon, authenticated;
revoke all on function public.ingest_project_prime_career_match(jsonb,uuid,integer,text,text)
    from public, anon, authenticated;
grant execute on function public.ingest_project_prime_career_match(jsonb,uuid,integer,text,text)
    to service_role;
