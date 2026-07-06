import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    data = f.read()

print(f'File size: {len(data)} bytes')
print(f'First 4 bytes: {data[:4].hex()}')

pe_off = struct.unpack('<I', data[0x3C:0x40])[0]
print(f'PE offset: {pe_off:#x}')
print(f'PE signature: {data[pe_off:pe_off+4]}')

machine = struct.unpack('<H', data[pe_off+4:pe_off+6])[0]
archs = {0x8664: 'x64', 0x14c: 'x86', 0xaa64: 'ARM64', 0x1c0: 'ARMv7'}
print(f'Machine: 0x{machine:04X} ({archs.get(machine, "unknown")})')

# Number of sections
num_sec = struct.unpack('<H', data[pe_off+6:pe_off+8])[0]
print(f'Sections: {num_sec}')

# Optional header size
opt_size = struct.unpack('<H', data[pe_off+20:pe_off+22])[0]
print(f'Optional header size: {opt_size}')

# Data directories
dd_offset = pe_off + 24 + opt_size
print(f'Data dirs at: {dd_offset:#x}')

exp_rva = struct.unpack('<I', data[dd_offset:dd_offset+4])[0]
exp_size = struct.unpack('<I', data[dd_offset+4:dd_offset+8])[0]
print(f'Export RVA: {exp_rva:#x}, Size: {exp_size:#x}')

# Check if export directory looks valid
if exp_rva > 0 and exp_rva < len(data):
    print(f'Export directory looks valid (RVA within file bounds)')
else:
    print(f'WARNING: Export RVA {exp_rva:#x} is outside file bounds ({len(data)} bytes)')
    print(f'This DLL may be corrupted or not a valid PE file!')

# Print sections
sec_offset = pe_off + 24 + opt_size + 16 + 8
print(f'\nSections (at offset {sec_offset:#x}):')
for i in range(num_sec):
    sec_start = sec_offset + i * 40
    sec = data[sec_start:sec_start+40]
    name = sec[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
    vs = struct.unpack('<I', sec[8:12])[0]
    va = struct.unpack('<I', sec[12:16])[0]
    rs = struct.unpack('<I', sec[16:20])[0]
    ro = struct.unpack('<I', sec[20:24])[0]
    print(f'  [{i}] {name}: VA={va:#x}, VirtSize={vs:#x}, RawSize={rs:#x}, RawOff={ro:#x}')
