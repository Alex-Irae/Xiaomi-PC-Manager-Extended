"""Copy the shared interface files into each app's frontend folder, or verify the copies.

Dependencies: Python stdlib. Outputs: revamp.css and revamp.js inside each frontend folder.
Command: python -B tools/sync_ui.py [--check] [EXTRA_FRONTEND_FOLDER ...]
Example for FileSync, which lives in its own repository: python -B tools/sync_ui.py ..\\FileSync\\frontend
"""
import argparse
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHARED = ROOT / "source/shared/ui"
FRONTENDS = [ROOT / "source/pc-manager/www", ROOT / "source/file-search/frontend", ROOT / "source/screen-translator/frontend"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Only verify that every copy equals the shared file")
    parser.add_argument("extra", nargs="*", type=Path, help="More frontend folders, such as FileSync's")
    args = parser.parse_args()
    stale = []
    for folder in FRONTENDS + [path.resolve() for path in args.extra]:
        for source in sorted(SHARED.iterdir()):
            target = folder / source.name
            if target.exists() and target.read_bytes() == source.read_bytes():
                continue
            if args.check:
                stale.append(str(target))
            else:
                shutil.copyfile(source, target)
                print("copied " + str(target))
    if stale:
        sys.exit("Shared interface copies differ, run tools/sync_ui.py: " + ", ".join(stale))
    print("PASS: shared interface files are identical in every frontend")


if __name__ == "__main__":
    main()
