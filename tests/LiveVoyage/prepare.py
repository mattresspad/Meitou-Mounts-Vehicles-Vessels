"""Stage a release acceptance profile in an empty mods directory."""

import argparse
import json
from pathlib import Path
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[2]
CORE = "Meitou Transport"
EXAMPLE = "Western Hive Skiff"
SADDLE = "Garru Saddle"


def release(name):
    version = json.loads((ROOT / "mods" / CORE / "meitou.json").read_text(encoding="utf-8"))["version"]
    return ZipFile(ROOT / "dist" / f"{name.replace(' ', '-')}-{version}.zip")


def make_consumer(mods, example):
    # Execute the copy-and-rename recipe from the guide that ships in the
    # framework ZIP. The example ZIP stays closed to the game's mod loader.
    guide = (mods / CORE / "AUTHORING.md").read_text(encoding="utf-8")
    definition = guide.split("```json\n", 1)[1].split("\n```", 1)[0]
    pack = json.loads(definition)
    original = json.loads(example.read(f"{EXAMPLE}/transport.json"))
    source, target = original["vessels"][0], pack["vessels"][0]
    folder = mods / "Skiff"
    folder.mkdir()
    (folder / "Skiff.mod").write_bytes(example.read(f"{EXAMPLE}/{EXAMPLE}.mod"))
    for field in ("hullPath", "texture"):
        destination = folder / target[field]
        destination.parent.mkdir(exist_ok=True)
        destination.write_bytes(example.read(f"{EXAMPLE}/{source[field]}"))
    (folder / "transport.json").write_text(definition + "\n", encoding="utf-8")


def prepare(folder, scenario):
    folder.mkdir(parents=True, exist_ok=True)
    if any(folder.iterdir()):
        raise ValueError(f"Use an empty QA mods directory: {folder}")
    with release(CORE) as archive:
        archive.extractall(folder)
    if scenario in ("example", "author", "mixed"):
        with release(EXAMPLE) as archive:
            if scenario in ("example", "mixed"):
                archive.extractall(folder)
            else:
                make_consumer(folder, archive)
    if scenario in ("garru", "bull", "mixed", "stoat"):
        with release(SADDLE) as archive:
            if scenario == "bull":
                name = "Pack Bull Saddle"
                target = folder / name
                target.mkdir()
                guide = (folder / CORE / "AUTHORING.md").read_text(encoding="utf-8").split("## Fit a Pack Bull saddle", 1)[1]
                definition = guide.split("```json\n", 1)[1].split("\n```", 1)[0]
                (target / "transport.json").write_text(definition+"\n", encoding="utf-8")
                (target / (name+".mod")).write_bytes(archive.read(f"{SADDLE}/{SADDLE}.mod"))
                original = json.loads(archive.read(f"{SADDLE}/transport.json"))["mounts"][0]
                fit = json.loads(definition)["mounts"][0]
                for asset in ("saddleMesh", "texture"):
                    dest = target / fit[asset]
                    dest.parent.mkdir(exist_ok=True)
                    dest.write_bytes(archive.read(f"{SADDLE}/{original[asset]}"))
            else:
                archive.extractall(folder)
    if scenario == "stoat":
        with release("Stoat Desert Ride") as archive:
            archive.extractall(folder)
    print("Installed:", ", ".join(sorted(p.name for p in folder.iterdir())))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scenario", choices=["core", "example", "author", "garru", "bull", "mixed", "stoat"])
    parser.add_argument("mods", type=Path)
    args = parser.parse_args()
    prepare(args.mods, args.scenario)
