"""Audit real Unity bundle DXBC, with keyword identities. No game/device launch."""
import ctypes as C
import hashlib
import json
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'out/asset-reader-deps'))
import UnityPy
import lz4.block
from UnityPy.export.ShaderConverter import ShaderSubProgram
from UnityPy.streams import EndianBinaryReader

OUT = ROOT / 'out/hl-regress-20261114/dxbc'
OUT.mkdir(parents=True, exist_ok=True)
compiler = C.WinDLL('d3dcompiler_47.dll')


def disassemble(code):
    blob = C.c_void_p()
    hr = compiler.D3DDisassemble(code, C.c_size_t(len(code)), 0, None, C.byref(blob))
    if hr:
        raise RuntimeError(f'D3DDisassemble {hr}')
    table = C.cast(C.cast(blob, C.POINTER(C.c_void_p))[0], C.POINTER(C.c_void_p))
    try:
        ptr = C.WINFUNCTYPE(C.c_void_p, C.c_void_p)(table[3])(blob)
        size = C.WINFUNCTYPE(C.c_size_t, C.c_void_p)(table[4])(blob)
        return C.string_at(ptr, size).decode('latin1').rstrip('\0')
    finally:
        C.WINFUNCTYPE(C.c_ulong, C.c_void_p)(table[2])(blob)


def stats(text):
    text = text.replace('\0', '')
    lines = [s.strip() for s in text.splitlines() if s.strip() and not s.lstrip().startswith('//')]
    profile = next(s for s in lines if re.match(r'(ps|vs)_\d', s))
    instructions = [s for s in lines if not s.startswith(('dcl_', 'ps_', 'vs_'))]
    inputs = [s for s in lines if s.startswith('dcl_input_ps')]
    return dict(profile=profile, instructions=len(instructions),
                sample_sites=sum(s.startswith(('sample', 'gather')) for s in instructions),
                raw_load_sites=sum(s.startswith(('ld_raw', 'ld_structured')) for s in instructions),
                ps_input_registers=len(inputs),
                ps_input_components=sum(len(m.group(1)) for s in inputs if (m := re.search(r'v\d+\.([xyzw]+)', s))),
                inputs=inputs)


rows = []
for path in sorted((ROOT / 'out/original-tile-shader').glob('program-*.txt')):
    rows.append(dict(source='Sprites/Tile', keywords=[], file=str(path.relative_to(ROOT)), **stats(path.read_text(encoding='utf-8'))))

bundles = [ROOT / 'tools/gpudeco-lab/evidence/drawcollapse5/bundle/stutterfix_gpudeco',
           ROOT / 'out/hl-singlearray-lab/bundle/stutterfix_singlearray_research']
for bundle in bundles:
    env = UnityPy.load(str(bundle))
    for obj in env.objects:
        if obj.type.name != 'Shader':
            continue
        tree = obj.read_typetree()
        name = tree['m_ParsedForm']['m_Name']
        if name not in ('StutterFix/GpuDecoBatch', 'StutterFix/ResearchSingleArray'):
            continue
        compressed = bytes(tree['compressedBlob'])
        for i, platform in enumerate(tree['platforms']):
            if platform != 4:
                continue
            offsets, lengths, sizes = (tree[k][i] for k in ('offsets', 'compressedLengths', 'decompressedLengths'))
            if not isinstance(offsets, list):
                offsets, lengths, sizes = [offsets], [lengths], [sizes]
            assert len(offsets) == 1, 'multi-segment keyword mapping needs explicit support'
            raw = lz4.block.decompress(compressed[offsets[0]:offsets[0]+lengths[0]], uncompressed_size=sizes[0])
            # Unity 6 interleaves parameter blobs with code. Only indices in
            # player stage metadata are programs; parsing every blob as a
            # ShaderSubProgram misreads parameter bytes as keyword strings.
            keyword_names = tree['m_ParsedForm']['m_KeywordNames']
            metadata = []
            for shader_pass in tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes']:
                for stage in ('progVertex', 'progFragment'):
                    for group in shader_pass[stage]['m_PlayerSubPrograms']:
                        metadata.extend(group)
            for entry in metadata:
                index = entry['m_BlobIndex']
                reader = EndianBinaryReader(raw, endian='<')
                reader.Position = struct.unpack_from('<I', raw, 4+index*12)[0]
                sub = ShaderSubProgram(reader)
                code = bytes(sub.m_ProgramCode)
                start = code.find(b'DXBC')
                if start < 0:
                    continue
                size = struct.unpack_from('<I', code, start+24)[0]
                code = code[start:start+size]
                assembly = disassemble(code)
                filename = f'{name.split("/")[-1]}-{index}.txt'
                (OUT / filename).write_text(assembly, encoding='utf-8')
                rows.append(dict(source=name, keywords=sorted(keyword_names[k] for k in entry['m_KeywordIndices']),
                                 file=str((OUT/filename).relative_to(ROOT)), sha256=hashlib.sha256(code).hexdigest(), **stats(assembly)))
    print(bundle.name, 'read', flush=True)

(OUT / 'stats.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
selected = []
for row in rows:
    keys = set(row['keywords'])
    if row['source'] == 'Sprites/Tile' or ('SF_SIMPLE' not in keys and ('SF_SINGLE_ARRAY' not in keys) and ('SF_SINGLE_2D' not in keys or 'SF_LEAN_VERTEX' in keys)):
        selected.append(row)
print(json.dumps(selected, indent=2))
