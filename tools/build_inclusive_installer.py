"""Seal a complete single-EXE release with explicitly selected app replacements.

Dependencies: Python stdlib, Windows Framework compiler, verified previous packages.
Outputs: a new release, deduplicated payload and self-extracting EXE below 2 GiB.
Command: private-python -B tools/build_inclusive_installer.py --previous OLD_RELEASE --changes changes.json --output NEW_RELEASE
"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
from pathlib import Path
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_suite import public_source

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def compiler(source, output, define=None):
    executable = Path(os.environ["SystemRoot"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    references = ["System.Windows.Forms", "System.Drawing", "System.IO.Compression", "System.IO.Compression.FileSystem", "System.Web.Extensions"]
    command = [str(executable), "/nologo", "/target:winexe", "/platform:x64", "/out:" + str(output), "/win32manifest:" + str(ROOT / "source/shared/Setup.manifest")]
    command += ["/reference:" + name + ".dll" for name in references]
    if define:
        command.append("/define:" + define)
    subprocess.run(command + [str(source)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("previous", "output"):
        parser.add_argument("--" + name, required=True, type=Path, help=name + " path")
    parser.add_argument("--changes", type=Path, help="Optional JSON of already-built app files to replace")
    args = parser.parse_args()
    previous, release = args.previous.resolve(), args.output.resolve()
    release.mkdir(parents=True, exist_ok=False)
    manifest = json.loads((previous / "packages.json").read_text(encoding="utf-8-sig"))
    for name, component in manifest["components"].items():
        if digest(previous / component["payload"]) != component["sha256"]:
            raise ValueError("Previous package checksum failed: " + name)
        shutil.copy2(previous / component["payload"], release / component["payload"])
        shutil.copy2(previous / (name + "-development.zip"), release / (name + "-development.zip"))
    shutil.copy2(previous / "development-toolchain.zip", release / "development-toolchain.zip")
    shutil.copy2(previous / "packages.json", release / "packages.json")
    if args.changes:
        subprocess.run(["pwsh", "-NoProfile", "-File", str(ROOT / "tools/seal_component_changes.ps1"), "-Release", str(release), "-Changes", str(args.changes.resolve())], check=True)
    manifest = json.loads((release / "packages.json").read_text(encoding="utf-8-sig"))
    replacements = json.loads(args.changes.read_text(encoding="utf-8")) if args.changes else {}
    if args.changes:
        for name, files in replacements.items():
            for relative, source in files.items():
                if manifest["components"][name]["files"].get(relative) != digest(source):
                    raise ValueError("Staged replacement was not sealed: " + name + "/" + relative)
    previous_manifest = json.loads((previous / "packages.json").read_text(encoding="utf-8-sig"))
    for name, component in manifest["components"].items():
        if name not in replacements and component != previous_manifest["components"][name]:
            raise ValueError("Unselected component changed: " + name)
    for name, define in (("Xiaomi-Revamp-Setup.exe", "SUITE_SETUP"), ("AI-Center-Setup.exe", "SEARCH_SETUP"), ("Screen-Translator-Setup.exe", "TRANSLATOR_SETUP"), ("Uninstall.exe", "UNINSTALL")):
        compiler(ROOT / "source/shared/Setup.cs", release / name, define)
    shutil.copy2(ROOT / "README.md", release / "README.md")
    store = release / "payload.zip"
    written = set()
    paths = 0
    tool_files = {}
    with zipfile.ZipFile(store, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as target:
        for name, component in manifest["components"].items():
            print("STEP: packing " + name + " without duplicate files", flush=True)
            with zipfile.ZipFile(release / component["payload"]) as source:
                for relative, expected in component["files"].items():
                    paths += 1
                    if expected in written:
                        continue
                    with source.open(relative) as input_stream, target.open(expected, "w", force_zip64=True) as output_stream:
                        checksum = hashlib.sha256()
                        while block := input_stream.read(1024 * 1024):
                            checksum.update(block)
                            output_stream.write(block)
                        if checksum.hexdigest() != expected:
                            raise ValueError("Package file checksum failed: " + relative)
                    written.add(expected)
                    if len(written) % 3000 == 0:
                        print("STEP: " + str(len(written)) + " unique files packed", flush=True)
        print("STEP: including the complete offline development toolchain", flush=True)
        with zipfile.ZipFile(release / "development-toolchain.zip") as source:
            for entry in source.infolist():
                data = source.read(entry)
                expected = hashlib.sha256(data).hexdigest()
                tool_files[entry.filename] = expected
                paths += 1
                if expected not in written:
                    target.writestr(expected, data)
                    written.add(expected)
    inline = dict(manifest)
    inline["contentStore"] = {"payload": store.name, "sha256": digest(store)}
    inline["developmentToolchain"] = {**manifest["developmentToolchain"], "files": tool_files}
    (release / "inline-packages.json").write_text(json.dumps(inline, indent=2), encoding="utf-8")
    outer = release / "embedded-setup.zip"
    with zipfile.ZipFile(outer, "x", compression=zipfile.ZIP_STORED) as archive:
        for name in ("Xiaomi-Revamp-Setup.exe", "AI-Center-Setup.exe", "Screen-Translator-Setup.exe", "Uninstall.exe", "README.md", "payload.zip"):
            archive.write(release / name, name)
        archive.write(release / "inline-packages.json", "packages.json")
    stub = release / "InstallerBootstrap.exe"
    compiler(ROOT / "source/shared/InstallerBootstrap.cs", stub)
    installer = release / "Xiaomi-Revamp-Installer.exe"
    with installer.open("xb") as output:
        with stub.open("rb") as source:
            shutil.copyfileobj(source, output)
        offset = output.tell()
        with outer.open("rb") as source:
            shutil.copyfileobj(source, output)
        output.write(struct.pack("<QQ", offset, outer.stat().st_size) + bytes.fromhex(digest(outer)) + b"XRSETUP1")
    assert installer.stat().st_size < 2 * 1024**3, "Single-asset GitHub size limit exceeded"
    source_zip = release / "xiaomi-revamp-source.zip"
    with zipfile.ZipFile(source_zip, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name in ("source", "tools", "checks"):
            for path in public_source(ROOT / name):
                archive.write(path, path.relative_to(ROOT).as_posix())
        archive.write(ROOT / "README.md", "README.md")
        archive.write(ROOT / "RELEASE_NOTES.md", "RELEASE_NOTES.md")
        archive.write(ROOT / "source/pc-manager/LICENSE", "LICENSE")
    shutil.copy2(outer, release / "Xiaomi-Revamp-Installer.zip")
    names = [installer.name, "Xiaomi-Revamp-Installer.zip", "development-toolchain.zip", "pc-manager-development.zip", "file-search-development.zip", "screen-translator-development.zip", source_zip.name]
    assets = [{"name": name, "bytes": (release / name).stat().st_size, "sha256": digest(release / name)} for name in names]
    (release / "SHA256SUMS.txt").write_text("".join(item["sha256"] + "  " + item["name"] + "\n" for item in assets), encoding="utf-8")
    assets.append({"name": "SHA256SUMS.txt", "bytes": (release / "SHA256SUMS.txt").stat().st_size, "sha256": digest(release / "SHA256SUMS.txt")})
    (release / "public-assets.json").write_text(json.dumps({"version": "0.2.0", "assets": assets}, indent=2), encoding="utf-8")
    (release / "build-config.json").write_text(json.dumps({"previous": str(previous), "changes": str(args.changes.resolve()) if args.changes else None, "unique_files": len(written), "installed_paths_with_toolchain": paths, "pc_manager_unchanged": "pc-manager" not in replacements, "installer_bytes": installer.stat().st_size}, indent=2), encoding="utf-8")
    print("PASS: complete installer including development tools, " + str(round(installer.stat().st_size / 1024**2, 1)) + " MiB", flush=True)


if __name__ == "__main__":
    main()
