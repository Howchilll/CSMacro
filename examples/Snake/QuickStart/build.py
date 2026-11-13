#!/usr/bin/env python3
"""QuickStart：构建 Snake（首次会从本地包源还原 CSMacro 包）。

对应命令：dotnet build
"""
import subprocess
import sys
from pathlib import Path

# 示例项目根目录（QuickStart 的上一级），保证在任意位置运行结果一致
ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    print(f"$ dotnet build   (cwd={ROOT})")
    return subprocess.call(["dotnet", "build"], cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
