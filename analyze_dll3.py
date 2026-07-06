import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    data = f.read()

# DOS header
pe_offset = struct.unpack_from('<I', data, 0x3C)[0]
print(f'PE offset: {pe_offset}')

# PE signature
sig = data[pe_offset:pe_offset+4]
print(f'PE signature: {sig}')

# COFF header
machine = struct.unpack_from('<H', data, pe_offset+4)[0]
num_sections = struct.unpack_from('<H', data, pe_offset+6)[0]
print(f'Machine: 0x{machine:04X}, Sections: {num_sections}')

# Optional header
opt_hdr_size = struct.unpack_from('<H', data, pe_offset+16)[0]
print(f'Optional header size: {opt_hdr_size}')

# PE magic
magic = struct.unpack_from('<H', data, pe_offset+24)[0]
print(f'PE magic: 0x{magic:04X}')

# Data directories
if magic == 0x20b:  # PE32+
    export_rva_off = pe_offset + 24 + 112
else:  # PE32
    export_rva_off = pe_offset + 24 + 96

export_rva = struct.unpack_from('<I', data, export_rva_off)[0]
export_size = struct.unpack_from('<I', data, export_rva_off+4)[0]
print(f'Export RVA: 0x{export_rva:08X}, Size: 0x{export_size:X}')

# Section headers
sect_offset = pe_offset + 24 + opt_hdr_size
print(f'\nSections (at offset 0x{sect_offset:X}):')
for i in range(num_sections):
    so = sect_offset + i * 40
    name = data[so:so+8].rstrip(b'\x00').decode('ascii', errors='replace')
    virt_size = struct.unpack_from('<I', data, so+8)[0]
    virt_addr = struct.unpack_from('<I', data, so+12)[0]
    raw_size = struct.unpack_from('<I', data, so+16)[0]
    raw_addr = struct.unpack_from('<I', data, so+20)[0]
    print(f'  {name}: VA=0x{virt_addr:08X}, VirtSize=0x{virt_size:X}, RawSize=0x{raw_size:X}, RawOff=0x{raw_addr:X}')
    
    # Check if export RVA falls in this section
    if export_rva >= virt_addr and export_rva < virt_addr + max(virt_size, raw_size):
        file_offset = raw_addr + (export_rva - virt_addr)
        print(f'    -> Export table at file offset 0x{file_offset:X}')
        
        # Parse export table
        with open('libs/r8bsrc.dll', 'rb') as f2:
            f2.seek(file_offset)
            exp_data = f2.read(export_size)
            
            num_names = struct.unpack_from('<I', exp_data, 24)[0]
            addr_of_funcs = struct.unpack_from('<I', exp_data, 28)[0]
            addr_of_names = struct.unpack_from('<I', exp_data, 32)[0]
            addr_of_ordinals = struct.unpack_from('<I', exp_data, 36)[0]
            
            print(f'    Number of exports: {num_names}')
            
            # Calculate file offsets for names
            name_rva_off = raw_addr + (addr_of_names - virt_addr)
            f2.seek(name_rva_off)
            name_ptrs = struct.unpack_from(f'<{num_names}I', f2.read(num_names * 4))
            
            print(f'\n    Exported functions:')
            for j, name_rva in enumerate(name_ptrs):
                name_off = raw_addr + (name_rva - virt_addr)
                f2.seek(name_off)
                func_name = b''
                while True:
                    b = f2.read(1)
                    if b == b'\x00' or not b:
                        break
                    func_name += b
                print(f'      [{j}] {func_name.decode("ascii", errors="replace")}')
