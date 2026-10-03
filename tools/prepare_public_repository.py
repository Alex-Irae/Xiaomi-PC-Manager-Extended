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
    if not (repository / '.git').is_dir():
        raise ValueError('Use the existing public Git checkout')
    if subprocess.check_output(['git','-C',str(repository),'status','--porcelain']).strip():
        raise ValueError('Preserve existing repository changes before public staging')
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
    shutil.copy2(ROOT / 'RELEASE_NOTES.md', repository / 'RELEASE_NOTES.md')
    forbidden = [str(path.relative_to(repository)) for path in repository.rglob('*') if path.is_file() and '.git' not in path.parts
                 and (path.name in {'history.dpapi', 'config.json', 'index.sqlite3.dpapi'} or path.stat().st_size >= 100_000_000)]
    if forbidden:
        raise ValueError(f'Public staging contains private or oversized files: {forbidden}')
    print('PASS: public source staged, private profiles/builds/models excluded', flush=True)


if __name__ == '__main__':
    main()
