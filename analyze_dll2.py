import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    data = f.read()

# The export directory at 0x1d9c50
exp = data[0x1d9c50:0x1d9c78]
print('Export directory:')
print(f'  Characteristics: {struct.unpack("<I", exp[0:4])[0]:#x}')
print(f'  TimeDateStamp: {struct.unpack("<I", exp[4:8])[0]:#x}')
print(f'  MajorVersion: {struct.unpack("<H", exp[8:10])[0]}')
print(f'  MinorVersion: {struct.unpack("<H", exp[10:12])[0]}')
print(f'  Name RVA: {struct.unpack("<I", exp[12:16])[0]:#x}')
print(f'  Base: {struct.unpack("<I", exp[16:20])[0]}')
print(f'  NumberOfFunctions: {struct.unpack("<I", exp[20:24])[0]}')
print(f'  NumberOfNames: {struct.unpack("<I", exp[24:28])[0]}')
print(f'  AddressOfFunctions RVA: {struct.unpack("<I", exp[28:32])[0]:#x}')
print(f'  AddressOfNames RVA: {struct.unpack("<I", exp[32:36])[0]:#x}')
print(f'  AddressOfNameOrdinals RVA: {struct.unpack("<I", exp[36:40])[0]:#x}')

# Convert to file offsets using .rdata section
# .rdata: VA=0x17f000, RawOff=0x17d800
rdata_va = 0x17f000
rdata_fo = 0x17d800

func_fo = rdata_fo + (0x001db478 - rdata_va)
names_fo = rdata_fo + (0x001db488 - rdata_va)
ord_fo = rdata_fo + (0x001db498 - rdata_va)

print(f'\nAddressOfFunctions file offset: {func_fo:#x}')
print(f'AddressOfNames file offset: {names_fo:#x}')
print(f'AddressOfNameOrdinals file offset: {ord_fo:#x}')

# Read function addresses
print(f'\nFunction addresses (4 functions):')
for j in range(4):
    rva = struct.unpack('<I', data[func_fo+j*4:func_fo+j*4+4])[0]
    print(f'  [{j}] RVA={rva:#010x}')

# Read name pointers
print(f'\nName pointers (4 names):')
for j in range(4):
    rva = struct.unpack('<I', data[names_fo+j*4:names_fo+j*4+4])[0]
    name_fo = rdata_fo + (rva - rdata_va)
    name_str = data[name_fo:name_fo+50].split(b'\x00')[0].decode('ascii', errors='replace')
    print(f'  [{j}] RVA={rva:#x} -> {name_str}')

# Read ordinals
print(f'\nOrdinals (4):')
for j in range(4):
    ord_val = struct.unpack('<H', data[ord_fo+j*2:ord_fo+j*2+2])[0]
    print(f'  [{j}] {ord_val}')

# Also check the DLL name
name_rva = struct.unpack("<I", exp[12:16])[0]
name_fo = rdata_fo + (name_rva - rdata_va)
dll_name = data[name_fo:name_fo+50].split(b'\x00')[0].decode('ascii', errors='replace')
print(f'\nDLL name: {dll_name}')
