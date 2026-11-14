"""Prepare an isolated shader experiment; never overwrite the production bundle."""
import hashlib
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
fast = '--fast-pixel' in sys.argv
LAB = ROOT / ('out/hl-singlearray-lab' if fast else 'out/singlearray-lab')
source_path = ROOT / 'tools/gpudeco-lab/GpuDecoBatch.shader'
source = source_path.read_text(encoding='utf-8')
start = source.index('#ifdef NO_DIRECT_TEXTURES\n')
assert source.count('#ifdef NO_DIRECT_TEXTURES\n') == 1
specialized = source[:start] + '''#ifdef SF_SINGLE_ARRAY // Proven single-array span only; binding selection eliminated.
                if(usePoint) c=_SfSingleArray.SampleLevel(sampler_point_clamp,float3(uv,asfloat(v.info.y)),0);
                else c=_SfSingleArray.SampleLevel(sampler_linear_clamp,float3(uv,asfloat(v.info.y)),0);
#elif defined(SF_SINGLE_2D)
                if(usePoint) c=_SfSingle2D.SampleLevel(sampler_point_clamp,uvIn,0);
                else c=_SfSingle2D.SampleLevel(sampler_linear_clamp,uvIn,0);
#elif defined(NO_DIRECT_TEXTURES)
''' + source[start+len('#ifdef NO_DIRECT_TEXTURES\n'):]
specialized = specialized.replace('Shader "StutterFix/GpuDecoBatch"', 'Shader "StutterFix/ResearchSingleArray"', 1)
specialized = specialized.replace('#pragma multi_compile_local _ SF_SIMPLE','#pragma multi_compile_local _ SF_SIMPLE\n            #pragma multi_compile_local _ SF_SINGLE_ARRAY SF_SINGLE_2D',1)
specialized = specialized.replace('            Texture2DArray _SfDecoTex0;', '            Texture2DArray _SfSingleArray;\n            Texture2D _SfSingle2D;\n            Texture2DArray _SfDecoTex0;', 1)
specialized = specialized.replace('#pragma multi_compile_local _ SF_SINGLE_ARRAY SF_SINGLE_2D', '#pragma multi_compile_local _ SF_SINGLE_ARRAY SF_SINGLE_2D\n            #pragma multi_compile_local _ SF_LEAN_VERTEX', 1)
# The VS and PS compile separately: the VS otherwise retains DIRECT_FEW's
# dynamic constant-buffer lookup even when the PS never consumes binding bits.
specialized = specialized.replace('#if defined(DIRECT_FEW) && !defined(NO_DIRECT_TEXTURES)', '#if defined(DIRECT_FEW) && !defined(NO_DIRECT_TEXTURES) && !(defined(SF_LEAN_VERTEX) && (defined(SF_SINGLE_ARRAY) || defined(SF_SINGLE_2D)))', 1)
old_region='o.color=asfloat(_CollapseVerts.Load4(a+12));o.uv=uvi.xy;o.region=asfloat(_CollapseVerts.Load4(a+44));'
assert specialized.count(old_region)==1
specialized=specialized.replace(old_region, '''o.color=asfloat(_CollapseVerts.Load4(a+12));o.uv=uvi.xy;
#if defined(SF_LEAN_VERTEX) && defined(SF_SINGLE_2D)
                o.region=0; // Direct textures sample uvIn; region is not consumed.
#else
                o.region=asfloat(_CollapseVerts.Load4(a+44));
#endif''')
if fast:
    # Explicit normal-render candidate. Keep the generic/LOD/diagnostic path
    # intact unless SF_FAST_PIXEL is selected, so reference and controls remain.
    specialized = specialized.replace('#pragma multi_compile_local _ SF_LEAN_VERTEX',
        '#pragma multi_compile_local _ SF_LEAN_VERTEX\n            #pragma multi_compile_local _ SF_FAST_PIXEL\n            #pragma multi_compile_local _ SF_TILE_LINEAR SF_TILE_POINT', 1)
    # Conservative static direct spans: no image actor, repeat or additive.
    begin = specialized.index('                if(_LodMode>0.5 && _LodMode<1.5)return _LodTex.')
    specialized = specialized[:begin] + '''#if defined(SF_TILE_LINEAR) || defined(SF_TILE_POINT)
#ifdef SF_TILE_POINT
                float4 tile=_SfSingle2D.SampleLevel(sampler_point_clamp,v.uv,0);
#else
                float4 tile=_SfSingle2D.SampleLevel(sampler_linear_clamp,v.uv,0);
#endif
                if(tile.a<=0.0)discard;
                tile*=v.color;tile.rgb*=tile.a;return tile;
#else
''' + specialized[begin:]
    end = specialized.index('                c*=v.color;c.rgb*=c.a;return c;')
    end += len('                c*=v.color;c.rgb*=c.a;return c;')
    specialized = specialized[:end] + '\n#endif // fixed direct span' + specialized[end:]
    begin = specialized.index('                if(_LodMode>0.5 && _LodMode<1.5)return _LodTex.')
    end = specialized.index('                // info.x =', begin)
    specialized = specialized[:begin] + '#ifndef SF_FAST_PIXEL\n' + specialized[begin:end] + '#endif\n' + specialized[end:]
    specialized = specialized.replace('                if(_SfNoDiscard<0.5 && c.a<=0.0)discard;',
        '#ifdef SF_FAST_PIXEL\n                if(c.a<=0.0)discard;\n#else\n                if(_SfNoDiscard<0.5 && c.a<=0.0)discard;\n#endif', 1)
    line = '                if(_SfDebugMode>0.5){if(_SfDebugMode<1.5 || (((uint)v.vertex.x|(uint)v.vertex.y)&1u)!=0u)discard;}'
    assert specialized.count(line) == 1
    specialized = specialized.replace(line, '#ifndef SF_FAST_PIXEL\n' + line + '\n#endif', 1)
