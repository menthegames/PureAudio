import urllib.request
import json

url = "https://api.github.com/repos/unevens/r8brain/releases"
try:
    req = urllib.request.Request(url, headers={"User-Agent": "Python"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read())
    
    print(f"Found {len(data)} releases:")
    for r in data:
        print(f"\n  Tag: {r['tag_name']}")
        print(f"  Name: {r['name']}")
        print(f"  Assets:")
        for a in r.get('assets', []):
            print(f"    - {a['name']} ({a['size']} bytes)")
            print(f"      URL: {a['browser_download_url']}")
except Exception as e:
    print(f"Error: {e}")
