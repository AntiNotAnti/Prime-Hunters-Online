-- Operational visibility for trusted career-reporting servers.
alter table public.project_prime_career_reporters
  add column if not exists last_seen_at timestamptz,
  add column if not exists last_report_at timestamptz,
  add column if not exists last_result text;

comment on column public.project_prime_career_reporters.last_seen_at is
  'Newest authenticated report request from this server key.';
comment on column public.project_prime_career_reporters.last_report_at is
  'Newest report successfully accepted or recognized as duplicate.';
comment on column public.project_prime_career_reporters.last_result is
  'Short operational result for the most recent authenticated report request.';
