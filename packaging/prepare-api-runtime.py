"""Add the hash-pinned CPython 3.12 zstandard wheel to an isolated release stage."""
import argparse,hashlib,json,pathlib,urllib.request,zipfile

p=argparse.ArgumentParser();p.add_argument('--target',required=True);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[1];target=pathlib.Path(a.target).resolve()
assert target.is_relative_to(repo/'work') and any(p.name.startswith('stage-') for p in target.parents) and (target/'python312.dll').is_file()
lock=json.loads((repo/'packaging/zstandard-wheel.json').read_text())
assert lock['url'].startswith('https://files.pythonhosted.org/') and lock['filename'].endswith('cp312-cp312-win_amd64.whl')
cache=repo/'work/dependencies'/lock['filename'];cache.parent.mkdir(parents=True,exist_ok=True)
if not cache.is_file():
    with urllib.request.urlopen(lock['url'],timeout=45) as f:body=f.read(3000000)
    if hashlib.sha256(body).hexdigest()!=lock['sha256']:raise ValueError('Archive reader wheel hash mismatch')
    cache.write_bytes(body)
assert hashlib.sha256(cache.read_bytes()).hexdigest()==lock['sha256']
dest=target/'Lib/site-packages';dest.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(cache) as wheel:
    for item in wheel.infolist():
        path=(dest/item.filename).resolve()
        if not path.is_relative_to(dest.resolve()):raise ValueError('Invalid wheel member')
        if item.is_dir():path.mkdir(parents=True,exist_ok=True)
        else:
            path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(wheel.read(item))
pth=target/'python312._pth';lines=pth.read_text().splitlines()
if 'Lib/site-packages' not in lines:lines.append('Lib/site-packages')
pth.write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(json.dumps({'archive_reader':lock['name'],'version':lock['version'],'sha256':lock['sha256'],'live_runtime_modified':False}))
