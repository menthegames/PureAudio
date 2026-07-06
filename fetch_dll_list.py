import urllib.request, json

for folder in ['DLL/Win32', 'DLL/Win64']:
    print(f'\n=== {folder} ===')
    url = f'https://api.github.com/repos/unevens/r8brain/contents/{folder}'
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'Python'})
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode('utf-8'))
            for item in data:
                print(f'  {item["type"]}: {item["name"]} ({item.get("size", 0)} bytes)')
    except Exception as e:
        print(f'  Error: {e}')
