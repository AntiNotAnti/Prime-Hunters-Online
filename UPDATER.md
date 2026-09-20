# Project Prime updater

Project Prime publishes source code, tags, release notes, and downloadable binaries from the same public repository:

`AntiNotAnti/Prime-Hunters-Online`

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

Open **Actions -> release -> Run workflow** in `AntiNotAnti/Prime-Hunters-Online`.

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

Release builds are stamped with the tag version. On launcher startup the client asks:

`https://api.github.com/repos/AntiNotAnti/Prime-Hunters-Online/releases/latest`

When a newer stable version exists:

- Windows and writable Linux installs download the matching archive, verify GitHub's SHA-256 asset digest, unpack to a staging directory, launch the new binary as the updater, replace the old application files, and restart.
- Android downloads the APK, verifies GitHub's SHA-256 asset digest, verifies the APK signer matches the installed application, and hands it to Android's installer.
- macOS opens this repository's release page because copying individual files into a signed app bundle invalidates its resource seal.
- Dedicated servers keep the existing safe-update behavior and only swap when the server lifecycle says it is safe.

Desktop replacement copies the new release over the installation. It does not delete unrelated player data such as settings, saves, extracted game data, or replays.

## Security model

The client never carries a GitHub token. It only consumes the public GitHub Releases API and accepts HTTPS release URLs supplied by GitHub from GitHub/GitHubusercontent hosts.

For one-click installation, the selected release asset must also have GitHub's `sha256:` digest. The downloaded bytes are hashed and compared before the package is unpacked or executed. A release whose asset has no supported digest can still be announced, but it falls back to the release page rather than silently executing an unverifiable package.

The release workflow also verifies that the repository remains public before publishing. This protects the updater contract from an accidental repository-visibility change.
