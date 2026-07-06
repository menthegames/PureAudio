import struct
with open('libs/r8bsrc.dll', 'rb') as f:
    f.seek(0x3C)
    pe_offset = struct.unpack('<I', f.read(4))[0]
    f.seek(pe_offset + 4)
    machine = struct.unpack('<H', f.read(2))[0]
    archs = {0x8664: 'x64', 0x14c: 'x86', 0xaa64: 'ARM64', 0x1c0: 'ARMv7'}
    print(f'Machine: 0x{machine:04X} ({archs.get(machine, "unknown")})')
