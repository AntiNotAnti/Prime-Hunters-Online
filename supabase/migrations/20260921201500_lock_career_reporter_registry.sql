-- The career reporter registry is service-role-only.
-- Grants are already revoked from client roles; this explicit restrictive
-- policy documents the boundary and keeps RLS tooling from treating the
-- intentional no-client-access table as an accidental omission.

drop policy if exists project_prime_career_reporters_no_client_access
on public.project_prime_career_reporters;

create policy project_prime_career_reporters_no_client_access
on public.project_prime_career_reporters
as restrictive
for all
to anon, authenticated
using (false)
with check (false);
