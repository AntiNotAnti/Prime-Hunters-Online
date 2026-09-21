# Build & Deploy — workflow

Build and release workflow summary.

Release versions use canonical `vMAJOR.MINOR.PATCH` tags with each component
between 0 and 999, and a nonzero Android versionCode. Validation happens before
a bump creates its tag. Oversized or zero-padded numbers are refused before
Bash arithmetic. Local desktop and Android builds have an explicit `local`
informational stamp; release builds override it, so `v1.0.0` is also a usable
release. Prerelease suffixes remain outside the automatic update channel.

Offline regressions: `tools/check-release-version.ps1` runs the real workflow
tag step with a mocked `gh`, and `dotnet run --project tools/updatecheck` checks
the updater against synthetic metadata. Run the latter again with
`-p:InformationalVersion=1.0.0 -- 1.0.0` to check a stamped assembly.

Workflows

| Workflow | When | What |
|---|---|---|
| `.github/workflows/build.yml` | every push and PR | publishes Windows/Linux targets on Ubuntu and signed macOS application archives on matching ARM64/Intel Mac runners, plus a Windows-runner job that builds and starts the Windows dedicated server |
| `.github/workflows/release.yml` | a `v*` tag, or by hand -- naming a tag or picking a bump that creates one | resolves one tag, builds desktop/Android and macOS packages in parallel, then publishes a GitHub Release directly in this public repository. Tag pushes publish; a manual run may set `publish=false` to leave a draft |

Tagging

Two ways in, both landing on the same `resolve the tag` step, which runs
before anything is checked out (`gh api` needs no local repo, and this is
where a typo becomes one clear line instead of `actions/checkout` retrying a
fetch for a ref that never existed).

**Build a tag that exists.** Push it, or type it into the dispatch form:

```
git tag v0.2.0 && git push origin v0.2.0
```

A bare number typed into the form is `v`-prefixed for you; a tag that was
never pushed fails with the `git tag ... && git push ...` line spelled out.

**Bump.** Leave the tag box empty and pick `patch`/`minor`/`major`. The step
lists `refs/tags`, keeps only `vX.Y.Z`, sorts with `sort -V` (the API returns
ref order, where `v0.10.0` sorts *before* `v0.9.0` -- this is why the sort is
not `tail -1` on the raw list), increments, and creates the ref on
`github.sha`, which is the tip of whichever branch the dropdown picked. With
no release tag at all it starts at `v0.1.0` rather than bumping an imagined
`v0.0.0` to `v0.0.1`. Both boxes empty is the one combination that errors.

Three things worth not rediscovering:

- **The bump lives inside `release.yml` on purpose.** A tag pushed by a
  separate workflow using the default `GITHUB_TOKEN` does not trigger another
  workflow -- GitHub's anti-recursion rule -- so a `tag.yml` would need a PAT
  or a GitHub App just to make the release run. One workflow that mints its
  own tag needs no second credential.
- **`INPUT_TAG` and `PUSHED_TAG` are separate variables** for the same reason
  the old `github.event.inputs.tag || github.ref_name` was an
  expressions-engine `||` and not a bash `${GITHUB_REF_NAME:-...}` fallback.
  `GITHUB_REF_NAME` is always set, and for a dispatch run it is whichever
  branch the dropdown defaulted to (almost always `master`). Hit once already:
  a run dispatched with nothing typed resolved to `vmaster`. A single
  `||`-chained variable breaks again the moment the tag box is allowed to be
  empty, which is exactly what the bump does -- hence
  `PUSHED_TAG: ${{ github.event_name == 'push' && github.ref_name || '' }}`,
  empty for every event that is not a tag push.
- **Nothing auto-tags on a push to main**, deliberately: every push would be a
  release. Nothing derives a version from commit messages either. A tag push
  publishes after validation; a manually dispatched run may explicitly leave
  the release as a draft.

Release notes

A standing block (beta, bring your own cartridge dump, which package is which,
a link to the README) with GitHub's own changelog appended under a `---`:
`gh api -X POST .../releases/generate-notes -f tag_name=$TAG --jq .body`,
which picks the previous tag itself. Appended rather than substituted, and
never fatal -- a rate limit or a tag with no predecessor leaves the standing
block and a `::warning::` in the log. Before this the notes were the same
words on every release and said nothing about the build being downloaded.

Rerunning the same tag updates the existing release instead of failing: the
create-vs-upload arms are chosen by `gh release view`, so notes are regenerated
and assets are `--clobber`ed. A manual run follows its `publish` input; tag
pushes publish.

Two Windows executables, one PE header field

`ProjectPrime.exe` is `WinExe` (no console; double-clicking opens the
launcher). That same property makes it useless as a server: cmd/PowerShell do
not wait for it and its exit code never reaches `%ERRORLEVEL%`.
`dotnet publish -r win-x64 -p:MphReadServer=true` publishes the same sources
without the launcher and with a console header, as `ProjectPrimeServer.exe`.
`tools/check-subsystem.sh gui|console <exe>` asserts each one in both
workflows, since it comes out of a csproj condition nothing else would notice
changing.

