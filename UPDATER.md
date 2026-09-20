# Prime Hunters Online updater

The source repository stays private. Client updates are published as binaries to the public repository:

`AntiNotAnti/Prime-Hunters-Online-Releases`

## One-time GitHub setup

1. Create **AntiNotAnti/Prime-Hunters-Online-Releases** as a **public** repository and initialize its `main` branch. A README is enough.
2. Create a fine-grained GitHub personal access token scoped only to that release repository with **Contents: read and write**.
3. In the private source repository, add that token as the Actions secret **RELEASE_REPO_TOKEN**.
4. For Android in-place updates, configure the existing `ANDROID_KEYSTORE`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, and `ANDROID_KEY_PASSWORD` secrets. Android rejects an update signed by a different certificate.

Never put the release token in client code. It exists only in GitHub Actions.

## Publishing an update

### GitHub Actions

Open **Actions -> release -> Run workflow** in the private source repository.

- Choose `patch`, `minor`, or `major`.
- Leave **publish** enabled to release to clients after every build/check succeeds.
- Turn **publish** off to create or refresh a draft release instead.

### Git tag

Pushing a normal release tag automatically publishes after successful builds:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Tags must use `vMAJOR.MINOR.PATCH`.

## Client behavior

Release builds are stamped with the tag version. On launcher startup the client asks the public release repository for `releases/latest`.

When a newer stable version exists:

- Windows and writable Linux installs download the matching archive, verify GitHub's SHA-256 asset digest, unpack to a staging directory, launch the new binary as the updater, replace the old application files, and restart.
- Android downloads the APK, verifies GitHub's SHA-256 asset digest, verifies the APK signer matches the installed application, and hands it to Android's installer.
- macOS opens the release page because copying individual files into a signed app bundle invalidates its resource seal.
- Dedicated servers keep the existing safe-update behavior and only swap when the server lifecycle says it is safe.

Desktop replacement copies the new release over the installation. It does not delete unrelated player data such as settings, saves, extracted game data, or replays.

## Security model

The client never carries a GitHub token. It only accepts HTTPS release URLs supplied by GitHub and only from GitHub/GitHubusercontent hosts.

For one-click installation, the selected release asset must also have GitHub's `sha256:` digest. The downloaded bytes are hashed and compared before the package is unpacked or executed. A release whose asset has no supported digest can still be announced, but it falls back to the release page rather than silently executing an unverifiable package.
