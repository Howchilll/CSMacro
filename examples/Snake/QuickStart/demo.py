#!/usr/bin/env python3
"""QuickStart：机器人自动玩一局（种子固定，结果可复现）。

对应命令：dotnet run -- --demo
可透传额外参数，例如：python demo.py --seed 42
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
