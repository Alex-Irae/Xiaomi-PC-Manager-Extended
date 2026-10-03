"""Check recursive exclusions, source preservation and actual Windows shortcut handover.

Dependencies: stdlib, suite's copied search packages, .NET 8 SDK/Desktop. Outputs: new numbered results.
Command: python checks/check_suite.py --sdk PATH --dotnet PATH
No desktop capture, firmware operation, model inference, live installation or original application change occurs.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', type=Path, required=True, help='Existing .NET 8 SDK executable')
    parser.add_argument('--dotnet', type=Path, required=True, help='Suite private .NET 8 runtime executable')
    args = parser.parse_args()
    results = ROOT / 'results'
    results.mkdir(exist_ok=True)
    highest = max([int(path.name.split('_')[0]) for path in results.iterdir() if path.is_dir() and path.name.split('_')[0].isdigit()] + [0])
    run = results / f'{highest+1:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_seed0'
    run.mkdir()
    (run / 'config.json').write_text(json.dumps({'seed': 0, 'mode': 'suite-integration-check', 'sdk': str(args.sdk.resolve()), 'dotnet': str(args.dotnet.resolve()), 'windowsKeys': ['Ctrl+Alt+Shift+F19', 'Ctrl+Alt+Shift+F20', 'Ctrl+Alt+Shift+F21', 'Ctrl+Alt+Shift+F22'], 'agentTimeoutSeconds': 12, 'modelsInitialized': False, 'desktopCaptured': False}, indent=2), encoding='utf-8')
    sys.path.insert(0, str(ROOT / 'source' / 'file-search'))
    sys.path.insert(0, str(ROOT / 'install' / 'AI Center' / 'runtime' / 'python' / 'Lib' / 'site-packages'))
    from xiaomi_search.exclusions import is_within
    from xiaomi_search.indexer import Indexer
    from xiaomi_search.store import Store
    paths = [
        (r'C:\Demo\Excluded', r'C:\Demo\Excluded', True),
        (r'C:\Demo\Excluded\child\grandchild\needle.txt', r'C:\Demo\Excluded', True),
        ('c:/demo/excluded/child/needle.txt', 'C:/DEMO/Excluded/', True),
        (r'C:\Demo\ExcludedOther\needle.txt', r'C:\Demo\Excluded', False),
        (r'\\server\share\excluded\child\needle.txt', r'\\SERVER\SHARE\excluded', True),
        (r'C:\Other\needle.txt', 'C:/', True),
        (r'C:\Demo\Ünicode\child\needle.txt', r'C:\Demo\ünicode', True),
        (r'C:\Demo\Excluded\..\Allowed\needle.txt', r'C:\Demo\Excluded', False),
    ]
    for candidate, ancestor, expected in paths:
        assert is_within(candidate, ancestor) == expected, (candidate, ancestor)
    fixture = run / 'files'
    excluded = fixture / 'Excluded'
    inside = excluded / 'child' / 'grandchild' / 'needle.txt'
    sibling = fixture / 'ExcludedOther' / 'needle.txt'
    for file in [inside, sibling]:
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text('needle', encoding='utf-8')
    store = Store(run / 'index.sqlite')
    for path in [excluded, excluded / 'child', inside, sibling]:
        store.discover(path, path.stat())
    config = json.loads((ROOT / 'source' / 'file-search' / 'config.example.json').read_text(encoding='utf-8'))
    config.update(roots=[str(fixture)], excluded_folders=[str(excluded).swapcase().replace('\\', '/')], excluded_names=[], excluded_extensions=[])
    indexer = Indexer(config, store, None, run / 'data')
    assert not indexer.allowed(excluded) and not indexer.allowed(inside), 'Indexer admitted excluded folder or descendant'
    assert indexer.allowed(sibling), 'Sibling-prefix folder was accidentally excluded'
    store.roots = [str(fixture)]; store.excluded_folders = config['excluded_folders']
    result = store.search('needle', names=True, contents=False)
    assert [row['file_path'] for row in result['results']] == [str(sibling)], result
    where, parameters = store._where({}, None)
    with store.connect() as connection:
        assert [row['path'] for row in connection.execute('SELECT f.path FROM files f WHERE ' + where, parameters)] == [str(sibling)]
    print('PASS: recursive exclusions reject the folder, every descendant and stale indexed rows; sibling folders remain searchable', flush=True)
    provenance = json.loads((ROOT / 'source-provenance.json').read_text(encoding='utf-8-sig')) if (ROOT / 'source-provenance.json').exists() else []
    for entry in provenance:
        with Path(entry['origin']).open('rb') as stream:
            actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        assert actual.upper() == entry['sha256'], entry['origin']
    print(f'PASS: {len(provenance)} original source files are unchanged', flush=True)
    os.environ.update(DOTNET_CLI_HOME=str(ROOT / 'toolchain' / 'cli'), NUGET_PACKAGES=str(ROOT / 'toolchain' / 'packages'), DOTNET_CLI_TELEMETRY_OPTOUT='1')
    subprocess.run([str(args.sdk.resolve()), 'build', str(ROOT / 'checks' / 'ShortcutHarness.csproj'), '-c', 'Release', '--configfile', str(ROOT / 'source' / 'NuGet.Config'), '-p:NuGetAudit=false', '-p:RestoreSources=', '-o', str(run / 'agents')], check=True)
    subprocess.run([str(args.dotnet.resolve()), str(run / 'agents' / 'ShortcutHarness.dll'), 'verify'], check=True)
    summary = {'passed': True, 'recursiveExclusions': len(paths), 'indexerAndRetrieval': True, 'unchangedOriginalSourceFiles': len(provenance), 'shortcutEvidence': 'agents/summary.json'}
    (run / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print('PASS: suite integration checks at', run, flush=True)


if __name__ == '__main__':
    main()