`-p:MphReadServer=true` is published three times: `win-x64-server`,
`linux-x64-server` and `linux-arm64` (the Pi is server-only; plain x64 server
covers a VPS/spare desktop). Only the Windows server package is renamed --
Linux keeps the plain `ProjectPrime` name, and the Pi's own
`deploy-server.sh` migrates a systemd unit still pointing at the old name,
`MphRead`.

Both x64 Linux builds (game's server capability and the standalone server
package) run the dedicated-server startup contract on the Ubuntu release
runner. CI has no operator-supplied game files, so the expected gameplay-server
result is an actionable refusal and exit 1, not a live match. The Windows
server gets the same contract on a Windows runner. The ARM64
package is cross-compiled and never started by CI -- only the Pi, through
`deploy-server.sh`, has ever run it.
`tools/check-dedicated-server.sh` runs on the Windows runner too, in Git
Bash rather than a parallel PowerShell script: the binary is
`MphReadServer.exe`, `python3` may only be `python`, and a path has to go
through `cygpath` before .NET reads it as anything but a path on the current
drive.

Asset guard

`tools/check-no-game-assets.sh` runs twice in each workflow -- once over what
git tracks, once over what the build produced -- refusing game-file
extensions, extraction/cache directories, and (for tracked files) anything
over 2 MB. Map previews are the easy mistake: rendered locally, they look like
ordinary screenshots. Run it locally before pushing:

```bash
tools/check-no-game-assets.sh                    # the repository
tools/check-no-game-assets.sh publish/win-x64    # a build
```

Notes

- `AntiNotAnti/Prime-Hunters-Online` is public and is the single source, tag, release and updater repository. The release workflow uses its built-in `GITHUB_TOKEN`; no cross-repository release PAT is required. `Mods/Branding.cs` keeps `ReleaseRepository` as a semantic alias of `Repository`.
- `MPHREAD_SERVER` (defined on server builds) is a different question from "has no launcher": it is what makes a bare invocation print what the binary is for, instead of falling through to upstream's setup check.

## Updating in place

The front screen has always checked GitHub for a newer release
(`Mods/Update/UpdateCheck.cs`) and shown a badge. Two things are new: the
build is named in the corner of the picture, and on **Android** the update is
fetched and installed rather than pointed at.

- **The version corner is the button**, bottom right of the splash, front
  card only. `1.2.3` in green when it is the published build; `1.2.3 : Update
  available ! Click here to update` in amber when it is not, and pressing it
  is what takes the update. One control rather than a label and a button
  beside it, because the state and the action are the same fact here -- green
  is "nothing to do" and amber is "press this", so either arrangement leaves
  one of the two redundant in either state. The download's progress and any
  failure are written into the same line.
- **Dim is a third state, and it is not green.** A local build, and a check
  that has not answered. Painting "no answer" green is the one thing that line
  must never do: a server refuses a client on a different build at Hello, so
  being told you are current when nobody has asked is worse than being told
  nothing. `Updater.CheckInBackground` gained a `done` callback for it --
  before, "there is an update" and "you are up to date" were the same silence.
- **`UpdateBadge` stays, for every card but the front one.** It is over the
  splash so that a fresh install -- which sits on the game-files card, exactly
  where an out-of-date copy is most likely to be -- still sees it, and that
  card has no version corner. On the front card the two would be the same
  offer in opposite corners of one picture, so the badge hides there.
- **`BuildVersion` reads *this* assembly when there is no entry assembly**,
  which is every Android launch: the system starts an activity, so the process
  has no managed entry point and `Assembly.GetEntryAssembly()` answers null.
  Every APK therefore called itself "a local build", release or not, and the
  version line could never be anything but grey. The fallback is right on that
  platform because the Android head *compiles* MphRead's sources into its own
  assembly (`<Compile Include="..\MphRead\**\*.cs" />`) rather than
  referencing the project, so the file this class lives in is part of the APK's
  assembly -- and that assembly is the one `release.yml` stamps. Checked in
  `obj/.../MphRead.Android.AssemblyInfo.cs` against a build with
  `-p:InformationalVersion=1.2.3`.
- **Only a *tagged release* APK is stamped.** `build.yml`'s android job passes
  no version, so the `ProjectPrime-android` artifact from a push says "a local
  build" and that is correct, not a bug: an unstamped build has no way to tell
  whether it is ahead of the published release or behind it, and overwriting a
  developer's own binary with a download is the one failure the updater must
  never have.
- **`UpdateCheck.Rid()` answers `android`**, and not as an os-arch pair: the
  APK is one file for every ABI, so matching on the architecture found nothing
  on every phone. `UpdateInfo` now carries the asset's **URL and size** as
  GitHub gave them, never composed from the tag -- a release whose files were
  named differently gets no download rather than a guess.
- **`Mods/Update/UpdateInstall.cs` is the seam**, and both platforms now fill
  it. They have almost nothing in common; what they share is the shape of the
  conversation with the player, which is all the front screen wants. The split
  between `Prepare` and `Install` is the point of no return: everything that
  can fail belongs in the first, while the program is still running and can
  put the reason on screen. Left null, the front screen opens the release page
  exactly as before.

