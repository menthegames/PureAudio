import urllib.request

url = 'https://raw.githubusercontent.com/unevens/r8brain/master/CDSPResampler.h'
try:
    req = urllib.request.Request(url)
    with urllib.request.urlopen(req) as resp:
        content = resp.read().decode('utf-8')
        print(content)
except Exception as e:
    print(f'Error: {e}')
