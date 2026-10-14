"""Opt-in private GitHub usage summaries. Recorder databases stay read-only."""
import argparse, base64, collections, ctypes, datetime as dt, hashlib, json, os
import pathlib, re, shutil, sqlite3, subprocess, sys, time, urllib.error, urllib.parse, urllib.request, uuid

FIELDS=('input_tokens','cached_input_tokens','output_tokens','reasoning_output_tokens','total_tokens')
REPOSITORY=re.compile(r'^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$')
DEVICE=re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
MAX_FILE=750000
UTC=dt.timezone.utc

class SyncError(Exception):
    def __init__(self,code): self.code=code; super().__init__(code)

def now(): return dt.datetime.now(UTC).isoformat()
def stamp(value): return dt.datetime.fromisoformat(value.replace('Z','+00:00')).astimezone(UTC)
def read(path,default=None):
    try: return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError,ValueError): return {} if default is None else default
def save(path,value):
    temp=path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8'); temp.replace(path)
def encoded(value): return (json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':'))+'\n').encode('utf-8')
def clean_label(value): return ''.join(c for c in str(value) if c.isprintable())[:32] or 'Device'
def zero(): return dict.fromkeys(FIELDS,0)
def usage(value):
    value=dict(value)
    if any(type(value.get(k) or 0) is not int or abs(value.get(k) or 0)>9007199254740991 for k in FIELDS): raise SyncError('invalid_token_record')
    result={k:int(value.get(k) or 0) for k in FIELDS}
    if any(v<0 for v in result.values()) or result['total_tokens']!=result['input_tokens']+result['output_tokens'] or result['cached_input_tokens']>result['input_tokens'] or result['reasoning_output_tokens']>result['output_tokens']:
        raise SyncError('invalid_token_record')
    return result

def backend_root(data):
    legacy=read(data/'legacy-source.json').get('root')
    if legacy:
        p=pathlib.Path(legacy)
        if p.is_absolute() and (p/'monitor.py').is_file() and (p/'status.json').is_file(): return p
    return data

def identity(data):
    path=data/'github-sync-device.json'; existing=read(path).get('id')
    if existing and DEVICE.fullmatch(existing): return existing
    value=read(data/'device-id.json').get('id')
    if not isinstance(value,str) or not DEVICE.fullmatch(value): value=str(uuid.uuid4())
    save(path,{'id':value}); return value

