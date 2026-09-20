Fruity Prime for macOS

Extract the tar.gz and open "Fruity Prime.app". You can move the app to
Applications. Use osx-arm64 for Apple Silicon, osx-x64 for Intel.

Bring your own Metroid Prime Hunters cartridge dump (.nds). No game data is
included or downloaded. Settings, extracted files, saves and logs are stored
in ~/Library/Application Support/Fruity Prime/.

Command-line launch:
  "./Fruity Prime.app/Contents/MacOS/FruityPrime"

Startup diagnostics without a cartridge, display or audio device:
  "./Fruity Prime.app/Contents/MacOS/FruityPrime" -smoketest

These builds are ad-hoc signed, not Apple notarized. If macOS blocks a download
you trust, use System Settings > Privacy & Security > Open Anyway. If needed,
remove quarantine from this app only, from the directory containing it:
  xattr -dr com.apple.quarantine "Fruity Prime.app"

To update, download the new archive and replace the app. Your user data stays
in Application Support. Older portable installations can keep their data:
copy their contents (paths.txt, settings, saves and extracted files) into the
user-data folder before launching. Absolute paths in paths.txt must still point
to the extracted files; rerun game-file setup if those files have moved.

F11 or Alt+Enter switches fullscreen; Escape opens the pause menu.
