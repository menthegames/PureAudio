import struct
import os

def check_dll_bitness(path):
    with open(path, 'rb') as f:
        dos_header = f.read(64)
        if dos_header[:2] != b'MZ':
            return 'Not a PE file'
        pe_offset = struct.unpack('<I', dos_header[60:64])[0]
        f.seek(pe_offset)
        pe_sig = f.read(4)
        if pe_sig != b'PE\x00\x00':
            return 'Not a PE file'
        machine = struct.unpack('<H', f.read(2))[0]
        if machine == 0x8664:
            return '64-bit (x64)'
        elif machine == 0x14c:
            return '32-bit (x86)'
        elif machine == 0xaa64:
            return 'ARM64'
        else:
            return f'Unknown machine type: 0x{machine:04x}'

def list_exports(path):
    """List exported functions from DLL using pefile if available, otherwise use dumpbin"""
    try:
        import pefile
        pe = pefile.PE(path)
        if hasattr(pe, 'DIRECTORY_ENTRY_EXPORT'):
            exports = []
            for exp in pe.DIRECTORY_ENTRY_EXPORT.symbols:
                if exp.name:
                    exports.append(exp.name.decode())
            return exports
        return []
    except ImportError:
        return None

files_to_check = [
    'libs/r8bsrc.dll',
    'r8brain_repo/DLL/Win64/r8bsrc.dll',
    'r8brain_repo/DLL/Win32/r8bsrc.dll',
]

for fpath in files_to_check:
    if os.path.exists(fpath):
        print(f'=== {fpath} ===')
        print(f'  Size: {os.path.getsize(fpath)} bytes')
        print(f'  Bitness: {check_dll_bitness(fpath)}')
        print()
    else:
        print(f'=== {fpath} === NOT FOUND')
        print()

# Also check the .lib files
lib_files = [
    'libs/r8bsrc.lib',
    'r8brain_repo/DLL/Win64/r8bsrc.lib',
    'r8brain_repo/DLL/Win32/r8bsrc.lib',
]

print('=== .lib files ===')
for fpath in lib_files:
    if os.path.exists(fpath):
        print(f'  {fpath}: {os.path.getsize(fpath)} bytes')
    else:
        print(f'  {fpath}: NOT FOUND')