def export_records(database,device_id,label,at=None,host='A',days=31):
    """Whitelisted hourly counts and quota fields; no response/task/account IDs."""
    if not DEVICE.fullmatch(device_id): raise SyncError('invalid_device_id')
    at=at or dt.datetime.now(UTC); start=(at-dt.timedelta(days=days)).isoformat()
    if not database.is_file(): raise SyncError('recorder_database_missing')
    c=sqlite3.connect(database.resolve().as_uri()+'?mode=ro',uri=True,timeout=10)
    c.row_factory=sqlite3.Row; c.execute('PRAGMA query_only=ON'); c.execute('BEGIN')
    documents={}; totals=zero(); first=None; last=None; response_count=0
    allrow=c.execute('SELECT MIN(time),MAX(time),COUNT(*) FROM events WHERE host=? AND time<=?',(host,at.isoformat())).fetchone()
    for row in c.execute('SELECT '+','.join(FIELDS)+' FROM events WHERE host=? AND time<=?',(host,at.isoformat())):
        u=usage(row)
        for k in FIELDS: totals[k]+=u[k]
    def day_doc(day):
        if day not in documents:
            documents[day]={'schema':1,'device_id':device_id,'label':clean_label(label),'day':day,'timezone':'UTC',
                            'coverage':'recorded_local_observer_events; not complete account usage','buckets':{},'quota':[]}
        return documents[day]
    for row in c.execute('SELECT time,model,speed,account_id,'+','.join(FIELDS)+' FROM events WHERE host=? AND time>=? ORDER BY time',(host,start)):
        t=stamp(row['time'])
        if t>at: continue
        model=row['model'] or 'unknown'
        if not re.fullmatch(r'[A-Za-z0-9_.:-]{1,80}',model): model='unknown'
        speed=row['speed'] if row['speed'] in ('standard','fast','priority','ultrafast') else 'unspecified'
        account=hashlib.sha256(('UsageObserver-account:'+row['account_id']).encode()).hexdigest()[:24] if row['account_id'] else 'unknown'
        key=(t.strftime('%Y-%m-%dT%H:00:00Z'),model,speed,account)
        buckets=day_doc(t.strftime('%Y-%m-%d'))['buckets']
        bucket=buckets.setdefault(key,{'hour':key[0],'model':model,'speed':speed,'account_tag':account,'responses':0,'tokens':zero()})
        u=usage(row); bucket['responses']+=1
        for k in FIELDS: bucket['tokens'][k]+=u[k]
        response_count+=1; first=first or t.isoformat(); last=t.isoformat()
    latest_quota=None; prior=None
    for row in c.execute('SELECT time,quota_json FROM samples WHERE time>=? ORDER BY id',(start,)):
        q=json.loads(row['quota_json']); p=q.get('primary') or {}; t=stamp(row['time'])
        if t>at or q.get('error') or not isinstance(p.get('usedPercent'),(int,float)) or not 0<=p['usedPercent']<=100: continue
        v={'time':t.isoformat(),'used_percent':p['usedPercent'],'window_minutes':p.get('windowDurationMins'),
           'resets_at':p.get('resetsAt'),'available_resets':q.get('available_resets')}
        # Keep resets/changes plus one sample per five minutes; preserve source timestamp.
        key=(v['used_percent'],v['window_minutes'],v['resets_at'],v['available_resets'],int(t.timestamp())//300)
        latest_quota=v
        if key!=prior: day_doc(t.strftime('%Y-%m-%d'))['quota'].append(v); prior=key
    c.close()
    daily=[]
    for day,d in sorted(documents.items()):
        d['buckets']=list(d['buckets'].values()); subtotal=zero()
        for b in d['buckets']:
            for k in FIELDS: subtotal[k]+=b['tokens'][k]
        daily.append({'day':day,'tokens':subtotal,'responses':sum(b['responses'] for b in d['buckets'])})
        if len(encoded(d))>MAX_FILE: raise SyncError('daily_summary_too_large')
    latest={'schema':1,'device_id':device_id,'label':clean_label(label),'uploaded_at':at.isoformat(),
            'recording_start':allrow[0],'last_recorded_at':allrow[1],'responses':allrow[2],
            'coverage':'local Observer records; unknown missing/cloud activity','totals':totals,
            'latest_quota':latest_quota,'daily':daily}
    return documents,latest

class Blob(ctypes.Structure): _fields_=[('size',ctypes.c_ulong),('data',ctypes.POINTER(ctypes.c_ubyte))]
def unprotect(data):
    if os.name!='nt': raise SyncError('windows_credential_required')
    buffer=ctypes.create_string_buffer(data); source=Blob(len(data),ctypes.cast(buffer,ctypes.POINTER(ctypes.c_ubyte))); target=Blob()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(source),None,None,None,None,1,ctypes.byref(target)):
        raise SyncError('credential_unavailable_for_this_windows_user')
    try: return ctypes.string_at(target.data,target.size).decode('utf-8')
    finally: ctypes.windll.kernel32.LocalFree(target.data)
