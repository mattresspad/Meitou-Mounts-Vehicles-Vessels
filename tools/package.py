"""Package the installable mods, or rebuild a tester handoff from Git HEAD."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


ROOT = Path(__file__).resolve().parents[1]
DIST = ROOT / "dist"
CORE = "Meitou Transport"
EXAMPLES = ("Western Hive Skiff", "Garru Saddle", "Stoat Desert Ride")
GUIDES = ("PLAYTEST.md", "AUTHORING.md", "LICENSING.md", "LICENSE", "LICENSES/CC0-1.0.txt")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def file_bytes(path):
    if path.name == "LICENSE" or path.suffix in (".md", ".json", ".txt"):
        return path.read_text(encoding="utf-8").encode("utf-8")
    return path.read_bytes()


def write_zip(destination, files):
    with ZipFile(destination, "w") as archive:
        for name, data in sorted(files.items()):
            entry = ZipInfo(name)  # Fixed timestamp and permissions on every platform.
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data, compress_type=ZIP_DEFLATED)


def sample_assets(folder):
    definition = read_json(folder / "transport.json")
    assets = set()
    for group, mesh in (("vessels", "hullPath"), ("mounts", "saddleMesh")):
        for entry in definition.get(group, []):
            for field in (mesh, "texture"):
                if name := entry.get(field):
                    path = PurePosixPath(name)
                    directory, suffixes = ("textures", {".dds", ".png"}) if field == "texture" else ("meshes", {".mesh"})
                    if (len(path.parts) < 2 or path.parts[0] != directory or ".." in path.parts
                            or "\\" in name or ":" in name or path.suffix.lower() not in suffixes):
                        raise ValueError(f"Invalid {field} in {folder.name}/transport.json: {name}")
                    assets.add(path.as_posix())
    return assets


def package_mods(project, stage):
    manifest = read_json(project / "mods" / CORE / "meitou.json")
    version = manifest["version"]
    guides = {name: file_bytes(project / name) for name in GUIDES}
    for name in (CORE, *EXAMPLES):
        folder = project / "mods" / name
        files = dict(guides)
        if name == CORE:
            if manifest["assemblies"] != ["Meitou.Transport.dll"]:
                raise ValueError("Check the framework assembly list before packaging this version.")
            members = ["meitou.json", *manifest["assemblies"]]
        else:
            members = ["transport.json", *sample_assets(folder)]
        for member in (f"{name}.mod", *members):
            files[member] = file_bytes(folder / member)
        write_zip(stage / f"{name.replace(' ', '-')}-{version}.zip",
                  {f"{name}/{path}": data for path, data in files.items()})
    return version


def handoff(stage):
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise ValueError("Commit the candidate changes before building a handoff from Git HEAD.")
    version = read_json(ROOT / "mods" / CORE / "meitou.json")["version"]
    prefix = f"Meitou-Transport-{version}"
    source = subprocess.check_output(["git", "archive", "--format=zip", f"--prefix={prefix}/", "HEAD"], cwd=ROOT)
    (stage / f"{prefix}-source.zip").write_bytes(source)
    with TemporaryDirectory(prefix="transport-build-") as work:
        with ZipFile(io.BytesIO(source)) as archive:
            archive.extractall(work)
        project = Path(work) / prefix
        env = os.environ.copy()
        release = Path(env.get("MEITOU_DIR") or ROOT / ".local/game/Meitou-0.18.0-win-x64")
        env["MEITOU_DIR"] = str(release.resolve())
        subprocess.run(["dotnet", "build", "framework/Meitou.Transport.csproj", "-c", "Release",
                        "-p:UseSharedCompilation=false"], cwd=project, env=env, check=True)
        package_mods(project, stage)
        files = {p.name: p.read_bytes() for p in stage.glob("*.zip")}
        files.update({name: file_bytes(project / name) for name in GUIDES})
    checksums = "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in sorted(files.items()))
    files["SHA256SUMS.txt"] = checksums.encode("ascii")
    write_zip(stage / f"{prefix}-handoff.zip", files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--handoff", action="store_true",
                        help="rebuild committed source, include it with the mods, guides and SHA-256 checksums")
    args = parser.parse_args()
    DIST.mkdir(exist_ok=True)
    with TemporaryDirectory(dir=DIST) as temp:
        stage = Path(temp)
        if args.handoff:
            handoff(stage)
        else:
            if not (ROOT / "mods" / CORE / "Meitou.Transport.dll").is_file():
                parser.exit(1, "Build framework/Meitou.Transport.csproj in Release first.\n")
            package_mods(ROOT, stage)
        for package in sorted(stage.iterdir()):
            destination = DIST / package.name
            package.replace(destination)
            print(destination)


if __name__ == "__main__":
    main()
