"""Check actual packaged DLLs for edition isolation and unchanged native export ABI."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'out/engine-hot/deps'))
import pefile

def exports(data):
    return {e.name.decode(): e.ordinal for e in pefile.PE(data=data).DIRECTORY_ENTRY_EXPORT.symbols if e.name}

native = (root / 'native/sfnative.dll').read_bytes()
baseline = exports(subprocess.check_output(['git', 'show', 'HEAD:native/sfnative.dll'], cwd=root))
assert exports(native) == baseline, 'native named exports or ordinals changed'
result = {'HEAD': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
          'native_exports_and_ordinals_preserved': len(baseline), 'editions': {}}
for name, relative in [('AUTOTEST', 'bin/PlayerAuto/StutterFix.dll'), ('Player', 'bin/Player/StutterFix.dll'), ('DEV', 'bin/Release/StutterFix.dll')]:
    data = (root / relative).read_bytes()
    assert native in data, 'stale embedded native: '+name
    expected = name != 'Player'
    for typename in (b'FillCensus\x00', b'FillGeometry\x00'):
        assert (typename in data) == expected, 'Fill diagnostic edition leak/missing: '+name
    assert (b'StutterFix.fillshaders' in data) == expected, 'shader catalogue edition leak/missing: '+name
    assert (b'PresentPath\x00' in data) == (name == 'AUTOTEST')
    result['editions'][name] = {'FillCensus': expected, 'native_embedded': True,
                                'sha256': hashlib.sha256(data).hexdigest()}
result['native_sha256'] = hashlib.sha256(native).hexdigest()
path = root / 'out/fillcensus/artifact-check.json'
path.write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result, indent=2))
