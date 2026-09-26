#!/usr/bin/env python3
import hashlib
import json
import pathlib
import sys

MANIFEST = ".project-prime-files.json"

if len(sys.argv) != 2:
    raise SystemExit("usage: write-release-manifest.py <publish-directory>")

root = pathlib.Path(sys.argv[1]).resolve()
if not root.is_dir():
    raise SystemExit(f"not a directory: {root}")

files = sorted(
    p.relative_to(root).as_posix()
    for p in root.rglob("*")
    if p.is_file() and p.name != MANIFEST
)
hashes = {}
for relative in files:
    digest = hashlib.sha256()
    with (root / relative).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    hashes[relative] = digest.hexdigest()

(root / MANIFEST).write_text(
    json.dumps({"Version": 1, "Files": files, "Hashes": hashes}, separators=(",", ":")),
    encoding="utf-8",
)
print(f"{root}: release manifest contains {len(files)} files")
