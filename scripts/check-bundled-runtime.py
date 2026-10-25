#!/usr/bin/env python3
"""Fails if any release archive bundles a .NET 10 runtime older than the latest patch.

The rhp tool is a self-contained single-file executable, so each archive carries the runtime of
whichever SDK built it, and an operating system update never patches it. A stale SDK would ship
months of security fixes behind and nothing would notice. This reads the runtime version out of
the program in each archive (the executable embeds its deps.json, which names the runtime pack
as runtimepack.Microsoft.NETCore.App.Runtime.<rid>/<version>) and compares it with the latest
10.0 runtime Microsoft lists. The RhpV2.Client NuGet package is a library with no runtime in it,
so it is not checked.

A runtime newer than the latest the index lists passes: SDK workloads can reach nuget.org a few
days before the release index lists that patch (10.0.14 on 2026-10-25, which failed another
packet-net release), and the point is to catch a stale build machine, not one ahead of the index.
It fails when a program bundles an older runtime, or zero or several recognisable versions.

Usage: scripts/check-bundled-runtime.py <archive.tar.gz|archive.zip>...
"""
import json
import re
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
PROGRAMS = ("rhp", "rhp.exe")
RUNTIME = re.compile(rb"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def program_bytes(archive):
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as z:
            return [(n, z.read(n)) for n in z.namelist() if n.rsplit("/", 1)[-1] in PROGRAMS]
    with tarfile.open(archive) as t:
        return [(m.name, t.extractfile(m).read()) for m in t.getmembers()
                if m.isfile() and m.name.rsplit("/", 1)[-1] in PROGRAMS]


def main(archives):
    if not archives:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for archive in map(Path, archives):
        programs = program_bytes(archive)
        if not programs:
            print(f"::error::{archive.name}: no program found to check")
            bad = True
        for name, data in programs:
            found = sorted({m.decode() for m in RUNTIME.findall(data)})
            print(f"{archive.name}: {name} bundles {', '.join(found) or 'nothing recognisable'}")
            if len(found) != 1:
                print(f"::error::{archive.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                      "not one recognisable version. Check how the program was built.")
                bad = True
            elif version_key(found[0]) < version_key(want):
                print(f"::error::{archive.name} bundles .NET runtime {found[0]}, older than {want}. "
                      "Update the .NET 10 SDK on the machine that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
