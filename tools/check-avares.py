#!/usr/bin/env python3
"""Every avares:// URI the launcher names has to be in the package it runs from.

Avalonia's asset scheme resolves against what a project declared as an
<AvaloniaResource>, and there are two projects declaring the same screens'
assets: the desktop one, where the files live, and the Android head, which
compiles the same sources.  A font the desktop ships and the head does not is
not a warning and not a missing picture -- Avalonia does not fall back for a
font, it throws `Could not create glyphTypeface` out of the measure pass, and
the app dies on its first layout on a device while every build stays green.

That is exactly what happened: the deck theme added three weights of Pixelify
Sans and two of JetBrains Mono, every label on every screen began naming one of
them, and the head's own list was not touched.

So: read the URIs out of the sources, ask MSBuild what the project actually
declares, and compare.  Run once per project.

    tools/check-avares.py src/MphRead/MphRead.csproj -p:RuntimeIdentifier=linux-x64
"""
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
URI = re.compile(r'avares://[A-Za-z0-9_.]+/([^"\')\s]+)')


def wanted():
    """Every avares path named in the shared sources, and who names it."""
    found = {}
    for source in sorted(ROOT.glob("src/**/*.cs")):
        if "/bin/" in str(source) or "/obj/" in str(source):
            continue
        for line, text in enumerate(source.read_text(errors="replace").splitlines(), 1):
            for match in URI.finditer(text):
                # The fragment after "#" is the font family inside the file,
                # not part of the path.
                path = match.group(1).split("#")[0]
                # Built at runtime from a variable -- nothing to check here.
                if "{" in path or not path:
                    continue
                found.setdefault(path.replace("\\", "/"), []).append(
                    f"{source.relative_to(ROOT)}:{line}")
    return found


def declared(project, extra):
    """Every asset path the project ships, as the URI would spell it."""
    result = subprocess.run(
        ["dotnet", "msbuild", project, "-getItem:AvaloniaResource", *extra],
        capture_output=True, text=True, cwd=ROOT)
    if result.returncode != 0:
        print(result.stdout[-4000:], file=sys.stderr)
        print(result.stderr[-4000:], file=sys.stderr)
        sys.exit(f"could not read {project}'s items")
    # MSBuild prints the JSON after whatever the SDK had to say first.
    text = result.stdout[result.stdout.index("{"):]
    items = json.loads(text)["Items"].get("AvaloniaResource", [])
    paths = set()
    for item in items:
        # Link is where the file lands in the package; Identity is that too
        # when the file is already inside the project.
        path = item.get("Link") or item["Identity"]
        paths.add(path.replace("\\", "/"))
    return paths


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    project = sys.argv[1]
    ships = declared(project, sys.argv[2:])
    missing = {path: where for path, where in wanted().items() if path not in ships}
    for path, where in sorted(missing.items()):
        print(f"{project} does not ship {path}")
        for one in where:
            print(f"    named at {one}")
    if missing:
        sys.exit(f"{len(missing)} asset(s) named by the launcher are not in this package")
    print(f"{project}: every avares:// asset the launcher names is shipped")


if __name__ == "__main__":
    main()
