"""Add CPython 3.12's IDNA closure to an isolated package, never a live runtime."""
import argparse,pathlib,shutil,sys,json
p=argparse.ArgumentParser();p.add_argument('--target',required=True);a=p.parse_args()
assert sys.version_info[:2]==(3,12) and sys.maxsize>2**32,'Build requires complete Windows x64 CPython 3.12'
source=pathlib.Path(sys.base_prefix);target=pathlib.Path(a.target).resolve()
repo=pathlib.Path(__file__).resolve().parents[1]
assert target.is_relative_to(repo/'work') and any(x.name.startswith('stage-') for x in target.parents) and (target/'python312.dll').is_file(),'Only an isolated package stage is supported'
closure=['encodings/__init__.py','encodings/aliases.py','encodings/idna.py','stringprep.py']
for name in closure:
    dest=target/'Lib'/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source/'Lib'/name,dest)
native=source/'DLLs/unicodedata.pyd';assert native.is_file()
shutil.copy2(native,target/'DLLs/unicodedata.pyd')
print(json.dumps({'network_codec_closure':closure+['unicodedata.pyd'],'source_python':sys.version.split()[0],'live_runtime_modified':False}))