### The phone: `MphRead.Android/ApkInstaller.cs`

`PackageInstaller` rather than the deprecated `Intent.ACTION_INSTALL_PACKAGE`
that every example uses. Three things come of that: no `FileProvider` is
needed, since the bytes go into the session rather than through a content URI;
the result comes back as a status **with a reason**; and a silent install, if
it is ever wanted, is one flag on the same code.

The reason matters more than it looks. The failure this will actually hit is
`FAILURE_CONFLICT`, and Android's own words for it are "App not installed",
which sends people looking for a duplicate that is not there. `SameSigner`
checks for it *before* committing and says what it is.

Two things a player sees once: the per-app **install-source** permission
(Android 8+ replaced the global "unknown sources" switch with one Settings
screen per app, and nothing can wait for it -- so the entry stays pressable
and says "allow installs from this app, then press again"), and the system's
install dialog itself, which is started from here so no file manager is
involved. The download lives in the app's own cache, which no file manager on
Android 11+ can browse anyway.

**A successful install kills this process.** Android replaces the app to do
it and there is no supported way to survive that, so anything that must be on
disk has to be on disk before `Commit`. `Finished` is therefore only ever seen
when something went *wrong*.

### The desktop: `Mods/Update/DesktopUpdate.cs`

A program cannot overwrite the file it is running from -- Windows refuses, and
Unix allows it in a way that is worse than refusing. So the swap is done by **a
second copy of the new build**: the archive is unpacked into `.update/staged`
beside the installation, that binary is started with `-applyupdate <dir> <pid>`,
the launcher exits, and the new copy waits for the old process to be gone,
copies itself and everything beside it over the installation, and starts it
again.

Running the *new* build as the one doing the copying is what keeps it a single
mechanism: the old build never has to know how a future release wants to be
laid out, and the file doing the work is never one of the files being replaced.

Details that are not obvious and are each a bug if dropped:

- **Nothing is deleted.** It is a copy over the top, which is what the release
  page always told people to do by hand -- so `paths.txt`, `controls.txt`,
  saves and extracted game files stay where they are.
- **`.tar.gz` needs `System.Formats.Tar`, not a zip reader**, and that is not
  a preference: a zip carries no mode bits, so a Linux or macOS package
  unpacked as one arrives with a binary nobody can execute. `MakeExecutable`
  sets it either way.
- **The copy retries.** Windows keeps a handle a moment past exit and virus
  scanners keep it longer; a file that is still held is a file that will be
  free in a moment, not a failed update.
- **`Clean()` runs at startup**, not in the copying process, which cannot
  delete the directory it is running from.
- **`Supported` write-probes the install directory.** A system-wide install is
  read-only for the person running it, and the answer there is the release
  page rather than a failure half way through a copy.

Verified end to end here on 2026-09-04, against a staged build and a separate
"installed" directory: the payload was replaced, a file the "player" owned was
untouched, and the installed copy was relaunched.

### ⚠️ Android cannot work until releases are signed with one stable key

`release.yml` signs the APK with **the SDK's debug key**, which .NET for
Android generates on the machine doing the build. Every release runs on a
fresh runner, so **every release carries a different signing certificate**, and
Android refuses an update whose signature differs from the installed app. The
client code above is correct and will fail on every current release, by
design, with the message `SameSigner` produces.

`release.yml` now signs with one **when the secrets are set**, and falls back
to the debug key with a `::warning::` when they are not -- a fork with no
keystore still gets an installable APK, it simply cannot be updated in place.
The four secrets are `ANDROID_KEYSTORE` (the .jks, base64),
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`;
the passwords reach MSBuild as `env:NAME` rather than as values, which keeps
them out of the process table and the build log. The keystore is written to
`$RUNNER_TEMP`, outside the workspace, so no later step can archive it by
accident, and a step after the publish prints the APK's signer fingerprint --
which is what turns a future "App not installed" into a two-minute diff
against the last release's log.

Make the keystore once:

```
keytool -genkeypair -v -keystore projectprime.jks -alias projectprime \
  -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=Project Prime, O=Project Prime, C=FR"
```

No domain and no certificate authority: an Android signing certificate is
self-signed and nothing in it is ever validated. Only *sameness* is compared.
`-validity 10000` because an expired certificate cannot be renewed, only
replaced, and a replacement breaks every existing install. **Losing the file
means never being able to update anyone again.**

Where it lives is a real choice and not a detail. In CI secrets the workflow
signs on its own, but the key and the release trigger sit behind one account,
so compromising GitHub compromises both and the guarantee collapses back to
"GitHub was not compromised" -- which is the guarantee `Updater.cs` says is
not good enough to install on. Signing locally and uploading the signed APK
keeps the key off every server: somebody who takes the GitHub account can
then only publish a package that will not install anywhere.

Either way, **everybody already running a debug-signed APK has to reinstall by
hand once.** There is no migration from one certificate to another.


## macOS packaging

See [MACOS.md](MACOS.md) for signing, bundle layout, native smoke tests and
user-data paths. macOS uses whole-app replacement via the release page; the
file-copy updater is disabled there to preserve the bundle signature.
