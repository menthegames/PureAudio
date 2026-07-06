import struct

# Read the DLL from output directory
with open(r'bin\Debug\net8.0-windows\win-x64\r8bsrc.dll', 'rb') as f:
    d = f.read()

# Search for function names in the DLL
funcs_to_find = [b'r8b_create', b'r8b_process', b'r8b_clear', b'r8b_delete', b'r8b_']
for func in funcs_to_find:
    idx = d.find(func)
    if idx >= 0:
        print(f"Found '{func.decode()}' at offset {idx}")
        print(f"  Context: {d[max(0,idx-5):idx+len(func)+20]}")
    else:
        print(f"'{func.decode()}' NOT found in DLL")

# Check PE header for exported functions
# PE signature at offset 0x3C
if d[0:2] == b'MZ':
    pe_offset = struct.unpack('<I', d[0x3C:0x40])[0]
    print(f"\nPE signature at offset {pe_offset}")
    
    # Export directory
    if d[pe_offset:pe_offset+4] == b'PE\x00\x00':
        # Number of sections
        num_sections = struct.unpack('<H', d[pe_offset+6:pe_offset+8])[0]
        print(f"Number of sections: {num_sections}")
        
        # Optional header size
        opt_header_size = struct.unpack('<H', d[pe_offset+20:pe_offset+22])[0]
        print(f"Optional header size: {opt_header_size}")
        
        # Section headers start
        sections_start = pe_offset + 24 + opt_header_size
        
        # Find .edata section
        for i in range(num_sections):
            sec_start = sections_start + i * 40
            sec_name = d[sec_start:sec_start+8].rstrip(b'\x00').decode('ascii', errors='replace')
            sec_vsize = struct.unpack('<I', d[sec_start+8:sec_start+12])[0]
            sec_vaddr = struct.unpack('<I', d[sec_start+12:sec_start+16])[0]
            sec_rsize = struct.unpack('<I', d[sec_start+16:sec_start+20])[0]
            sec_rptr = struct.unpack('<I', d[sec_start+20:sec_start+24])[0]
            
            if sec_name == '.edata':
                print(f"\nExport section (.edata):")
                print(f"  Virtual size: {sec_vsize}")
                print(f"  Virtual addr: {sec_vaddr:#x}")
                print(f"  Raw size: {sec_rsize}")
                print(f"  Raw ptr: {sec_rptr:#x}")
                
                # Parse export directory
                edata = d[sec_rptr:sec_rptr+sec_rsize]
                export_flags = struct.unpack('<I', edata[0:4])[0]
                time_stamp = struct.unpack('<I', edata[4:8])[0]
                major_ver = struct.unpack('<H', edata[8:10])[0]
                minor_ver = struct.unpack('<H', edata[10:12])[0]
                name_rva = struct.unpack('<I', edata[12:16])[0]
                ordinal_base = struct.unpack('<I', edata[16:20])[0]
                num_functions = struct.unpack('<I', edata[20:24])[0]
                num_names = struct.unpack('<I', edata[24:28])[0]
                addr_tbl_rva = struct.unpack('<I', edata[28:32])[0]
                name_ptr_rva = struct.unpack('<I', edata[32:36])[0]
                ordinal_tbl_rva = struct.unpack('<I', edata[36:40])[0]
                
                print(f"  Number of functions: {num_functions}")
                print(f"  Number of names: {num_names}")
                
                # Convert RVA to file offset
                def rva_to_offset(rva):
                    for j in range(num_sections):
                        s_start = sections_start + j * 40
                        s_vaddr = struct.unpack('<I', d[s_start+12:s_start+16])[0]
                        s_vsize = struct.unpack('<I', d[s_start+8:s_start+12])[0]
                        s_rptr = struct.unpack('<I', d[s_start+20:s_start+24])[0]
                        if s_vaddr <= rva < s_vaddr + s_vsize:
                            return rva - s_vaddr + s_rptr
                    return None
                
                # Read name pointers
                name_ptr_off = rva_to_offset(name_ptr_rva)
                if name_ptr_off:
                    print(f"\n  Exported functions (by name):")
                    for j in range(min(num_names, 50)):
                        name_rva_off = name_ptr_off + j * 4
                        if name_rva_off + 4 > len(d):
                            break
                        name_rva_val = struct.unpack('<I', d[name_rva_off:name_rva_off+4])[0]
                        name_off = rva_to_offset(name_rva_val)
                        if name_off:
                            name_bytes = d[name_off:name_off+50].split(b'\x00')[0]
                            name = name_bytes.decode('ascii', errors='replace')
                            print(f"    {j}: {name}")
                
                break
        else:
            print("\nNo .edata section found (DLL may have no exports)")
else:
    print("Not a valid PE file (no MZ header)")
