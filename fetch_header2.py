import urllib.request, json

# Get CDSPResampler.h
url = 'https://raw.githubusercontent.com/unevens/r8brain/master/CDSPResampler.h'
try:
    req = urllib.request.Request(url)
    with urllib.request.urlopen(req) as resp:
        content = resp.read().decode('utf-8')
        print(content[:8000])
except Exception as e:
    print(f'Error: {e}')

print('\n\n========== DLL folder ==========')
url2 = 'https://api.github.com/repos/unevens/r8brain/contents/DLL'
try:
    req = urllib.request.Request(url2, headers={'User-Agent': 'Python'})
    with urllib.request.urlopen(req) as resp:
        data = json.loads(resp.read().decode('utf-8'))
        for item in data:
            print(f'{item["type"]}: {item["name"]}')
except Exception as e:
    print(f'Error: {e}')
