"""Offline resize-path extension; inherits PE/PDB identity checks from present-path."""
import runpy
from pathlib import Path

c = runpy.run_path(str(Path(__file__).with_name('present-path-disasm.py')))
pe, md, symbols, starts, label = (c[k] for k in ('pe', 'md', 'symbols', 'starts', 'label'))
import bisect
names = ('Screen_CUSTOM_SetResolution', 'ScreenManagerWin::SetRequestedResolution',
         'ScreenManagerWin::SetResolutionImmediate', 'ScreenManagerWin::HandleWindowResize',
         'ScreenManagerWin::UpdateResolutionAfterPresent', 'ScreenManagerWin::UpdateResolutionPrefs',
         'PlayerMainWndProc', 'GfxDisplayManagerD3D11::SetResolution',
         'D3D11SwapChain::Adjust', 'D3D11SwapChain::ReleaseBackbufferResources',
         'D3D11SwapChain::CreateBackBufferResources', 'WinScreenSetup::SetResolution',
         'WinScreenSetup::AdjustWindowForModeChange', 'WinScreenSetup::SetDXGIExclusiveFullscreenMode',
         'DXGICommon::ResizeTargetWindowIfNeeded', 'HandlePendingWindowResize',
         'winutils::DisplayErrorMessagesAndQuit', 'winutils::DisplayErrorMessages',
         'winutils::AddErrorMessage', 'ShouldResizeTargetWindow',
         'GfxDeviceD3D11Base::OnAfterPresent', 'GfxDeviceD3D11::OnAfterPresent',
         'GfxDeviceD3D11::WaitForLastPresentationAndGetTimestamp',
         'GfxDeviceD3D11::PresentFrame', 'GfxDeviceD3D11Base::GfxDeviceD3D11Base',
         'DXGIWaitForHandleWithMessagePump', 'GfxDeviceVK::WaitForPresentationImpl',
         'SwapChainWaitingContext::ReleaseResources',
         'SwapChainWaitingContext::SetMaximumFrameLatencyForWaitableObject',
         'SwapChainWaitingContext::WaitOnSwapChain')
lines = []
for a, name in symbols:
    if name not in names: continue
    end = starts[bisect.bisect_right(starts, a)]
    lines.append(f'\n## {name} {a:x}..{end:x}')
    for ins in md.disasm(pe.get_data(a, end-a), a):
        suffix = ''
        if ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
            suffix = ' ; ' + label(int(ins.op_str, 16))
        for op in ins.operands:
            if op.type == c['capstone'].x86.X86_OP_MEM and op.mem.base == c['capstone'].x86.X86_REG_RIP:
                target = ins.address + ins.size + op.mem.disp
                raw = pe.get_data(target, 300).split(b'\0', 1)[0]
                literal = raw.decode('ascii', errors='replace') if raw and all(32 <= b < 127 for b in raw) else ''
                suffix += f' ; [{target:x}] ' + (literal or label(target))
        lines.append(f'{ins.address:08x} {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{suffix}')
# Exact primary-table size and callback ABI used by the one-shot object hook.
import struct
base = pe.OPTIONAL_HEADER.ImageBase
table = 0x1e3bcd0
entries = struct.unpack('<380Q', pe.get_data(table, 0xbe0))
assert entries[0] == base + 0x930b30
assert entries[0xbd8 // 8] == base + 0x930dc0
assert any(a == table and name == "GfxDeviceD3D11::`vftable'" for a, name in symbols)
assert any(a == table + 0xbe0 and name.endswith("`vftable'") for a, name in symbols)
for value in entries:
    assert any(s.VirtualAddress <= value-base < s.VirtualAddress+s.Misc_VirtualSize
               and s.Characteristics & 0x20000000 for s in pe.sections)
lines.append(f'\nPrimary device table {table:x}..{table+0xbe0:x}: 380 executable slots; '
             'slot 0xbd8 = GfxDeviceD3D11::OnAfterPresent 930dc0; next symbol is secondary vftable.')
# Search every executable section for both field displacements, then decode from
# the enclosing symbol start to discard byte-pattern matches inside other operands.
candidate_functions = set()
for section in pe.sections:
    if not section.Characteristics & 0x20000000: continue
    data = section.get_data()
    for offset in (0x5e68, 0x5e70):
        position = 0
        while (position := data.find(offset.to_bytes(4, 'little'), position)) >= 0:
            address = section.VirtualAddress + position
            candidate_functions.add(starts[bisect.bisect_right(starts, address)-1])
            position += 1
lines.append('\n## All decoded executable references to guard field displacements')
for address in sorted(candidate_functions):
    end = starts[bisect.bisect_right(starts, address)]
    for ins in md.disasm(pe.get_data(address, end-address), address):
        if any(op.type == c['capstone'].x86.X86_OP_MEM and op.mem.disp in (0x5e68, 0x5e70)
               for op in ins.operands):
            lines.append(f'{ins.address:08x} {ins.mnemonic} {ins.op_str} ; {label(ins.address)}')
out = c['WT'] / 'out/present-buffers'
out.mkdir(exist_ok=True)
(out / 'disasm.txt').write_text('\n'.join(lines), encoding='utf-8')
print(out / 'disasm.txt')
