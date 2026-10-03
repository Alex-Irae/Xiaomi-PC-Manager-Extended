"""Stage public Xiaomi Revamp source while preserving the prior PC Manager source in history and an archive.

Dependencies: Python stdlib and existing Git checkout. Outputs: public source/docs only, no user data or build files.
Command: python tools/prepare_public_repository.py --repository public-repository
Run only after the user requests GitHub publishing.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from build_suite import public_source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', type=Path, required=True, help='Existing clean copied PC Manager checkout')
    args = parser.parse_args()
    repository = args.repository.resolve()
    if not repository.is_relative_to(ROOT) or not (repository / '.git').is_dir():
        raise ValueError('Use the copied public checkout inside this workspace')
    tracked = subprocess.check_output(['git', '-C', str(repository), 'ls-files', '-z']).decode().split('\0')
    for name in filter(None, tracked):
        path = repository / name
        if name.startswith(('source/', 'tools/', 'checks/', 'archive/')) or name in {'README.md', 'LICENSE', '.gitignore', 'RELEASE_NOTES.md', 'COMPONENTS.md'} or not path.exists():
            continue
        target = repository / 'archive' / 'pc-manager-0.1.6' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists(): path.replace(target)
    for directory in ['source', 'tools', 'checks']:
        for path in public_source(ROOT / directory):
            target = repository / directory / path.relative_to(ROOT / directory)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    shutil.copy2(ROOT / 'README.md', repository / 'README.md')
    shutil.copy2(ROOT / 'source' / 'pc-manager' / 'LICENSE', repository / 'LICENSE')
    (repository / '.gitignore').write_text('build/\ninstall/\npackages/\nresults/\ntoolchain/\npublic-repository/\n**/bin/\n**/obj/\n**/__pycache__/\n**/.venv/\n**/data/\n**/model_cache/\n**/*.pyc\n**/config.json\n**/included-folders.txt\n**/excluded-folders.txt\n**/Archives/\n', encoding='utf-8')
    (repository / 'COMPONENTS.md').write_text('''# Component branches

The integration parent is [`manager/main`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/main).
The child branches [`manager/screen-translator`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/screen-translator)
and [`manager/ai-center`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/ai-center)
start from that parent's v0.2.0 commit. They retain the complete source tree so shared integration stays buildable.
Each child identifies its component in `COMPONENT.md`. The existing default `main` also tracks the parent integration.

| App | Source | Role |
| --- | --- | --- |
| PC Manager | `source/pc-manager` | Mandatory combined-installer hub; shortcut owner while running |
| Screen Translator | `source/screen-translator` | Optional child; independent EXE, data and uninstaller |
| AI Center | `source/file-search` | Optional child; independent EXE, data and uninstaller |

Git branch refs are flat, so names, shared ancestry and these links express parent/child relationships.
Original PC Manager 0.1.6 source remains under `archive/pc-manager-0.1.6` and in Git history.
Personal data, local preservation archives, packaged binaries and models are excluded from repository commits.
''', encoding='utf-8')
    (repository / 'RELEASE_NOTES.md').write_text('''# PC Manager 0.2.0

PC Manager now connects independent Screen Translator and AI Center applications with synchronized shortcuts,
automatic shortcut swaps, a physical key recorder, direct translation from the quick panel and optional shared appearance.
Translator inference workers unload after ten seconds idle. File-search exclusions cover each folder and all descendants.

The offline installer supports optional components, chosen application/data paths, a separate AI Center data path,
current-user/all-users registration and editable Development copies. Each app has an independent uninstaller with
settings/data and development-removal choices. Development ZIPs and the copied offline toolchain are release assets.

Validation passed native builds, real Windows shortcut ownership/swaps/recorder events/fallback, recursive scan and SQL
exclusions, isolated install/upgrade/uninstall, Program Files data permissions, development preservation and payload hashes.
Installed model and UI checks are recorded locally; no personal reports, indexes or history are published.

Known limits: cached translator reload measured about 16 seconds; full-drive search remains Paused near the approximately
2 GiB protected-snapshot capacity. Reboot/sign-in, general translation quality and physical Copilot delivery are not fully qualified.
''', encoding='utf-8')
    forbidden = [str(path.relative_to(repository)) for path in repository.rglob('*') if path.is_file() and '.git' not in path.parts
                 and (path.name in {'history.dpapi', 'config.json', 'index.sqlite3.dpapi'} or path.stat().st_size >= 100_000_000)]
    if forbidden:
        raise ValueError(f'Public staging contains private or oversized files: {forbidden}')
    print('PASS: public source staged, private profiles/builds/models excluded', flush=True)


if __name__ == '__main__':
    main()
