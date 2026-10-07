# -*- coding: utf-8 -*-
"""反汇编 GameAssembly.dll 中的关键方法，验证 EntryKey 拼接格式。"""
import struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

DLL = r'<G>\GameAssembly.dll'
data = open(DLL, 'rb').read()

# 解析 PE 头，建立 VA->文件偏移映射
pe_off = struct.unpack_from('<I', data, 0x3C)[0]
num_sec = struct.unpack_from('<H', data, pe_off + 6)[0]
opt_size = struct.unpack_from('<H', data, pe_off + 20)[0]
image_base = struct.unpack_from('<Q', data, pe_off + 24 + 24)[0]
sec_off = pe_off + 24 + opt_size
sections = []
for i in range(num_sec):
    off = sec_off + 40 * i
    name = data[off:off+8].rstrip(b'\0').decode('ascii', 'replace')
    vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', data, off + 8)
    sections.append((name, vaddr, vsize, rawptr, rawsize))
print('image_base=%X' % image_base)

def va_to_off(va):
    rva = va - image_base
    for name, vaddr, vsize, rawptr, rawsize in sections:
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None

def read_cstr_utf16(va, maxlen=200):
    off = va_to_off(va)
    if off is None: return None
    b = data[off:off+maxlen*2]
    out = []
    for i in range(0, len(b)-1, 2):
        ch = struct.unpack_from('<H', b, i)[0]
        if ch == 0: break
        out.append(chr(ch))
    return ''.join(out)

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True

def disasm(name, file_off, count=60):
    print('='*70)
    print(name, '@file 0x%X' % file_off)
    code = data[file_off:file_off+count*16]
    va = image_base + file_off  # 简化：仅当段 VA 映射连续时成立；改用段映射
    # 由 file_off 反推 VA
    for sec in sections:
        sname, vaddr, vsize, rawptr, rawsize = sec
        if rawptr <= file_off < rawptr + rawsize:
            va = image_base + vaddr + (file_off - rawptr)
            break
    for ins in md.disasm(code, va):
        line = '0x%X: %s %s' % (ins.address, ins.mnemonic, ins.op_str)
        # 解析 rip-relative 目标
        if ins.mnemonic == 'lea' and 'rip' in ins.op_str:
            disp = int(ins.op_str.split('+')[1].rstrip(']'), 16) if '+' in ins.op_str else 0
            tgt = ins.address + ins.size + disp
            s = read_cstr_utf16(tgt, 60)
            if s and all(32 <= ord(c) < 0x3000 or c in '|{}: ' for c in s):
                line += '   ; -> "%s"' % s[:60]
        print(line)
        if ins.mnemonic == 'ret' and ins.address > va + 20:
            break

# EntryKey(file 0xD76820)、IsEntryEnabled(0xD7BA40)、LoadPrefs(0xD7BAD0)
disasm('ModPackage.EntryKey', 0xD76820)
disasm('ModRegistry.IsEntryEnabled', 0xD7BA40)
disasm('ModRegistry.LoadPrefs', 0xD7BAD0)
