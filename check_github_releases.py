import requests

url = 'https://api.github.com/repos/unevens/r8brain/releases'
r = requests.get(url, timeout=15)
if r.status_code == 200:
    releases = r.json()
    for rel in releases:
        print(f'Release: {rel["tag_name"]} - {rel["name"]}')
        for asset in rel.get('assets', []):
            print(f'  Asset: {asset["name"]} ({asset["size"]} bytes)')
        print()
else:
    print(f'Status: {r.status_code}')
    print(r.text[:500])
