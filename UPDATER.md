# Project Prime updater

Project Prime publishes source code, tags, release notes, and downloadable binaries from the same public repository:

`AntiNotAnti/Project-Prime`

The in-app updater reads the repository's public GitHub Releases API. No GitHub credential is embedded in client builds.

## One-time GitHub setup

The release workflow uses GitHub Actions' built-in `GITHUB_TOKEN` with repository `contents: write` permission. No personal access token or separate release repository is required.

For Android in-place updates, configure these repository Actions secrets:

- `ANDROID_KEYSTORE` — base64-encoded .jks signing keystore
- `ANDROID_KEYSTORE_PASSWORD`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`

Android rejects an in-place update signed by a different certificate, so keep the release signing key stable and backed up securely.

## Publishing an update

### GitHub Actions

Open **Actions -> release -> Run workflow** in `AntiNotAnti/Project-Prime`.

- Choose `patch`, `minor`, or `major`.
- Leave **publish** enabled to publish after every build and verification job succeeds.
- Turn **publish** off to create or refresh a draft release instead.

The workflow creates the requested version tag when using a bump, builds every supported package, generates release notes, and publishes the assets directly to this repository's GitHub Release.

### Git tag

Pushing a normal release tag also publishes after successful builds:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Tags must use `vMAJOR.MINOR.PATCH`.

## Client behavior

Release builds are stamped with the tag version. When automatic update checks are enabled, the launcher asks for the latest release at startup and then every five minutes while the launcher UI is attached:

`https://api.github.com/repos/AntiNotAnti/Project-Prime/releases/latest`

A newly discovered stable release updates the build chip immediately. When the player is on the hub it also opens an in-app update prompt; if another launcher screen is active, the prompt is deferred until the player returns to the hub. Choosing **Later** suppresses that tag for the rest of the current process, while a newer tag can still prompt.

The build chip also opens **Version Manager**. It reads the stable published release history from:

`https://api.github.com/repos/AntiNotAnti/Project-Prime/releases`

Automatic updates remain forward-only. Version Manager is the explicit path that may select an older release.

- Windows and writable Linux installs can switch both forward and backward. The matching archive is downloaded, GitHub's SHA-256 asset digest is verified, the release is unpacked to a staging directory, the staged binary applies the swap, and Project Prime restarts.
- Android upgrades keep the existing verified APK installer. Android does not allow a lower APK version code to be installed over a newer one, so downgrades are presented as a manual release-page path instead of downloading a package that the system will reject.
- macOS opens the selected release page for both upgrades and downgrades because copying individual files into a signed app bundle invalidates its resource seal.
- Dedicated servers keep the existing safe-update behavior and only swap when the server lifecycle says it is safe. The client Version Manager does not change server auto-update policy.

Desktop replacement is manifest-driven. Every new desktop package records the files owned by that release. During an in-app update, files owned by the previous release but absent from the new one are removed before the new files are copied. Player-owned data is never inferred from the package manifest and is preserved: settings, controls, saves, extracted game data, replays, custom maps and user-created media remain untouched.

The first upgrade from a pre-manifest build uses a conservative legacy cleanup that only removes historical Project Prime/Fruity Prime/MphRead executable/runtime names. The staged package also generates a manifest defensively, so Version Manager and older release packages converge on the same clean-install layout after one manifest-aware update.

## Security model

The client never carries a GitHub token. It only consumes the public GitHub Releases API and accepts HTTPS release URLs supplied by GitHub from GitHub/GitHubusercontent hosts.

For one-click installation, the selected release asset must also have GitHub's `sha256:` digest. The downloaded bytes are hashed and compared before the package is unpacked or executed. A release whose asset has no supported digest can still be announced, but it falls back to the release page rather than silently executing an unverifiable package.

The release workflow also verifies that the repository remains public before publishing. This protects the updater contract from an accidental repository-visibility change.
