"""Offline pinned-PE and executable synthetic x64 fixture; never loads the game.

Runs only copied replacement bytes in private Python memory. No Unity code is
executed, and the installed files and display settings are never modified.
"""
import ctypes as C
import bisect
import hashlib
import json
import re
import struct
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

WT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(WT / 'out/engine-hot/deps'))
import capstone
import pefile

player = Path(r'D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\UnityPlayer.dll')
src = (WT / 'EnginePatch.cs').read_text(encoding='utf-8')
digest = hashlib.sha256(player.read_bytes()).hexdigest()
assert digest == re.search(r'WaitUnitySha256 = "([a-f0-9]+)"', src)[1]
pe = pefile.PE(str(player))
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True

def array(name):
    body = re.search(r'byte\[\] ' + name + r' = \{(.*?)\};', src, re.S)[1]
    body = re.sub(r'//[^\n]*', '', body)
    return bytes(int(n.strip(), 0) for n in body.split(',') if n.strip())

rva = int(re.search(r'WaitInlineRva = (0x[0-9A-F]+)', src)[1], 16)
before, after = array('WaitInlineBefore'), array('WaitInlineAfter')
assert len(before) == len(after) == 19
assert pe.get_data(rva, len(before)) == before
assert pe.get_data(rva-2, 2) == bytes.fromhex('7413')
guards = re.findall(r'MatchBytes\(IntPtr.Add\(mod, (0x[0-9A-F]+)\), new byte\[\] \{([^}]+)\}\)', src)
for addr, body in guards:
    expected = bytes(int(n.strip(), 0) for n in body.split(','))
    assert pe.get_data(int(addr, 16), len(expected)) == expected, addr
decoded = list(md.disasm(after, rva))
assert sum(i.size for i in decoded) == 19
assert decoded[0].address + decoded[0].size + decoded[0].operands[1].mem.disp == 0x22736c0
assert decoded[1].mnemonic == 'lock inc' and decoded[1].operands[0].mem.disp == 0x5e68
assert all(i.mnemonic == 'nop' for i in decoded[2:])
assert rva + len(after) == 0x5d0a77
# Verify the replacement falls into the same wall-clock path used by v5.
assert pe.get_data(0x5d0aa2, 5) == bytes.fromhex('e839391101')

symbols = sorted((int(p[0], 16), p[2]) for line in
                 (WT.parent/'night-results/unityplayer-syms.tsv').read_text(encoding='utf-8-sig').splitlines()
                 if len(p := line.split('\t', 2)) == 3)
starts = sorted({addr for addr, _ in symbols})
lines = ['UnityPlayer SHA256=' + digest, 'Offline symbol bounds; no game code executed.']
for target in (0x5d0a62, 0x701050, 0xa18270, 0x8e815f):
    start = starts[bisect.bisect_right(starts, target)-1]
    end = starts[bisect.bisect_right(starts, target)]
    names = [name for addr, name in symbols if addr == start]
    lines.append('\n## ' + ' / '.join(names))
    lines.extend(f'{i.address:08x} {i.bytes.hex():24} {i.mnemonic:8} {i.op_str}'
                 for i in md.disasm(pe.get_data(start, end-start), start))
(WT/'out/waitskip-resize-disasm.txt').write_text('\n'.join(lines)+'\n', encoding='utf-8')

k = C.WinDLL('kernel32', use_last_error=True)
k.VirtualAlloc.restype = C.c_void_p
k.VirtualAlloc.argtypes = [C.c_void_p, C.c_size_t, C.c_ulong, C.c_ulong]
k.VirtualFree.argtypes = [C.c_void_p, C.c_size_t, C.c_ulong]
k.FlushInstructionCache.argtypes = [C.c_void_p, C.c_void_p, C.c_size_t]
k.GetCurrentProcess.restype = C.c_void_p
base = k.VirtualAlloc(None, 0x2280000, 0x3000, 0x40)
assert base
device = C.create_string_buffer(0x6800)
table = C.create_string_buffer(0x700)
dev = C.addressof(device)
C.c_uint64.from_address(dev).value = C.addressof(table)
A = C.c_uint64.from_address(dev + 0x5e68)
B = C.c_uint64.from_address(dev + 0x5e70)
calls = C.c_uint64()

def put(addr, data):
    C.memmove(base + addr, data, len(data))
    assert k.FlushInstructionCache(k.GetCurrentProcess(), base+addr, len(data))

try:
    C.c_uint64.from_address(base+0x22736c0).value = dev
    # Synthetic GetGfxDevice and virtual wait, retaining original instruction ABI.
    put(0x701050, b'\x48\xb8' + struct.pack('<Q', dev) + b'\xc3')
    # Count original wait calls, increment A like Unity (no actual wait in fixture).
    put(0x1000, b'\x48\xb8' + struct.pack('<Q', C.addressof(calls)) + b'\xf0\x48\xff\x00'
        + bytes.fromhex('f048ff81685e0000') + b'\xb8' + struct.pack('<I', 99) + b'\xc3')
    C.c_uint64.from_address(C.addressof(table)+0x698).value = base+0x1000
    # Windows x64 shadow space. arg==0 takes original JE; arg==1 calls/skips wait.
    prefix = bytes.fromhex('4883ec284885c9e9')
    put(0x2000, prefix + struct.pack('<i', rva-2-(0x2000+len(prefix)+4)))
    put(rva-2, bytes.fromhex('7413'))
    # Timestamp path returns 123; original wait result 99 bypasses timestamp path.
    put(0x5d0a77, b'\xb8' + struct.pack('<I',123) + bytes.fromhex('4883c428c3'))
    put(0x5d0aab, bytes.fromhex('4883c428c3'))
    fn = C.CFUNCTYPE(C.c_uint64, C.c_uint64)(base+0x2000)
    cases = 0
    for patched in (False, True, False, True, False):
        put(rva, after if patched else before)
        assert C.string_at(base+rva,19) == (after if patched else before)
        for initial in (0, 10, (1<<64)-1):
            A.value = B.value = initial
            n = calls.value
            assert fn(0) == 123 and A.value == initial and B.value == initial
            assert fn(1) == (123 if patched else 99)
            assert A.value == (initial+1) % (1<<64) and B.value == initial
            assert calls.value == n + (0 if patched else 1)
            B.value = (B.value+1) % (1<<64)  # synthetic OnAfterPresent
            assert A.value == B.value
            cases += 1
    put(rva, after)
    A.value = B.value = 0
    n = calls.value
    def worker(_):
        for i in range(10000):
            assert fn(1) == 123
    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(worker, range(4)))
    assert A.value == 40000 and B.value == 0 and calls.value == n
    put(rva, before)
    assert C.string_at(base+rva, 19) == before
    result = dict(unity_sha256=digest, guard_sites_verified=len(guards),
                  inline_bytes=19, branch_toggle_wrap_cases=cases,
                  atomic_increments=A.value, original_wait_calls_during_patch=0,
                  exact_restore=True, game_run=False,
                  patched_disasm=[f'{i.address:x} {i.mnemonic} {i.op_str}' for i in decoded])
    (WT/'out/waitskip-resize-offline.json').write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(result, indent=2))
finally:
    assert k.VirtualFree(base, 0, 0x8000)