def credential(data,settings):
    if settings.get('use_git_login'):
        exe=shutil.which('git')
        if not exe: raise SyncError('git_login_unavailable')
        env=dict(os.environ,GCM_INTERACTIVE='never',GCM_GUI_PROMPT='false',GIT_TERMINAL_PROMPT='0')
        p=subprocess.run([exe,'-c','credential.interactive=never','credential','fill'],input='protocol=https\nhost=github.com\n\n',capture_output=True,text=True,encoding='utf-8',errors='replace',env=env,timeout=20,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        values=dict(line.split('=',1) for line in p.stdout.splitlines() if '=' in line)
        if p.returncode or not values.get('password'): raise SyncError('git_login_unavailable')
        return values['password']
    p=data/'github-sync-token.dpapi'
    if not p.is_file(): raise SyncError('github_token_not_configured')
    return unprotect(p.read_bytes())

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs): raise SyncError('unexpected_github_redirect')
class GitHub:
    def __init__(self,repository,token):
        if not REPOSITORY.fullmatch(repository) or '..' in repository: raise SyncError('invalid_repository')
        self.repository=repository; self.base='https://api.github.com/repos/'+repository
        self.token=token; self.opener=urllib.request.build_opener(NoRedirect())
    def request(self,method,suffix='',payload=None,missing=False):
        if not suffix.startswith('/') and suffix: raise SyncError('invalid_api_path')
        url=self.base+suffix
        req=urllib.request.Request(url,data=encoded(payload) if payload is not None else None,method=method,
            headers={'Authorization':'Bearer '+self.token,'Accept':'application/vnd.github+json','User-Agent':'UsageObserver-Private-Sync/1',
                     'X-GitHub-Api-Version':'2022-11-28','Content-Type':'application/json'})
        try:
            with self.opener.open(req,timeout=20) as response:
                body=response.read(2*MAX_FILE+1)
                if len(body)>2*MAX_FILE: raise SyncError('github_response_too_large')
                return json.loads(body)
        except urllib.error.HTTPError as e:
            if e.code==404 and missing: return None
            raise SyncError('github_http_'+str(e.code)) from None
        except (urllib.error.URLError,TimeoutError): raise SyncError('github_network_unavailable') from None
    def check_private(self):
        repo=self.request('GET')
        if repo.get('private') is not True or repo.get('full_name','').lower()!=self.repository.lower(): raise SyncError('private_repository_required')
        if repo.get('archived'): raise SyncError('repository_archived')
        return repo
    def path(self,path):
        if path.startswith('/') or '..' in path.split('/') or '\\' in path: raise SyncError('invalid_file_path')
        return '/contents/'+urllib.parse.quote(path,safe='/')
    def file(self,path): return self.request('GET',self.path(path),missing=True)
    def decoded(self,path):
        f=self.file(path)
        if f is None: return None
        if not isinstance(f,dict) or f.get('encoding')!='base64' or f.get('size',MAX_FILE+1)>MAX_FILE: raise SyncError('invalid_remote_file')
        try: return json.loads(base64.b64decode(f['content']))
        except (ValueError,KeyError): raise SyncError('invalid_remote_json') from None
    def put(self,path,value):
        body=encoded(value)
        if len(body)>MAX_FILE: raise SyncError('upload_too_large')
        for attempt in range(2):
            existing=self.file(path)
            if existing and base64.b64decode(existing.get('content',''))==body: return False
            args={'message':'Update device usage summary','content':base64.b64encode(body).decode()}
            if existing: args['sha']=existing['sha']
            try: self.request('PUT',self.path(path),args); return True
            except SyncError as e:
                if attempt or e.code not in ('github_http_409','github_http_422'): raise

def validate_latest(value,device,at=None):
    if not isinstance(value,dict) or value.get('schema')!=1 or value.get('device_id')!=device or not DEVICE.fullmatch(device): raise SyncError('invalid_remote_summary')
    usage(value.get('totals') or {})
    if not isinstance(value.get('daily'),list) or len(value['daily'])>32: raise SyncError('invalid_remote_history')
    if stamp(value['uploaded_at'])>(at or dt.datetime.now(UTC))+dt.timedelta(minutes=5): raise SyncError('future_remote_summary')
    for day in value['daily']:
        if not re.fullmatch(r'\d{4}-\d{2}-\d{2}',day.get('day','')): raise SyncError('invalid_remote_day')
        usage(day.get('tokens') or {})
    value['label']=clean_label(value.get('label','Device')); return value

