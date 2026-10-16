"""Publish only a verified complete package to the explicitly supplied owned draft."""
import os,pathlib,json,hashlib,urllib.request,urllib.parse,urllib.error
root=pathlib.Path(__file__).resolve().parents[1]
repo=os.environ['GITHUB_REPOSITORY']; assert repo=='lys20001205/usage-observer'
token=os.environ['GH_TOKEN']; source=os.environ['GITHUB_SHA']; release_id=int(os.environ['RELEASE_ID'])
out=pathlib.Path(os.environ['RUNNER_TEMP'])/'release'
build=json.loads((root/'work/release-1.0.4.14/build-evidence.json').read_text(encoding='utf-8-sig'))
metadata=json.loads((out/'release-metadata.json').read_text(encoding='utf-8-sig'))
base='https://api.github.com/repos/'+repo
def request(method,url,payload=None,file=None):
    parsed=urllib.parse.urlparse(url)
    assert parsed.scheme=='https' and parsed.hostname in ('api.github.com','uploads.github.com')
    headers={'Authorization':'Bearer '+token,'User-Agent':'UsageObserver-CI-Publisher','Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'}
    body=None
    if payload is not None:body=json.dumps(payload).encode();headers['Content-Type']='application/json'
    if file is not None:body=file.read_bytes();headers['Content-Type']='application/octet-stream'
    with urllib.request.urlopen(urllib.request.Request(url,data=body,headers=headers,method=method),timeout=300) as r:
        data=r.read();return json.loads(data) if data else {}
r=request('GET',base+'/releases/'+str(release_id))
assert r['draft'] and r['author']['login'] in ('lys20001205','github-actions[bot]')
assert r['tag_name']==metadata['tag_name'] or r['tag_name'].startswith('untagged-')
metadata['target_commitish']=source
r=request('PATCH',base+'/releases/'+str(release_id),metadata)
assert r['draft'] and r['tag_name']==metadata['tag_name'] and r['target_commitish']==source
files=[('UsageObserver-Setup.exe',build['setup_sha256']),('UsageObserver-Windows-x64.zip',build['zip_sha256'])]
existing=request('GET',base+'/releases/'+str(release_id)+'/assets')
(out/'prior-unpublished-assets.json').write_text(json.dumps(existing,indent=2),encoding='utf-8')
for asset in existing:
    assert asset['name'] in dict(files) and asset['uploader']['login'] in ('lys20001205','github-actions[bot]')
    request('DELETE',base+'/releases/assets/'+str(asset['id']))
verified=[]
for name,digest in files:
    path=out/name;assert hashlib.sha256(path.read_bytes()).hexdigest()==digest
    url=r['upload_url'].split('{')[0];assert urllib.parse.urlparse(url).path.startswith('/repos/'+repo+'/releases/')
    asset=request('POST',url+'?'+urllib.parse.urlencode({'name':name}),file=path)
    assert asset['size']==path.stat().st_size and asset.get('digest')=='sha256:'+digest
    verified.append({'name':name,'bytes':asset['size'],'sha256':digest})
    print('Verified release asset: '+name,flush=True)
latest=request('GET',base+'/releases/latest')
def version(tag):return tuple(int(x) for x in tag.lstrip('v').split('.'))
assert version(latest['tag_name'])<=version(metadata['tag_name']),'A newer stable release exists; keep this draft and do not replace latest'
metadata.update(draft=False,make_latest='true')
r=request('PATCH',base+'/releases/'+str(release_id),metadata)
assert not r['draft'] and r['tag_name']==metadata['tag_name']
result={'published':True,'release_id':release_id,'url':r['html_url'],'source_commit':source,'assets':verified}
(out/'published-release.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result),flush=True)
