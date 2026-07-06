import urllib.request
import json

# Check Win64 directory
url = "https://api.github.com/repos/unevens/r8brain/contents/DLL/Win64"
try:
    req = urllib.request.Request(url, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read())
    
    print("DLL/Win64 directory contents:")
    for item in data:
        print(f"  {item['type']:5} {item['name']:30} {item.get('size', 0)} bytes")
        if item['type'] == 'file' and item['name'].endswith('.dll'):
            print(f"    Download URL: {item['download_url']}")
except Exception as e:
    print(f"Error: {e}")

print()

# Also check Win32
url2 = "https://api.github.com/repos/unevens/r8brain/contents/DLL/Win32"
try:
    req = urllib.request.Request(url2, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read())
    
    print("DLL/Win32 directory contents:")
    for item in data:
        print(f"  {item['type']:5} {item['name']:30} {item.get('size', 0)} bytes")
except Exception as e:
    print(f"Win32 error: {e}")
