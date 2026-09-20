#!/usr/bin/env python3
"""Exercise the published smoke test with no display, assets, or audio device.

Usage: python3 tools/test-compatibility-smoke.py publish/<rid>/FruityPrime
Also accepts a framework-dependent FruityPrime.dll (runs it through dotnet).
Only the temporary copy is modified, never the supplied build.
"""

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def run(binary, expected_code, expected_line):
    command = [str(binary), "-smoketest"]
    if binary.suffix == ".dll":
        command.insert(0, "dotnet")
    environment = os.environ.copy()
    # macOS keeps writable state in Application Support beneath HOME.
    # Exercise first launch without reading or writing the developer's state.
    home = binary.parent.parent / "isolated home"
    home.mkdir(exist_ok=True)
    environment.update(HOME=str(home), DOTNET_CLI_HOME=str(home))
    environment.pop("DISPLAY", None)
    environment.pop("WAYLAND_DISPLAY", None)
    result = subprocess.run(command, env=environment, cwd=binary.parent.parent,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=60)
    print(result.stdout, end="")
    assert result.returncode == expected_code, (result.returncode, expected_code)
    assert expected_line in result.stdout, expected_line
    return result.stdout


def main():
    source = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="fruity smoke ") as temporary:
        package = Path(temporary) / "relocated package"
        shutil.copytree(source.parent, package)
        binary = package / source.name
        output = run(binary, 0, "Smoke test passed.")
        if sys.platform == "darwin":
            assert "[OK] OpenAL bindings" in output

        maps = package / "maps"
        maps.rename(package / "maps.hidden")
        run(binary, 1, "[FAIL] maps")
        (package / "maps.hidden").rename(maps)

        if sys.platform == "darwin":
            openal = package / "libopenal.1.dylib"
            openal.rename(package / "libopenal.1.dylib.hidden")
            # The binding must fail too, even if Apple's framework is present.
            output = run(binary, 1, "[FAIL] OpenAL bindings")
            assert "[FAIL] libopenal.1.dylib" in output
    print("Compatibility smoke regressions passed.")


if __name__ == "__main__":
    main()
