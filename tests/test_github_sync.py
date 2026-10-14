import unittest, tempfile, pathlib, sqlite3, json, datetime as dt, sys, copy, hashlib
import base64, ctypes, os
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
import github_sync as sync

DEVICE='a1111111-1111-4111-8111-111111111111'
OTHER='c1111111-1111-4111-8111-111111111111'
AT=dt.datetime(2026,10,14,12,tzinfo=dt.timezone.utc)
class FakeAPI:
    repository='lys20001205/usage-observer-data'
    def __init__(self,private=True): self.private=private;self.writes=[];self.files={};self.fail=False
    def check_private(self):
        if not self.private: raise sync.SyncError('private_repository_required')
        return {'private':True}
    def put(self,path,value):
        if self.fail: raise sync.SyncError('github_network_unavailable')
        self.writes.append(path);self.files[path]=copy.deepcopy(value)
    def file(self,path):
        if path=='devices': return [{'type':'dir','name':DEVICE},{'type':'dir','name':OTHER}]
        return None
    def decoded(self,path): return self.files.get(path)

class Tests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name)
        self.db=self.root/'records.sqlite';c=sqlite3.connect(self.db)
        c.executescript('CREATE TABLE events(host TEXT,time TEXT,model TEXT,speed TEXT,account_id TEXT,input_tokens INTEGER,cached_input_tokens INTEGER,output_tokens INTEGER,reasoning_output_tokens INTEGER,total_tokens INTEGER,cwd TEXT,session_id TEXT);CREATE TABLE samples(id INTEGER PRIMARY KEY,time TEXT,quota_json TEXT);')
        for host in ('A','B'):
            c.execute('INSERT INTO events VALUES(?,?,?,?,?,?,?,?,?,?,?,?)',(host,'2026-10-14T10:01:00+00:00','gpt-6.1-sol',None,'PRIVATE_ACCOUNT_SENTINEL',100,90,10,3,110,'PRIVATE_PATH_SENTINEL','PRIVATE_TASK_SENTINEL'))
        for n,used in enumerate((60,0,1)):
            time='2026-10-14T10:0'+str(n)+':00+00:00'
            q={'primary':{'usedPercent':used,'windowDurationMins':10080,'resetsAt':1792472267+n},'available_resets':2,'credit_balance':'PRIVATE_BALANCE_SENTINEL'}
            c.execute('INSERT INTO samples VALUES(?,?,?)',(n,time,json.dumps(q)))
        c.commit();c.close()
        sync.save(self.root/'github-sync-device.json',{'id':DEVICE})
        sync.save(self.root/'github-sync-settings.json',{'enabled':True,'repository':FakeAPI.repository,'label':'C','upload_local':True})
    def tearDown(self): self.temp.cleanup()
    def test_private_payload_and_local_only(self):
        before=hashlib.sha256(self.db.read_bytes()).hexdigest()
        docs,latest=sync.export_records(self.db,DEVICE,'C',AT)
        body=json.dumps([docs,latest]);self.assertNotIn('PRIVATE_',body)
        self.assertEqual(latest['totals']['total_tokens'],110)
        self.assertEqual(docs['2026-10-14']['buckets'][0]['tokens']['cached_input_tokens'],90)
        self.assertEqual([q['used_percent'] for q in docs['2026-10-14']['quota']],[60,0,1])
        self.assertEqual(before,hashlib.sha256(self.db.read_bytes()).hexdigest())
    def test_sync_idempotence(self):
        api=FakeAPI();one=sync.sync_once(self.root,api,AT)
        self.assertEqual(one['writes'],2);self.assertEqual(one['devices'],1)
        two=sync.sync_once(self.root,api,AT)
        self.assertEqual(two['writes'],0)
    def test_public_repository_never_uploaded(self):
        api=FakeAPI(False)
        with self.assertRaises(sync.SyncError): sync.sync_once(self.root,api,AT)
        self.assertEqual(api.writes,[])
    def test_failed_upload_not_acknowledged(self):
        api=FakeAPI();api.fail=True
        with self.assertRaises(sync.SyncError): sync.sync_once(self.root,api,AT)
        self.assertFalse((self.root/'github-sync-state.json').exists())
        api.fail=False;self.assertEqual(sync.sync_once(self.root,api,AT)['writes'],2)
    def test_reader_does_not_need_database_or_upload(self):
        settings=sync.read(self.root/'github-sync-settings.json');settings['upload_local']=False;sync.save(self.root/'github-sync-settings.json',settings)
        self.db.unlink();api=FakeAPI();result=sync.sync_once(self.root,api,AT)
        self.assertEqual(result['mode'],'read_only');self.assertEqual(api.writes,[])
    def test_offline_peer_keeps_counts(self):
        api=FakeAPI();_,other=sync.export_records(self.db,OTHER,'C',AT-dt.timedelta(hours=1))
        api.files['devices/'+OTHER+'/latest.json']=other
        sync.sync_once(self.root,api,AT)
        peers=sync.read(self.root/'github-sync-devices.json')['devices'];peer=next(x for x in peers if x['device_id']==OTHER)
        self.assertFalse(peer['online']);self.assertEqual(peer['totals']['total_tokens'],110)
    def test_invalid_remote_rejected(self):
        api=FakeAPI();api.files['devices/'+OTHER+'/latest.json']={'schema':1,'device_id':OTHER,'totals':{'input_tokens':1,'output_tokens':1,'total_tokens':999}}
        result=sync.sync_once(self.root,api,AT);self.assertEqual(result['rejected'],1)
    def test_source_preserves_legacy_routing(self):
        backend=self.root/'old';backend.mkdir();(backend/'monitor.py').touch();(backend/'status.json').write_text('{}')
        sync.save(self.root/'legacy-source.json',{'root':str(backend)});self.assertEqual(sync.backend_root(self.root),backend)
    def test_invalid_tokens_and_path(self):
        with self.assertRaises(sync.SyncError): sync.usage({'input_tokens':2,'cached_input_tokens':3,'total_tokens':2})
        api=sync.GitHub(FakeAPI.repository,'fictional')
        with self.assertRaises(sync.SyncError):api.path('../private')
        with self.assertRaises(sync.SyncError):sync.GitHub('owner/../other','fictional')
    def test_redirect_does_not_forward_credentials(self):
        with self.assertRaises(sync.SyncError):sync.NoRedirect().redirect_request(None,None,None,None,None,'https://other.example')
    def test_network_codec_closure(self):
        self.assertEqual('api.github.com'.encode('idna'),b'api.github.com')
        import stringprep,unicodedata
        self.assertEqual(unicodedata.ucd_3_2_0.normalize('NFC','Github'),'Github')
    def test_conflicting_put_refetches_current_sha_once(self):
        class Conflict(sync.GitHub):
            def __init__(self): super().__init__(FakeAPI.repository,'fictional');self.reads=0;self.puts=0
            def file(self,path):
                self.reads+=1;return {'sha':'new' if self.reads>1 else 'old','content':base64.b64encode(b'{}').decode()}
            def request(self,method,suffix='',payload=None,missing=False):
                self.puts+=1
                if self.puts==1: raise sync.SyncError('github_http_409')
                self.seen=payload['sha'];return {}
        api=Conflict();self.assertTrue(api.put('devices/'+DEVICE+'/latest.json',{'new':True}))
        self.assertEqual((api.puts,api.reads,api.seen),(2,2,'new'))
    @unittest.skipUnless(os.name=='nt','Windows user credential protection')
    def test_windows_user_credential_roundtrip(self):
        plain=b'fictional-test-token';buffer=ctypes.create_string_buffer(plain)
        source=sync.Blob(len(plain),ctypes.cast(buffer,ctypes.POINTER(ctypes.c_ubyte)));target=sync.Blob()
        self.assertTrue(ctypes.windll.crypt32.CryptProtectData(ctypes.byref(source),'UsageObserver test',None,None,None,1,ctypes.byref(target)))
        try: protected=ctypes.string_at(target.data,target.size)
        finally: ctypes.windll.kernel32.LocalFree(target.data)
        self.assertNotIn(plain,protected);self.assertEqual(sync.unprotect(protected),plain.decode())

if __name__=='__main__':unittest.main()
