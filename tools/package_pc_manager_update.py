"""Package PC Manager alone with the verified runtime from a prior suite release.

Dependencies: Python 3.12, Windows Framework compiler and the existing build.
Outputs: self-contained EXE, equivalent setup ZIP, editable source and checksums.
Run: python -B tools/package_pc_manager_update.py --base OLD --build BUILD --output NEW
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import zipfile

from build_inclusive_installer import ROOT, compiler, digest
from build_suite import RELEASE, VERSIONS

COMPONENT = "pc-manager"
VERSION = VERSIONS[COMPONENT]


def package(base, build, output):
    """Copy a verified component archive, replacing only the new app and source."""
    manifest = json.loads((base / "packages.json").read_text(encoding="utf-8-sig"))
    component = manifest["components"][COMPONENT]
    if manifest["version"] != RELEASE or component["payload"] != "pc-manager.zip":
        raise ValueError("Use the verified component packages for suite " + RELEASE)
    old = base / component["payload"]
    if digest(old) != component["sha256"]:
        raise ValueError("Base PC Manager payload checksum failed")
    dependencies = json.loads((build / "PCManager.deps.json").read_text(encoding="utf-8"))
    if "PCManager/" + VERSION not in dependencies["libraries"]:
        raise ValueError("Build PC Manager " + VERSION + " first")
    toolchain = base / "development-toolchain.zip"
    if digest(toolchain) != manifest["developmentToolchain"]["sha256"]:
        raise ValueError("Base development toolchain checksum failed")

    replacements = {
        "runtime/dotnet/" + name: build / name
        for name in ("PCManager.dll", "PCManager.exe", "PCManager.deps.json", "tools/oem-service.ps1")
    }
    for name in component["files"]:
        if name.startswith("Development/source/"):
            replacements[name] = ROOT / name.removeprefix("Development/")
    for path in (build / "www").rglob("*"):
        if path.is_file():
            replacements["runtime/dotnet/" + path.relative_to(build).as_posix()] = path

    output.mkdir(parents=True, exist_ok=False)
    # Keep intermediates available to inspect and regenerate the self-extracting EXE.
    work = output / "setup"
    work.mkdir()
    payload = work / component["payload"]
    files = {}
    installed_bytes = 0
    with zipfile.ZipFile(old) as previous, zipfile.ZipFile(payload, "x", zipfile.ZIP_DEFLATED) as updated, \
            zipfile.ZipFile(output / "pc-manager-development.zip", "x", zipfile.ZIP_DEFLATED) as development:
        if set(previous.namelist()) != set(component["files"]) or set(replacements) - set(component["files"]):
            raise ValueError("Base package inventory or replacement paths did not match")
        for name, expected in component["files"].items():
            data = previous.read(name)
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError("Base file checksum failed: " + name)
            if name == "runtime/dotnet/PCManager.pdb":
                continue  # Debug symbols are not needed to run the public build.
            if name in replacements:
                data = replacements[name].read_bytes()
            elif name == "Development/development.json":
                metadata = json.loads(data)
                metadata["version"] = VERSION
                data = json.dumps(metadata, indent=2).encode("utf-8")
            updated.writestr(name, data)
            files[name] = hashlib.sha256(data).hexdigest()
            installed_bytes += len(data)
            if name.startswith("Development/"):
                development.writestr(name, data)
    component = dict(component, files=files, installedBytes=installed_bytes, sha256=digest(payload))
    manifest = dict(manifest, components={COMPONENT: component})
    (work / "packages.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"PASS: verified and staged {len(files)} PC Manager files", flush=True)

    compiler(ROOT / "source/shared/Setup.cs", work / "Xiaomi-Revamp-Setup.exe", "SUITE_SETUP")
    compiler(ROOT / "source/shared/Setup.cs", work / "Uninstall.exe", "UNINSTALL")
    compiler(ROOT / "source/shared/InstallerBootstrap.cs", work / "InstallerBootstrap.exe")
    (work / "README.txt").write_text(
        f"PC Manager {VERSION}\n\n"
        "Quit Revamp PC Manager from its tray. Run Xiaomi-Revamp-Setup.exe with all setup files together.\n"
        "If it reopens, turn off Restart automatically if the app exits in Settings before quitting.\n"
        "Choose the existing Xiaomi Revamp folder and keep its user scope and startup option for an upgrade.\n"
        "To install an editable copy, place development-toolchain.zip beside setup first.\n"
        "This installer only contains PC Manager and does not alter stock Xiaomi software.\n",
        encoding="utf-8",
    )
    bundle = output / "PC-Manager-Installer.zip"
    with zipfile.ZipFile(bundle, "x", zipfile.ZIP_STORED) as archive:
        for name in ("Xiaomi-Revamp-Setup.exe", "Uninstall.exe", "packages.json", "pc-manager.zip", "README.txt"):
            archive.write(work / name, name)
    installer = output / "PC-Manager-Installer.exe"
    with installer.open("xb") as target, (work / "InstallerBootstrap.exe").open("rb") as stub, bundle.open("rb") as source:
        shutil.copyfileobj(stub, target)
        offset = target.tell()
        shutil.copyfileobj(source, target)
        target.write(struct.pack("<QQ", offset, bundle.stat().st_size) + bytes.fromhex(digest(bundle)) + b"XRSETUP1")
    shutil.copyfile(toolchain, output / toolchain.name)
    assets = (installer, bundle, output / "pc-manager-development.zip", output / toolchain.name)
    (output / "SHA256SUMS.txt").write_text(
        "".join(f"{digest(path)}  {path.name}\n" for path in assets), encoding="utf-8"
    )
    print(f"PASS: PC Manager {VERSION} offline EXE, ZIP and development packages", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True, help="Prior verified suite package directory")
    parser.add_argument("--build", type=Path, required=True, help="PC Manager Release build output")
    parser.add_argument("--output", type=Path, required=True, help="New directory for release assets")
    args = parser.parse_args()
    package(args.base.resolve(), args.build.resolve(), args.output.resolve())