def sync_once(data,api=None,at=None):
    settings=read(data/'github-sync-settings.json'); at=at or dt.datetime.now(UTC)
    if not settings.get('enabled'): return {'state':'disabled','updated_at':now()}
    api=api or GitHub(settings.get('repository',''),credential(data,settings))
    repo=api.check_private(); device=identity(data); state=read(data/'github-sync-state.json')
    if state.get('repository')!=api.repository: state={'repository':api.repository,'hashes':{}}
    hashes=state.setdefault('hashes',{}); writes=0; pending=0
    if settings.get('upload_local',True):
        docs,latest=export_records(backend_root(data)/'records.sqlite',device,settings.get('label','Device'),at)
        latest['recording_running']=bool(read(backend_root(data)/'status.json').get('running'))
        for day,doc in sorted(docs.items(),reverse=True):
            path='devices/'+device+'/days/'+day+'.json'; digest=hashlib.sha256(encoded(doc)).hexdigest()
            if hashes.get(path)==digest: continue
            if writes>=4: pending+=1; continue
            api.put(path,doc); hashes[path]=digest; writes+=1
        old=state.get('latest_payload') or {}; comparable=dict(latest); comparable.pop('uploaded_at',None)
        if comparable!=old or time.time()-state.get('latest_upload_epoch',0)>=900:
            api.put('devices/'+device+'/latest.json',latest)
            state['latest_payload']=comparable; state['latest_upload_epoch']=time.time(); writes+=1
    save(data/'github-sync-state.json',state)
    listing=api.file('devices') or []; peers=[]; rejected=0
    if not isinstance(listing,list) or len(listing)>32: raise SyncError('device_list_limit')
    for item in listing:
        candidate=item.get('name','')
        if item.get('type')!='dir' or not DEVICE.fullmatch(candidate): continue
        try:
            summary=api.decoded('devices/'+candidate+'/latest.json')
            if summary is None: continue
            summary=validate_latest(summary,candidate,at)
            summary['online']=(at-stamp(summary['uploaded_at'])).total_seconds()<=1800
            summary['is_local']=candidate==device; peers.append(summary)
        except (SyncError,KeyError,TypeError): rejected+=1
    remote={'schema':1,'repository':api.repository,'updated_at':at.isoformat(),'devices':peers,'rejected':rejected}
    save(data/'github-sync-devices.json',remote)
    return {'state':'ok','updated_at':at.isoformat(),'repository':api.repository,'private_verified':True,
            'devices':len(peers),'writes':writes,'pending_days':pending,'rejected':rejected,
            'mode':'upload_and_read' if settings.get('upload_local',True) else 'read_only'}

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--data',required=True); parser.add_argument('--loop',action='store_true')
    args=parser.parse_args(); data=pathlib.Path(args.data).resolve(); data.mkdir(parents=True,exist_ok=True)
    lock=(data/'github-sync.lock').open('a+b'); lock.seek(0)
    try:
        if os.name=='nt':
            import msvcrt
            msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
    except OSError: return 2
    save(data/'github-sync-process.json',{'pid':os.getpid(),'script':str(pathlib.Path(__file__).resolve())})
    next_run=0
    try:
        while True:
            if (data/'github-sync-stop.request').exists(): break
            settings=read(data/'github-sync-settings.json')
            if not settings.get('enabled'):
                save(data/'github-sync-status.json',{'state':'disabled','updated_at':now()}); break
            if time.time()>=next_run or (data/'github-sync.request').exists():
                (data/'github-sync.request').unlink(missing_ok=True)
                try: status=sync_once(data)
                except SyncError as e: status={'state':'error','code':e.code,'updated_at':now()}
                except Exception as e: status={'state':'error','code':type(e).__name__,'updated_at':now()}
                save(data/'github-sync-status.json',status)
                if not args.loop:
                    print(json.dumps(status)); return 0 if status['state']=='ok' else 1
                next_run=time.time()+(900 if status['state']=='error' else 300)
            time.sleep(5)
    finally:
        (data/'github-sync-process.json').unlink(missing_ok=True)
        lock.close()
    return 0

if __name__=='__main__': sys.exit(main())
