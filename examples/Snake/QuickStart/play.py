#!/usr/bin/env python3
"""QuickStart：交互模式自己玩（wasd / 方向键转向, P / 空格暂停, Esc / Q 退出）。

对应命令：dotnet run
需要真实终端；输入被重定向时会自动改跑 demo。
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
