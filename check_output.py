import os

paths = [
    'bin/Debug/net8.0-windows/r8bsrc.dll',
    'bin/Debug/net8.0-windows/win-x64/r8bsrc.dll',
    'bin/Release/net8.0-windows/r8bsrc.dll',
    'bin/Release/net8.0-windows/win-x64/r8bsrc.dll',
]
for p in paths:
    exists = os.path.exists(p)
    status = "EXISTS" if exists else "NOT FOUND"
    print(f'{p}: {status}')
    if exists:
        size = os.path.getsize(p)
        print(f'  size: {size} bytes')
