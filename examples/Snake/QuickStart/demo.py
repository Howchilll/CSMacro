#!/usr/bin/env python3
"""QuickStart: a bot plays one game (fixed seed, reproducible).

Equivalent command: dotnet run -- --demo
Extra arguments are passed through, e.g. python demo.py --seed 42
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    cmd = ["dotnet", "run", "--", "--demo", *sys.argv[1:]]
    print(f"$ {' '.join(cmd)}   (cwd={ROOT})")
    return subprocess.call(cmd, cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
