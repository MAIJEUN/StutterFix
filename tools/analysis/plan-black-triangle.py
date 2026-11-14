"""Prepare local charts/plans only. Never launch, install, or call sfmeasure."""
import copy
import hashlib
import json
from pathlib import Path
import shutil

root = Path(__file__).resolve().parents[2]
original = Path('D:/얼불춤 맵 파일/Artifact - Mare Tranquillitatis (4)/mare tranquillitatis.adofai')
out = root / 'out/tester-black-triangle'
chart_dir = out / 'large-background-fixture'
chart_dir.mkdir(parents=True, exist_ok=True)
data = json.loads(original.read_text(encoding='utf-8-sig'))
before = hashlib.sha256(original.read_bytes()).hexdigest()
for asset in original.parent.iterdir():
    if asset.suffix.lower() in ('.png', '.ogg'):
        shutil.copy2(asset, chart_dir / asset.name)
# BG.png in the original is a CustomBackground action, not an image decoration.
# Add explicit large image decorations in the COPY to exercise RunLod's path.
template = next(d for d in data['decorations'] if d['eventType'] == 'AddDecoration')
added = []
for index, rotation in enumerate((0, 45, -45)):
    deco = copy.deepcopy(template)
    deco.update(floor=0, decorationImage='BG.png', tag=f'sf_triangle_bg_{index}',
                position=[0, 0], rotation=rotation, scale=[10000, 10000],
                opacity=25, color='FFFFFF', depth=10000 + index,
                parallax=[0, 0], parallaxOffset=[0, 0],
                lockRotation=False, lockScale=False, tile=[1, 1],
                maskingType='None', hitbox='None', blendMode='None')
    data['decorations'].append(deco)
    added.append(deco)
fixture = chart_dir / 'large-background.adofai'
fixture.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
assert hashlib.sha256(original.read_bytes()).hexdigest() == before


def write(name, steps, settings):
    plan = dict(steps=steps, settings=settings, timeout_min=10,
                source_dll='bin/PlayerAuto/StutterFix.dll')
    (out / (name + '.json')).write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding='utf-8')


def capture(label):
    return [f'framecmp grab bt_{label}', f'framecmp save bt_{label}']


common = dict(GpuDeco=0, RunLod=False, GpuDecoDetach=False, DecoDetachCpu=False,
              SkipInvisible=True, SkipSame=True, DefaultsGen=5, NoFx=False,
              EffectsOff='', FiltersOff='', LowRenderScale=100, FrameGenOutside=0)
# Diagnostic pause uses Config.RunLod=false intentionally, so SongEnded cannot
# cancel a manual on/off comparison of the same frozen frame.
steps = [f'open {fixture.as_posix()}', 'auto on', 'play', 'waitsongpos 5 120',
         'pause', 'wait 2', 'framecmp filters on', 'framecmp mask none', 'runlod off']
steps += capture('base') + capture('noise')
steps += ['runlod force on', 'runlod on', 'wait 2', 'runlod state'] + capture('lod')
steps += ['runlod off', 'wait 2', 'runlod state'] + capture('restore')
steps += ['framecmp assertzero bt_base bt_noise png', 'framecmp diff bt_base bt_lod 0 png',
          'framecmp assertzero bt_base bt_restore png', 'framecmp clear']
steps += ['resume', 'stop', 'wait 2', 'runlod state', 'shot bt_editor_manual_off', 'wait 1', 'quit']
write('runlod-frozen', steps, common)

# Real configured lifecycle: force active solely to make rendering observable.
steps = [f'open {fixture.as_posix()}', 'auto on', 'runlod force on', 'play',
         'waitsongpos 5 120', 'runlod state', 'shot bt_configured_play', 'wait 1',
         'stop', 'wait 2', 'runlod state', 'shot bt_configured_editor', 'wait 1',
         'runlod force off', 'play', 'waitsongpos 5 120', 'runlod state',
         'stop', 'wait 2', 'runlod state', 'quit']
write('runlod-lifecycle', steps, dict(common, RunLod=True))

# Fresh process for every row; compare both editor transitions and paused image.
rows = {
    'control': {}, 'gpu': {'GpuDeco': 1},
    'detach': {'GpuDecoDetach': True, 'DecoDetachCpu': True},
    'skip-invisible-off': {'SkipInvisible': False},
    'skip-same-off': {'SkipSame': False},
}
for label, settings in rows.items():
    steps = [f'open {original.as_posix()}', 'auto on', 'seek 70 8',
             'waitsongpos 70 120', 'pause', 'wait 2', 'framecmp filters on',
             'framecmp mask none', 'decodetach state', 'runlod state']
    steps += capture(label) + capture(label + '_noise')
    steps += [f'framecmp assertzero bt_{label} bt_{label}_noise png', 'framecmp clear']
    steps += ['resume', 'stop', 'wait 2', 'decodetach state', 'runlod state',
              f'shot bt_editor_{label}', 'wait 1', 'quit']
    write('sample-' + label, steps, dict(common, **settings))

windflower = Path('D:/얼불춤 맵 파일/Windflower/Windflower.adofai')
assert windflower.is_file()
for enabled in (False, True):
    label = 'detach-on' if enabled else 'detach-off'
    steps = [f'open {windflower.as_posix()}', 'auto on', 'seek 258 8',
             'waitsongpos 258 120', 'decodetach state',
             'pause', 'wait 2', 'decodetach state', 'framecmp filters on', 'framecmp mask none']
    steps += capture('wf_' + label) + capture('wf_' + label + '_noise')
    steps += [f'framecmp assertzero bt_wf_{label} bt_wf_{label}_noise png', 'framecmp clear']
    steps += ['resume', 'stop', 'wait 2', 'decodetach state',
              f'shot bt_editor_wf_{label}', 'wait 1', 'quit']
    write('windflower-' + label, steps, dict(common, GpuDecoDetach=enabled, DecoDetachCpu=enabled))
summary = dict(original=str(original), original_sha256=before,
               original_image_decorations=len(data['decorations']) - len(added),
               original_BG_is_custom_background=True, fixture=str(fixture),
               added_rotations=[d['rotation'] for d in added],
               generated_plans=sorted(p.name for p in out.glob('*.json') if p.name.startswith(('runlod-', 'sample-', 'windflower-'))),
               execution='NOT_RUN', note='Require nonzero RunLod runs/members and visible GPU ownership/detach counts; otherwise intervention not exercised.')
(out / 'plans-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(summary, ensure_ascii=False, indent=2))
