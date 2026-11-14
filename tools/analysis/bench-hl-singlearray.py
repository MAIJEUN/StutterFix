"""bench_v14 CFG2 additions; default prints plans, --run explicitly launches benchmarks."""
import argparse
import importlib.util
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / 'out/hl-singlearray-lab/bundle/stutterfix_singlearray_research'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tag', nargs='?', default='gdHlFast')
    parser.add_argument('maps', nargs='?', default='hl50')
    parser.add_argument('configs', nargs='?', default='prodGd,prodGdFast,prodGdFast,prodGd')
    parser.add_argument('--run', action='store_true')
    parser.add_argument('--probe', action='store_true')
    args = parser.parse_args()
    os.environ['BENCH_WT'] = ROOT.name
    bench_path = ROOT.parent / 'bench_v14.py'
    spec = importlib.util.spec_from_file_location('bench_v14', bench_path)
    bench = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bench)
    assert bench.WT.resolve() == ROOT.resolve()
    base, commands = bench.CFG2['prodGd']
    for key, optimize, lean, fast in [('prodGdRef', False, False, False),
                                      ('prodGdSingle', True, True, False),
                                      ('prodGdFast', True, True, True)]:
        # Load before product preparation; enabling GpuDeco then follows the
        # same product path as prodGd. Settings start at 0 for this boundary.
        bench.CFG2[key] = (dict(base, GpuDeco=0), list(commands) + [
            'renderlod off', 'sfflat off', 'sfnodiscard off', 'sfdebug 0',
            'gpudeco drawcollapse singlearray '+str(BUNDLE),
            'gpudeco drawcollapse singlearray optimize '+('on' if optimize else 'off'),
            'gpudeco drawcollapse singlearray lean '+('on' if lean else 'off'),
            'gpudeco drawcollapse singlearray fast '+('on' if fast else 'off'),
            'set GpuDeco 1', 'wait 2'])
        bench.CFG_MID[key] = ['gpudeco drawcollapse singlearray state']
        bench.CFG_POST[key] = ['gpudeco drawcollapse singlearray state', 'gpudeco cost']
    if not args.run:
        print(json.dumps({key: bench.CFG2[key] for key in dict.fromkeys(args.configs.split(','))}, indent=2))
        print('Plan only. --run delegates to bench_v14/run_locked and launches the game.')
        return
    assert BUNDLE.is_file(), 'build the isolated fixture bundle first'
    manifest = json.loads((ROOT / 'out/hl-singlearray-lab/experiment.json').read_text())
    import hashlib
    shader = ROOT / 'out/hl-singlearray-lab/Assets/Shaders/SingleArray.shader'
    assert hashlib.sha256(shader.read_bytes()).hexdigest() == manifest['candidate_sha256']
    assert BUNDLE.stat().st_mtime >= shader.stat().st_mtime, 'stale shader bundle'
    admit = bench.scriptpath_admitted
    def admitted(key, text):
        if not admit(key, text):
            return False
        if key not in ('prodGdRef', 'prodGdSingle', 'prodGdFast'):
            return True
        states = [line for line in text.splitlines() if 'SINGLE_ARRAY_STATE' in line]
        if not states or any('actual_material_mismatches=0' not in line or 'reference=False' not in line or 'control=False' not in line for line in states):
            return False
        if key == 'prodGdFast':
            return all('fast_pixel=True' in line and
                       (m := re.search(r'actual_fixed_direct_spans=(\d+)', line)) and int(m[1]) > 0 for line in states)
        return True
    bench.scriptpath_admitted = admitted
    sys.argv = [str(bench_path), args.tag, args.maps, args.configs] + (['--probe'] if args.probe else [])
    bench.main()


if __name__ == '__main__':
    main()