shaders = LAB / 'Assets/Shaders'
editor = LAB / 'Assets/Editor'
for folder in (shaders, editor, LAB / 'Packages', LAB / 'ProjectSettings'):
    folder.mkdir(parents=True, exist_ok=True)
(shaders / 'Reference.shader').write_text(source, encoding='utf-8')
(shaders / 'SingleArray.shader').write_text(specialized, encoding='utf-8')
shutil.copy2(ROOT / 'tools/analysis/SingleArrayFixture.cs', editor / 'SingleArrayFixture.cs')
if fast:
    fixture = (editor / 'SingleArrayFixture.cs').read_text(encoding='utf-8')
    fixture = fixture.replace('for(int lean=0;lean<2;lean++)', 'for(int fast=0;fast<2;fast++)for(int lean=0;lean<2;lean++)', 1)
    fixture = fixture.replace('                Input(data,flags,layer,padded!=0);',
        '                if(fast!=0)candidate.EnableKeyword("SF_FAST_PIXEL");else candidate.DisableKeyword("SF_FAST_PIXEL");\n                Input(data,flags,layer,padded!=0);', 1)
    fixture = fixture.replace('result.leanConditions==1152', 'result.leanConditions==2304')
    fixture = fixture.replace('result.negativeControls==24&&result.negativeControlsDetected==24',
        'result.negativeControls==48&&result.negativeControlsDetected==48')
    fixture = fixture.replace('public int conditions,', 'public int fixedDirectConditions;\n        public int conditions,', 1)
    fixture = fixture.replace('                candidate.DisableKeyword("SF_SINGLE_ARRAY");',
        '                candidate.DisableKeyword("SF_TILE_LINEAR");candidate.DisableKeyword("SF_TILE_POINT");\n                candidate.DisableKeyword("SF_SINGLE_ARRAY");', 1)
    fixture = fixture.replace('var b=Draw(candidate,mesh,target,read);', '''
                if(fast!=0 && lean!=0 && kind!=0 && wrap==0 && additive==0)
                {candidate.EnableKeyword(point==0?"SF_TILE_LINEAR":"SF_TILE_POINT");result.fixedDirectConditions++;}
                var b=Draw(candidate,mesh,target,read);''', 1)
    fixture = fixture.replace('            Require(result.leanConditions==2304,', '            Require(result.fixedDirectConditions==96,"fixed direct fixture incomplete");\n            Require(result.leanConditions==2304,', 1)
    (editor / 'SingleArrayFixture.cs').write_text(fixture, encoding='utf-8')
for relative in ('Packages/manifest.json', 'ProjectSettings/ProjectVersion.txt'):
    shutil.copy2(ROOT / 'tools/gpudeco-lab/project' / relative, LAB / relative)
license_text=(ROOT/'effects/license.txt').read_text(encoding='utf-8').split('ScreenEffects.shader')[0]
(LAB/'LICENSE').write_text(license_text,encoding='utf-8')
manifest = dict(reference_sha256=hashlib.sha256(source_path.read_bytes()).hexdigest(),
                candidate_sha256=hashlib.sha256((shaders/'SingleArray.shader').read_bytes()).hexdigest(),
                change='normal-render PS removes diagnostic/LOD branches; lean VS and proven single resource' if fast else 'optional lean VS: omit proven-single binding remap and unused direct-texture region load',
                admitted_scope='all future textures proven to use one physical binding; plain non-GrabPass only',
                production_bundle_or_shader_files_modified=False,
                live_admission='AUTOTEST explicit opt-in and all-future-event proof required', performance='UNMEASURED')
(LAB / 'experiment.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(LAB)
