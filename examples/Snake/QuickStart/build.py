#!/usr/bin/env python3
"""QuickStart: build Snake (the first build restores the HowMacro package from the local feed).

Equivalent command: dotnet build
"""
import subprocess
import sys
from pathlib import Path

# The example's root folder (QuickStart's parent), so the script works from any directory
ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    print(f"$ dotnet build   (cwd={ROOT})")
    return subprocess.call(["dotnet", "build"], cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
