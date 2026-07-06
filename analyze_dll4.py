import struct

with open('libs/r8bsrc.dll', 'rb') as f:
    data = f.read()

pe_off = struct.unpack_from('<I', data, 0x3C)[0]
print(f'PE offset: {pe_off}')

# PE32+ (magic 0x20b) optional header structure:
# After PE signature (4 bytes) + COFF header (20 bytes) = 24 bytes
# Optional header starts at pe_off + 24
magic = struct.unpack_from('<H', data, pe_off + 24)[0]
print(f'Magic: 0x{magic:04X}')

if magic == 0x20b:  # PE32+
    # PE32+ optional header:
    # 0-1: Magic (2)
    # 2-3: LMajor (1) + LMinor (1)
    # 4-7: CodeSize (4)
    # 8-11: InitializedDataSize (4)
    # 12-15: UninitializedDataSize (4)
    # 16-19: EntryPoint RVA (4)
    # 20-23: BaseOfCode (4)
    # 24-27: ImageBase (8) - PE32+ has 8-byte ImageBase
    # 28-31: (cont) ImageBase
    # 32-35: SectionAlignment (4)
    # 36-39: FileAlignment (4)
    # 40-43: OSMajor (2) + OSMinor (2)
    # 44-47: ImageMajor (2) + ImageMinor (2)
    # 48-51: SubSysMajor (2) + SubSysMinor (2)
    # 52-55: Win32VersionValue (4)
    # 56-59: SizeOfImage (4)
    # 60-63: SizeOfHeaders (4)
    # 64-67: CheckSum (4)
    # 68-69: Subsystem (2)
    # 70-71: DllCharacteristics (2)
    # 72-79: SizeOfStackReserve (8)
    # 80-87: SizeOfStackCommit (8)
    # 88-95: SizeOfHeapReserve (8)
    # 96-103: SizeOfHeapCommit (8)
    # 104-107: LoaderFlags (4)
    # 108-111: NumberOfRvaAndSizes (4)
    # 112: DataDirectory array starts
    
    opt_hdr_start = pe_off + 24
    num_rva_sizes = struct.unpack_from('<I', data, opt_hdr_start + 108)[0]
    print(f'Number of data directories: {num_rva_sizes}')
    
    # Export directory is first (index 0)
    export_dir_off = opt_hdr_start + 112
    export_rva = struct.unpack_from('<I', data, export_dir_off)[0]
    export_size = struct.unpack_from('<I', data, export_dir_off + 4)[0]
    print(f'Export RVA: 0x{export_rva:08X}, Size: 0x{export_size:X}')
    
    # Section headers start after optional header
    # SizeOfOptionalHeader is at pe_off + 16 (2 bytes)
    size_of_opt_hdr = struct.unpack_from('<H', data, pe_off + 16)[0]
    print(f'SizeOfOptionalHeader: {size_of_opt_hdr}')
    
    sect_start = pe_off + 24 + size_of_opt_hdr
    num_sections = struct.unpack_from('<H', data, pe_off + 6)[0]
    print(f'Number of sections: {num_sections}')
    
    print(f'\nSections:')
    for i in range(num_sections):
        so = sect_start + i * 40
        name = data[so:so+8].rstrip(b'\x00').decode('ascii', errors='replace')
        virt_size = struct.unpack_from('<I', data, so+8)[0]
        virt_addr = struct.unpack_from('<I', data, so+12)[0]
        raw_size = struct.unpack_from('<I', data, so+16)[0]
        raw_addr = struct.unpack_from('<I', data, so+20)[0]
        print(f'  {name}: VA=0x{virt_addr:08X} Size=0x{virt_size:X} RawOff=0x{raw_addr:X} RawSize=0x{raw_size:X}')
        
        # Check if export RVA falls in this section
        if export_rva >= virt_addr and export_rva < virt_addr + virt_size:
            file_offset = raw_addr + (export_rva - virt_addr)
            print(f'    -> Export table at file offset 0x{file_offset:X}')
            
            # Parse export table
            exp_data = data[file_offset:file_offset+export_size]
            
            flags = struct.unpack_from('<I', exp_data, 0)[0]
            timestamp = struct.unpack_from('<I', exp_data, 4)[0]
            major_ver = struct.unpack_from('<H', exp_data, 8)[0]
            minor_ver = struct.unpack_from('<H', exp_data, 10)[0]
            name_rva = struct.unpack_from('<I', exp_data, 12)[0]
            ordinal_base = struct.unpack_from('<I', exp_data, 16)[0]
            num_funcs = struct.unpack_from('<I', exp_data, 20)[0]
            num_names = struct.unpack_from('<I', exp_data, 24)[0]
            addr_of_funcs = struct.unpack_from('<I', exp_data, 28)[0]
            addr_of_names = struct.unpack_from('<I', exp_data, 32)[0]
            addr_of_ordinals = struct.unpack_from('<I', exp_data, 36)[0]
            
            print(f'    Flags: 0x{flags:X}')
            print(f'    Timestamp: {timestamp}')
            print(f'    Version: {major_ver}.{minor_ver}')
            print(f'    Ordinal base: {ordinal_base}')
            print(f'    Number of functions: {num_funcs}')
            print(f'    Number of names: {num_names}')
            
            # Read function addresses
            funcs_rva_off = raw_addr + (addr_of_funcs - virt_addr)
            func_addrs = struct.unpack_from(f'<{num_funcs}I', data, funcs_rva_off)
            
            # Read name pointers
            names_rva_off = raw_addr + (addr_of_names - virt_addr)
            name_ptrs = struct.unpack_from(f'<{num_names}I', data, names_rva_off)
            
            # Read ordinals
            ordinals_rva_off = raw_addr + (addr_of_ordinals - virt_addr)
            ordinals = struct.unpack_from(f'<{num_names}H', data, ordinals_rva_off)
            
            print(f'\n    Exported functions (by name):')
            for j in range(num_names):
                name_rva_val = name_ptrs[j]
                ordinal = ordinals[j]
                func_addr = func_addrs[ordinal]
                
                name_off = raw_addr + (name_rva_val - virt_addr)
                func_name = b''
                while name_off < len(data):
                    b = data[name_off:name_off+1]
                    if b == b'\x00' or not b:
                        break
                    func_name += b
                    name_off += 1
                print(f'      [{j}] Ordinal={ordinal} Addr=0x{func_addr:08X} {func_name.decode("ascii", errors="replace")}')
else:
    print('Not PE32+ format')
