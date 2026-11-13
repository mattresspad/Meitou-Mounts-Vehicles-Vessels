"""Check a tester kit against its checkout and rebuild all installable ZIPs from its source."""

import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
CORE = "Meitou Transport"
PACKS = (CORE, "Garru Saddle", "Western Hive Skiff", "Stoat Desert Ride")


def check():
    version = json.loads((ROOT / "mods" / CORE / "meitou.json").read_bytes())["version"]
    prefix = f"Meitou-Transport-{version}"
    names = [f"{pack.replace(' ', '-')}-{version}.zip" for pack in PACKS]
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT).decode().strip()
    env = os.environ.copy()
    env["MEITOU_DIR"] = str(Path(env.get("MEITOU_DIR") or ROOT / ".local/game/Meitou-0.18.0-win-x64").resolve())

    with ZipFile(ROOT / "dist" / f"{prefix}-handoff.zip") as kit, TemporaryDirectory(prefix="transport test ") as temp:
        checksums = kit.read("SHA256SUMS.txt").decode("ascii").splitlines()
        expected = sorted(f"{hashlib.sha256(kit.read(name)).hexdigest()}  {name}"
                          for name in kit.namelist() if name != "SHA256SUMS.txt")
        if sorted(checksums) != expected:
            raise ValueError("The kit checksum list does not match its contents.")
        with ZipFile(io.BytesIO(kit.read(f"{prefix}-source.zip"))) as source:
            if source.comment.decode("ascii") != commit:
                raise ValueError("The source ZIP is from a different Git commit.")
            source.extractall(temp)
        project = Path(temp) / prefix
        start_file = project / "mods" / "Stoat Desert Ride" / "Stoat Desert Ride.mod"
        authored_start = start_file.read_bytes()
        generator = "tools/DesertRide/DesertRide.csproj"
        subprocess.run(["dotnet", "build", generator, "-c", "Release", "-p:UseSharedCompilation=false"],
                       cwd=project, env=env, check=True)
        subprocess.run(["dotnet", "tools/DesertRide/bin/Release/net11.0/DesertRide.dll"],
                       cwd=project, env=env, check=True)
        if start_file.read_bytes() != authored_start:
            raise ValueError("The FCS generator produced a different recording start.")
        for tree in (ROOT, project):
            subprocess.run(["dotnet", "build", "framework/Meitou.Transport.csproj", "-c", "Release",
                            "-t:Rebuild", "-p:UseSharedCompilation=false"], cwd=tree, env=env, check=True)
            subprocess.run([sys.executable, "tools/package.py"], cwd=tree, env=env, check=True)
            for name in names:
                if (tree / "dist" / name).read_bytes() != kit.read(name):
                    raise ValueError(f"Rebuilt {name} differs from the tester kit in {tree}.")
    print("All checksums and the source commit match. All mod ZIPs rebuild byte for byte.")


if __name__ == "__main__":
    check()
