import struct

with open(r'bin\Debug\net8.0-windows\win-x64\r8bsrc.dll', 'rb') as f:
    d = f.read()

print(f"File size: {len(d)} bytes")

# Check MZ header
if d[0:2] != b'MZ':
    print("NOT a valid PE file!")
    exit(1)

pe_off = struct.unpack('<I', d[0x3C:0x40])[0]
print(f"PE offset: {pe_off}")

if d[pe_off:pe_off+4] != b'PE\x00\x00':
    print("No PE signature!")
    exit(1)

machine = struct.unpack('<H', d[pe_off+4:pe_off+6])[0]
machine_str = {0x8664: 'x64 (AMD64)', 0x14c: 'x86 (I386)', 0xaa64: 'ARM64', 0x1c0: 'ARMv7'}.get(machine, f'Unknown ({hex(machine)})')
print(f"Machine: {machine_str}")

num_sec = struct.unpack('<H', d[pe_off+6:pe_off+8])[0]
opt_hdr = struct.unpack('<H', d[pe_off+20:pe_off+22])[0]
sec_start = pe_off + 24 + opt_hdr

print(f"Sections: {num_sec}")
print(f"Optional header size: {opt_hdr}")
print()

# Check characteristics
char = struct.unpack('<H', d[pe_off+22:pe_off+24])[0]
is_dll = bool(char & 0x2000)
print(f"Characteristics: {hex(char)}")
print(f"  Is DLL: {is_dll}")
print()

# List sections
print("Sections:")
for i in range(num_sec):
    s = sec_start + i * 40
    name = d[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
    vsize = struct.unpack('<I', d[s+8:s+12])[0]
    vaddr = struct.unpack('<I', d[s+12:s+16])[0]
    rsize = struct.unpack('<I', d[s+16:s+20])[0]
    rptr = struct.unpack('<I', d[s+20:s+24])[0]
    print(f"  {name:8} vsize={vsize:8} vaddr={vaddr:#010x} rsize={rsize:8} rptr={rptr:#010x}")

# Check if .edata exists
has_edata = False
for i in range(num_sec):
    s = sec_start + i * 40
    name = d[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
    if name == '.edata':
        has_edata = True
        break

print(f"\nHas .edata (export) section: {has_edata}")

# Check for .reloc section
has_reloc = False
for i in range(num_sec):
    s = sec_start + i * 40
    name = d[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
    if name == '.reloc':
        has_reloc = True
        break

print(f"Has .reloc section: {has_reloc}")

# Check subsystem
subsystem = struct.unpack('<H', d[pe_off+68:pe_off+70])[0]
subsystem_str = {1: 'NATIVE', 2: 'WINDOWS_GUI', 3: 'WINDOWS_CUI', 9: 'WINDOWS_CE_GUI'}.get(subsystem, f'Unknown ({subsystem})')
print(f"Subsystem: {subsystem_str}")

# Check if it's actually a DLL by looking at the DLL flag in characteristics
print(f"\nDLL flag in characteristics: {bool(char & 0x2000)}")

# Let's also check the original DLL in libs/
print("\n" + "="*60)
print("Checking original DLL in libs/")
print("="*60)

with open(r'libs\r8bsrc.dll', 'rb') as f:
    d2 = f.read()

print(f"File size: {len(d2)} bytes")

if d2[0:2] == b'MZ':
    pe_off2 = struct.unpack('<I', d2[0x3C:0x40])[0]
    machine2 = struct.unpack('<H', d2[pe_off2+4:pe_off2+6])[0]
    machine_str2 = {0x8664: 'x64 (AMD64)', 0x14c: 'x86 (I386)'}.get(machine2, f'Unknown ({hex(machine2)})')
    print(f"Machine: {machine_str2}")
    
    char2 = struct.unpack('<H', d2[pe_off2+22:pe_off2+24])[0]
    print(f"DLL flag: {bool(char2 & 0x2000)}")
    
    num_sec2 = struct.unpack('<H', d2[pe_off2+6:pe_off2+8])[0]
    opt_hdr2 = struct.unpack('<H', d2[pe_off2+20:pe_off2+22])[0]
    sec_start2 = pe_off2 + 24 + opt_hdr2
    
    print(f"Sections: {num_sec2}")
    for i in range(num_sec2):
        s = sec_start2 + i * 40
        name = d2[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
        vsize = struct.unpack('<I', d2[s+8:s+12])[0]
        vaddr = struct.unpack('<I', d2[s+12:s+16])[0]
        rsize = struct.unpack('<I', d2[s+16:s+20])[0]
        rptr = struct.unpack('<I', d2[s+20:s+24])[0]
        print(f"  {name:8} vsize={vsize:8} vaddr={vaddr:#010x} rsize={rsize:8} rptr={rptr:#010x}")
    
    # Check exports in original
    has_edata2 = False
    for i in range(num_sec2):
        s = sec_start2 + i * 40
        name = d2[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
        if name == '.edata':
            has_edata2 = True
            break
    print(f"Has .edata: {has_edata2}")
    
    # Compare files
    print(f"\nFiles are identical: {d == d2}")
else:
    print("Original libs/r8bsrc.dll is NOT a valid PE file!")
