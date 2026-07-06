import urllib.request
import struct

url = "https://raw.githubusercontent.com/unevens/r8brain/master/DLL/Win64/r8bsrc.dll"
print(f"Downloading from: {url}")

try:
    req = urllib.request.Request(url, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        d = resp.read()
    
    print(f"Downloaded: {len(d)} bytes")
    
    # Check PE structure
    if d[0:2] == b'MZ':
        pe_off = struct.unpack('<I', d[0x3C:0x40])[0]
        machine = struct.unpack('<H', d[pe_off+4:pe_off+6])[0]
        machine_str = {0x8664: 'x64', 0x14c: 'x86'}.get(machine, f'Unknown({hex(machine)})')
        print(f"Machine: {machine_str}")
        
        char = struct.unpack('<H', d[pe_off+22:pe_off+24])[0]
        print(f"Is DLL: {bool(char & 0x2000)}")
        
        num_sec = struct.unpack('<H', d[pe_off+6:pe_off+8])[0]
        opt_hdr = struct.unpack('<H', d[pe_off+20:pe_off+22])[0]
        sec_start = pe_off + 24 + opt_hdr
        
        has_edata = False
        for i in range(num_sec):
            s = sec_start + i * 40
            name = d[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
            if name == '.edata':
                has_edata = True
                # Parse exports
                vsize = struct.unpack('<I', d[s+8:s+12])[0]
                vaddr = struct.unpack('<I', d[s+12:s+16])[0]
                rsize = struct.unpack('<I', d[s+16:s+20])[0]
                rptr = struct.unpack('<I', d[s+20:s+24])[0]
                print(f"\n.edata section: vsize={vsize}, rptr={rptr:#x}")
                
                edata = d[rptr:rptr+rsize]
                num_names = struct.unpack('<I', edata[24:28])[0]
                name_ptr_rva = struct.unpack('<I', edata[32:36])[0]
                
                print(f"Number of exported names: {num_names}")
                
                # Simple RVA to offset
                def rva_to_off(rva):
                    for j in range(num_sec):
                        s2 = sec_start + j * 40
                        svaddr = struct.unpack('<I', d[s2+12:s2+16])[0]
                        svsize = struct.unpack('<I', d[s2+8:s2+12])[0]
                        srptr = struct.unpack('<I', d[s2+20:s2+24])[0]
                        if svaddr <= rva < svaddr + svsize:
                            return rva - svaddr + srptr
                    return None
                
                name_ptr_off = rva_to_off(name_ptr_rva)
                if name_ptr_off:
                    print(f"\nExported functions:")
                    for j in range(min(num_names, 30)):
                        nrv = struct.unpack('<I', d[name_ptr_off+j*4:name_ptr_off+j*4+4])[0]
                        noff = rva_to_off(nrv)
                        if noff:
                            fname = d[noff:noff+50].split(b'\x00')[0].decode('ascii', errors='replace')
                            print(f"  {j}: {fname}")
                break
        
        if not has_edata:
            print("\nNo .edata section - DLL has NO exports!")
            print("This DLL cannot be used with P/Invoke!")
            
            # Check if functions are there as strings
            for func in [b'r8b_create', b'r8b_process', b'r8b_clear', b'r8b_delete']:
                idx = d.find(func)
                if idx >= 0:
                    print(f"  '{func.decode()}' found as string at offset {idx}")
                else:
                    print(f"  '{func.decode()}' NOT found")
    else:
        print("Not a valid PE file!")
        
except Exception as e:
    print(f"Error: {e}")
