import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    d = f.read()

pe = struct.unpack('<I', d[0x3C:0x40])[0]
print('PE offset:', hex(pe))

num_sec = struct.unpack('<H', d[pe+6:pe+8])[0]
print('Sections:', num_sec)

opt_size = struct.unpack('<H', d[pe+20:pe+22])[0]
print('Optional header size:', opt_size)

dd_offset = pe + 24 + opt_size
print('Data dirs at:', hex(dd_offset))

exp_rva = struct.unpack('<I', d[dd_offset:dd_offset+4])[0]
exp_size = struct.unpack('<I', d[dd_offset+4:dd_offset+8])[0]
print('Export RVA:', hex(exp_rva), 'Size:', hex(exp_size))

# Print all data directories
for i in range(16):
    rva = struct.unpack('<I', d[dd_offset+i*8:dd_offset+i*8+4])[0]
    sz = struct.unpack('<I', d[dd_offset+i*8+4:dd_offset+i*8+8])[0]
    names = ['EXPORT', 'IMPORT', 'RESOURCE', 'EXCEPTION', 'SECURITY', 'BASERELOC', 'DEBUG', 'ARCHITECTURE', 'GLOBALPTR', 'TLS', 'LOAD_CONFIG', 'BOUND_IMPORT', 'IAT', 'DELAY_IMPORT', 'COM_DESCRIPTOR', 'UNUSED']
    if rva != 0:
        print(f'  [{i}] {names[i]}: RVA={hex(rva)}, Size={hex(sz)}')

# Print sections
sec_offset = pe + 24 + opt_size + 16 + 8
print('\nSections:')
for i in range(num_sec):
    sec = d[sec_offset + i*40:sec_offset + (i+1)*40]
    name = sec[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
    va = struct.unpack('<I', sec[12:16])[0]
    vs = struct.unpack('<I', sec[8:12])[0]
    rs = struct.unpack('<I', sec[16:20])[0]
    ro = struct.unpack('<I', sec[20:24])[0]
    print(f'  {name}: VA={hex(va)}, VirtSize={hex(vs)}, RawSize={hex(rs)}, RawOff={hex(ro)}')
