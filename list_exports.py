import pefile
pe = pefile.PE('libs/r8bsrc.dll')
print('Exports:')
for exp in pe.DIRECTORY_ENTRY_EXPORT.symbols:
    name = exp.name.decode() if exp.name else '[ordinal]'
    print(f'  {name} (ordinal={exp.ordinal})')
