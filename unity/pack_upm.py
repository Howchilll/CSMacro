#!/usr/bin/env python3
"""
Assembles the com.howmacro.unity UPM package.

The package sources in unity/com.howmacro.unity hold no binaries. This script adds them:
  Bridge~/                          dotnet publish of src/HowMacro.Unity.Bridge (Roslyn + the engine; never imported by Unity)
  Plugins/HowMacro.Abstractions.dll the macro API (netstandard2.0)
  Editor/CodeGen/BridgeStamp.cs     a hash of the above, so a new bridge makes Unity expand everything again
and sets the version in package.json.

  python unity/pack_upm.py                          -> artifacts/upm/com.howmacro.unity, version from Directory.Build.props
  python unity/pack_upm.py --out <dir> --version 0.2.0-preview

Use the output as a local package (Packages/manifest.json: "com.howmacro.unity": "file:<dir>"), or let the upm workflow
publish it to the upm branch. Needs the .NET SDK (8 or later).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "unity", "com.howmacro.unity")


def run(*args):
    print("+ " + " ".join(args), flush=True)
    subprocess.run(args, cwd=ROOT, check=True)


def default_version():
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8") as f:
        return re.search(r"<HowMacroVersion>([^<]+)</HowMacroVersion>", f.read()).group(1).strip()


def stamp(paths):
    """Hash of the given files' names and bytes, in a stable order."""
    h = hashlib.sha256()
    for path in sorted(paths, key=lambda p: os.path.basename(p).lower()):
        h.update(os.path.basename(path).encode())
        with open(path, "rb") as f:
            h.update(f.read())
    return h.hexdigest()[:16]


def main():
    parser = argparse.ArgumentParser(description="Assemble the com.howmacro.unity UPM package.")
    parser.add_argument("--out", default=os.path.join(ROOT, "artifacts", "upm", "com.howmacro.unity"))
    parser.add_argument("--version", default=None, help="package version (default: HowMacroVersion in Directory.Build.props)")
    args = parser.parse_args()
    out = os.path.abspath(args.out)
    version = (args.version or default_version()).lstrip("v")

    if os.path.exists(out):
        shutil.rmtree(out)
    shutil.copytree(SOURCE, out, ignore=shutil.ignore_patterns("Bridge~", "*.dll", "bin", "obj"))

    bridge = os.path.join(out, "Bridge~")
    run("dotnet", "publish", "src/HowMacro.Unity.Bridge", "-c", "Release", "-o", bridge, "-nologo")
    for name in os.listdir(bridge):
        if name.endswith(".xml"):  # XML docs aren't needed at expansion time
            os.remove(os.path.join(bridge, name))

    run("dotnet", "build", "src/HowMacro.Abstractions", "-c", "Release", "-nologo")
    abstractions = os.path.join(out, "Plugins", "HowMacro.Abstractions.dll")
    shutil.copy2(os.path.join(ROOT, "src", "HowMacro.Abstractions", "bin", "Release", "netstandard2.0", "HowMacro.Abstractions.dll"), abstractions)

    bridge_files = [os.path.join(dp, f) for dp, _, fs in os.walk(bridge) for f in fs]
    value = stamp(bridge_files + [abstractions])
    stamp_file = os.path.join(out, "Editor", "CodeGen", "BridgeStamp.cs")
    with open(stamp_file, encoding="utf-8") as f:
        text = f.read()
    text, n = re.subn(r'public const string Value = "[^"]*";', 'public const string Value = "%s";' % value, text)
    if n != 1:
        sys.exit("BridgeStamp.cs: couldn't find the Value constant")
    with open(stamp_file, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)

    package_json = os.path.join(out, "package.json")
    with open(package_json, encoding="utf-8") as f:
        package = json.load(f)
    package["version"] = version
    with open(package_json, "w", encoding="utf-8", newline="\n") as f:
        json.dump(package, f, indent=2)
        f.write("\n")

    size = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(out) for f in fs)
    print("com.howmacro.unity %s (bridge %s, %.1f MB) -> %s" % (version, value, size / 1e6, out))


if __name__ == "__main__":
    main()
