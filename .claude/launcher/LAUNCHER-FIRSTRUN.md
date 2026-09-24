# Launcher — first run and extraction

This file documents the first-run flow and the extraction child process used to unpack a .nds.

- The extraction uses upstream's `Extract.Setup` in a child process: it prints questions and expects stdin answers. The child is run so the GUI does not block on `Console.ReadKey`.
- ROM acceptance is based on a supported MPH game-code/revision plus structural/layout validation, not an exact whole-file hash. Padded/trimmed or otherwise byte-different dumps remain usable when the data layout Project Prime reads is compatible.
- Consequences:
  - `-launcher` is dispatched before upstream's `CheckSetup` to avoid a "press any key" console stop.
  - `GameFiles.Problem()` signals the rest of the screen whether paths are missing or invalid.

Progress bar

- `SetupProgress` classifies each output line into a phase (validation, writing files, unpacking, converting music, decompressing code) and moves asymptotically within that phase; total is unknown and a counting pass would require reading the cartridge twice.
- Preview rendering is deliberately outside the setup progress. Once extracted files validate, setup reaches **Ready to play** immediately; missing map previews are cosmetic background work.

UI behaviour

- During extraction the progress is drawn in the card; the console draws it with carriage returns only when stdout is a terminal. In a pipe or log the carriage return makes unreadable files.
- The GUI closes the setup sheet as soon as game files are ready, then starts missing preview generation from the persistent launcher. A preview worker/GL failure is logged and cannot make ROM setup fail or appear frozen.

On macOS the extraction child and launcher use the same Application Support
directory (`Mods/Platform/AppPaths.cs`), so paths.txt and extracted game files
never modify the signed app. Existing portable data is not moved automatically;
see `tools/macos-README.txt` for migration and whole-app updates.
