import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    # DOS header
    f.seek(0x3C)
    pe_offset = struct.unpack('<I', f.read(4))[0]
    
    # PE header
    f.seek(pe_offset + 4)
    machine = struct.unpack('<H', f.read(2))[0]
    num_sections = struct.unpack('<H', f.read(2))[0]
    
    archs = {0x8664: 'x64', 0x14c: 'x86', 0xaa64: 'ARM64', 0x1c0: 'ARMv7'}
    print(f'Architecture: {archs.get(machine, "unknown")} (0x{machine:04X})')
    
    # Optional header
    f.seek(pe_offset + 20)
    opt_header_size = struct.unpack('<H', f.read(2))[0]
    
    # Section headers
    f.seek(pe_offset + 24 + opt_header_size)
    
    sections = []
    for i in range(num_sections):
        name = f.read(8).decode('ascii', errors='ignore').rstrip('\x00')
        virtual_size = struct.unpack('<I', f.read(4))[0]
        virtual_addr = struct.unpack('<I', f.read(4))[0]
        raw_size = struct.unpack('<I', f.read(4))[0]
        raw_addr = struct.unpack('<I', f.read(4))[0]
        f.read(16)
        sections.append((name, virtual_size, virtual_addr, raw_size, raw_addr))
    
    # Find .edata
    for name, vs, va, rs, ra in sections:
        if name == '.edata':
            f.seek(ra)
            f.read(8)  # flags + timestamp
            f.read(4)  # version
            name_rva = struct.unpack('<I', f.read(4))[0]
            ordinal_base = struct.unpack('<I', f.read(4))[0]
            num_functions = struct.unpack('<I', f.read(4))[0]
            num_names = struct.unpack('<I', f.read(4))[0]
            addr_functions_rva = struct.unpack('<I', f.read(4))[0]
            addr_names_rva = struct.unpack('<I', f.read(4))[0]
            addr_ordinals_rva = struct.unpack('<I', f.read(4))[0]
            
            print(f'Exports: {num_functions} functions, {num_names} named')
            
            # Read names
            names_offset = addr_names_rva - va + ra
            f.seek(names_offset)
            for j in range(num_names):
                name_rva = struct.unpack('<I', f.read(4))[0]
                name_offset = name_rva - va + ra
                old_pos = f.tell()
                f.seek(name_offset)
                name_bytes = b''
                while True:
                    b = f.read(1)
                    if b == b'\x00' or not b:
                        break
                    name_bytes += b
                f.seek(old_pos)
                print(f'  [{j}] {name_bytes.decode("ascii", errors="ignore")}')
            break
    else:
        print('No .edata section found')
