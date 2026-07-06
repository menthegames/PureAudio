import urllib.request
import json

# Check the repo root contents
url = "https://api.github.com/repos/unevens/r8brain/contents"
try:
    req = urllib.request.Request(url, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read())
    
    print("Repository root contents:")
    for item in data:
        print(f"  {item['type']:5} {item['name']:30} {item.get('size', 0)} bytes")
except Exception as e:
    print(f"Error: {e}")

print()

# Check DLL directory
url2 = "https://api.github.com/repos/unevens/r8brain/contents/DLL"
try:
    req = urllib.request.Request(url2, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read())
    
    print("DLL directory contents:")
    for item in data:
        print(f"  {item['type']:5} {item['name']:30} {item.get('size', 0)} bytes")
except Exception as e:
    print(f"DLL dir error: {e}")
