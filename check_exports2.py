import struct
import os

def parse_pe_exports(path):
    """Parse DLL exports manually without pefile dependency"""
    with open(path, 'rb') as f:
        data = f.read()
    
    # Parse DOS header
    if data[:2] != b'MZ':
        print("Not a valid PE file")
        return []
    
    pe_offset = struct.unpack('<I', data[60:64])[0]
    
    # Parse PE header
    if data[pe_offset:pe_offset+4] != b'PE\x00\x00':
        print("No PE signature")
        return []
    
    # Get number of sections and optional header size
    num_sections = struct.unpack('<H', data[pe_offset+6:pe_offset+8])[0]
    opt_header_size = struct.unpack('<H', data[pe_offset+16:pe_offset+18])[0]
    
    # Find export directory
    # Optional header starts at pe_offset + 24
    opt_header_start = pe_offset + 24
    
    # For PE32+ (64-bit), magic is 0x20b
    magic = struct.unpack('<H', data[opt_header_start:opt_header_start+2])[0]
    
    if magic == 0x10b:  # PE32
        data_dir_start = opt_header_start + 96
    elif magic == 0x20b:  # PE32+
        data_dir_start = opt_header_start + 112
    else:
        print(f"Unknown PE magic: 0x{magic:04x}")
        return []
    
    # Export directory is the first data directory entry
    export_rva = struct.unpack('<I', data[data_dir_start:data_dir_start+4])[0]
    export_size = struct.unpack('<I', data[data_dir_start+4:data_dir_start+8])[0]
    
    if export_rva == 0:
        print("No export directory")
        return []
    
    # Convert RVA to file offset
    # Parse section headers
    section_headers_start = opt_header_start + opt_header_size
    
    def rva_to_offset(rva):
        for i in range(num_sections):
            sh_start = section_headers_start + i * 40
            virtual_addr = struct.unpack('<I', data[sh_start+12:sh_start+16])[0]
            virtual_size = struct.unpack('<I', data[sh_start+8:sh_start+12])[0]
            raw_addr = struct.unpack('<I', data[sh_start+20:sh_start+24])[0]
            if virtual_addr <= rva < virtual_addr + virtual_size:
                return rva - virtual_addr + raw_addr
        return None
    
    export_offset = rva_to_offset(export_rva)
    if export_offset is None:
        print("Cannot resolve export directory")
        return []
    
    # Parse export directory
    num_functions = struct.unpack('<I', data[export_offset+20:export_offset+24])[0]
    num_names = struct.unpack('<I', data[export_offset+24:export_offset+28])[0]
    addr_of_functions = rva_to_offset(struct.unpack('<I', data[export_offset+28:export_offset+32])[0])
    addr_of_names = rva_to_offset(struct.unpack('<I', data[export_offset+32:export_offset+36])[0])
    addr_of_ordinals = rva_to_offset(struct.unpack('<I', data[export_offset+36:export_offset+40])[0])
    
    exports = []
    for i in range(num_names):
        name_rva = struct.unpack('<I', data[addr_of_names + i*4:addr_of_names + i*4 + 4])[0]
        name_offset = rva_to_offset(name_rva)
        if name_offset:
            name = data[name_offset:data.index(b'\x00', name_offset)].decode('ascii', errors='replace')
            ordinal = struct.unpack('<H', data[addr_of_ordinals + i*2:addr_of_ordinals + i*2 + 2])[0]
            func_rva = struct.unpack('<I', data[addr_of_functions + ordinal*4:addr_of_functions + ordinal*4 + 4])[0]
            exports.append((ordinal, name, func_rva))
    
    return exports

# Check all DLLs
files_to_check = [
    'libs/r8bsrc.dll',
    'r8brain_repo/DLL/Win64/r8bsrc.dll',
    'r8brain_repo/DLL/Win32/r8bsrc.dll',
]

for fpath in files_to_check:
    if os.path.exists(fpath):
        print(f'=== {fpath} ===')
        exports = parse_pe_exports(fpath)
        if exports:
            print(f'  Number of exports: {len(exports)}')
            for ord_num, name, rva in sorted(exports, key=lambda x: x[0]):
                print(f'    [{ord_num}] {name} @ 0x{rva:08X}')
        else:
            print('  No exports found')
        print()
    else:
        print(f'=== {fpath} === NOT FOUND')
        print()
