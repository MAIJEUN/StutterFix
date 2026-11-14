"""Read installed compiled shader states only. Does not modify installed assets or run Unity."""
from pathlib import Path
import hashlib
import json
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'out/asset-reader-deps'))
import UnityPy
from UnityPy.enums import PassType

game = Path('D:/SteamLibrary/steamapps/common/A Dance of Fire and Ice/A Dance of Fire and Ice_Data')
catalog = {}
evidence = []
paths = sorted(game.glob('sharedassets*.assets')) + [game / 'resources.assets']
for path in paths:
    env = UnityPy.load(str(path))
    used = False
    for obj in env.objects:
        if obj.type.name != 'Shader':
            continue
        tree = obj.read_typetree()
        parsed = tree.get('m_ParsedForm', {})
        name = parsed.get('m_Name') or tree.get('m_Name')
        if not name:
            continue
        variants = []
        for sub in parsed.get('m_SubShaders', []):
            passes = sub.get('m_Passes', [])
            grabs = sum(p.get('m_Type') == int(PassType.kPassTypeGrab) for p in passes)
            blend = []
            for p in passes:
                if p.get('m_Type') != int(PassType.kPassTypeNormal):
                    continue
                state = p.get('m_State', {}).get('rtBlend0', {})
                def factor(key):
                    f = state.get(key, {})
                    n = f.get('name', '')
                    return 'property:'+n if n and n != '<noninit>' else str(f.get('val', 'unknown'))
                blend.append('/'.join(factor(k) for k in ('srcBlend', 'destBlend', 'srcBlendAlpha', 'destBlendAlpha')))
            variants.append((grabs, ';'.join(blend)))
        if variants and len(set(variants)) == 1:
            row = variants[0]
            if name in catalog and catalog[name] != row:
                raise RuntimeError('ambiguous shader name: '+name)
            catalog[name] = row
            evidence.append({'shader': name, 'asset': path.name, 'path_id': obj.path_id,
                             'grab_passes': row[0], 'blend_src_dst_srcA_dstA': row[1]})
            used = True
    if used:
        print('read-only shader metadata:', path.name)
    del env
out = root / 'tools/fillcensus'
out.mkdir(exist_ok=True)
(out / 'shader-metadata.tsv').write_text(''.join(f'{name}\t{grabs}\t{blend}\n' for name, (grabs, blend) in sorted(catalog.items())), encoding='utf-8')
result = {'UnityPy': UnityPy.__version__, 'read_only': True, 'shader_count': len(catalog), 'entries': evidence,
          'asset_sha256': {p.name: hashlib.file_digest(p.open('rb'), 'sha256').hexdigest() for p in paths}}
(root / 'out/fillcensus/shader-metadata.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print('catalogued shaders:', len(catalog), 'with GrabPass:', sum(v[0] > 0 for v in catalog.values()))
