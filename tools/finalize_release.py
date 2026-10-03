"""Prepare offline GitHub assets from a verified Xiaomi Revamp release.

Dependencies: Python standard library; existing sealed component ZIPs. Outputs: combined installer,
clean source ZIP, SHA256SUMS.txt and public-assets.json. No user profiles or preservation archives.
Command: python tools/finalize_release.py --release packages/NNN_UTC
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_suite import public_source
ROOT = Path(__file__).resolve().parents[1]


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True, help="Verified new sealed release directory")
    args = parser.parse_args()
    release = args.release.resolve()
    if not release.is_relative_to(ROOT / "packages") or not (release / "packages.json").is_file():
        raise ValueError("Use a sealed release in packages")
    manifest = json.loads((release / "packages.json").read_text(encoding="utf-8-sig"))
    for component in manifest["components"].values():
        assert checksum(release / component["payload"]) == component["sha256"]
    source_zip = release / "xiaomi-revamp-source.zip"
    with zipfile.ZipFile(source_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for directory in ["source", "tools", "checks"]:
            for path in public_source(ROOT / directory):
                archive.write(path, path.relative_to(ROOT).as_posix())
        archive.write(ROOT / "README.md", "README.md")
        archive.write(ROOT / "source/pc-manager/LICENSE", "LICENSE")
    print("PASS: clean source ZIP refreshed", flush=True)
    bundle = release / "Xiaomi-Revamp-Installer.zip"
    if bundle.exists():
        raise ValueError("The combined release asset already exists; use a new release")
    entries = ["Xiaomi-Revamp-Setup.exe", "AI-Center-Setup.exe", "Screen-Translator-Setup.exe", "Uninstall.exe",
               "packages.json", "README.md", "pc-manager.zip", "file-search.zip", "screen-translator.zip"]
    with zipfile.ZipFile(bundle, "x", compression=zipfile.ZIP_STORED) as archive:
        for name in entries:
            archive.write(release / name, name)
    assert bundle.stat().st_size < 2 * 1024**3, "GitHub single-asset size limit exceeded"
    names = [bundle.name, "development-toolchain.zip", "pc-manager-development.zip", "file-search-development.zip",
             "screen-translator-development.zip", source_zip.name]
    assets = [{"name": name, "bytes": (release/name).stat().st_size, "sha256": checksum(release/name)} for name in names]
    (release / "SHA256SUMS.txt").write_text("".join(f"{a['sha256']}  {a['name']}\n" for a in assets), encoding="utf-8")
    assets.append({"name": "SHA256SUMS.txt", "bytes": (release/"SHA256SUMS.txt").stat().st_size,
                   "sha256": checksum(release/"SHA256SUMS.txt")})
    (release / "public-assets.json").write_text(json.dumps({"version": "0.2.0", "assets": assets}, indent=2), encoding="utf-8")
    print(f"PASS: {len(assets)} public release assets; combined installer {bundle.stat().st_size/1024**2:.1f} MiB", flush=True)


if __name__ == "__main__":
    main()
