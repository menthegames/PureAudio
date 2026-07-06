import pefile
pe = pefile.PE('libs/r8bsrc.dll')

print('Sections:')
for section in pe.sections:
    name = section.Name.decode().rstrip('\x00')
    chars = []
    if section.Characteristics & 0x20: chars.append('CODE')
    if section.Characteristics & 0x40: chars.append('INIT_DATA')
    if section.Characteristics & 0x80: chars.append('UNINIT_DATA')
    if section.Characteristics & 0x20000000: chars.append('EXECUTE')
    if section.Characteristics & 0x40000000: chars.append('READ')
    if section.Characteristics & 0x80000000: chars.append('WRITE')
    print(f'  {name}: VA=0x{section.VirtualAddress:X}, Size=0x{section.SizeOfRawData:X}, VirtSize=0x{section.Misc_VirtualSize:X}')
    print(f'    Flags: {" | ".join(chars)}')

print('\nEntry point:', hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint))
print('Subsystem:', pe.OPTIONAL_HEADER.Subsystem)
print('DLL Characteristics:', hex(pe.OPTIONAL_HEADER.DllCharacteristics))

# Check for TLS callbacks
if hasattr(pe, 'DIRECTORY_ENTRY_TLS'):
    print('\nTLS directory found!')
    tls = pe.DIRECTORY_ENTRY_TLS
    print(f'  AddressOfCallBacks: {[hex(x) for x in tls.struct.AddressOfCallBacks]}')
else:
    print('\nNo TLS directory')

# Check for load config (SEH handlers)
if hasattr(pe, 'DIRECTORY_ENTRY_LOAD_CONFIG'):
    print('\nLoad config found')
else:
    print('\nNo load config')

# Check for CRT initializers
print('\nChecking for CRT sections...')
for section in pe.sections:
    name = section.Name.decode().rstrip('\x00')
    if 'CRT' in name or 'crt' in name or 'C' in name:
        print(f'  Found: {name}')

# Check for .reloc section
print('\nRelocation entries:', len(pe.DIRECTORY_ENTRY_BASERELOC) if hasattr(pe, 'DIRECTORY_ENTRY_BASERELOC') else 0)

# Check if DLL has IMAGE_FILE_DLL flag
print('\nCharacteristics:', hex(pe.FILE_HEADER.Characteristics))
print('Is DLL:', bool(pe.FILE_HEADER.Characteristics & 0x2000))

# Check for delay-load imports
if hasattr(pe, 'DIRECTORY_ENTRY_DELAY_IMPORT'):
    print('\nDelay-load imports:')
    for entry in pe.DIRECTORY_ENTRY_DELAY_IMPORT:
        print(f'  {entry.dll.decode()}')
else:
    print('\nNo delay-load imports')
