import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    dos_header = f.read(64)
    pe_offset = struct.unpack('<I', dos_header[0x3c:0x40])[0]
    f.seek(pe_offset)
    sig = f.read(4)
    machine = struct.unpack('<H', f.read(2))[0]
    
    if machine == 0x8664:
        print('Bitness: 64-bit (x64)')
    elif machine == 0x14c:
        print('Bitness: 32-bit (x86)')
    elif machine == 0xaa64:
        print('Bitness: ARM64')
    else:
        print(f'Bitness: Unknown (0x{machine:04x})')

import os
size = os.path.getsize('libs/r8bsrc.dll')
print(f'File size: {size} bytes ({size/1024:.1f} KB)')
