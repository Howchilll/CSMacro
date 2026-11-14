#!/usr/bin/env python3
"""QuickStart: play interactively (wasd / arrow keys to turn, P / Space to pause, Esc / Q to quit).

Equivalent command: dotnet run
Needs a real terminal; when input is redirected it runs the demo instead.
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    cmd = ["dotnet", "run", *sys.argv[1:]]
    print(f"$ {' '.join(cmd)}   (cwd={ROOT})")
    return subprocess.call(cmd, cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
