import copy,datetime as dt,json,pathlib,sqlite3,sys,tempfile,unittest
from decimal import Decimal
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
import api_pricing as prices
import api_usage as api
import github_sync as sync

AT=dt.datetime(2026,11,8,0,tzinfo=dt.timezone.utc)
IDS=['a1111111-1111-4111-8111-111111111111','b1111111-1111-4111-8111-111111111111','c1111111-1111-4111-8111-111111111111']
U=dict(input_tokens=100,cached_input_tokens=90,cache_write_input_tokens=0,output_tokens=10,reasoning_output_tokens=3,total_tokens=110)


class Cloud:
    repository='example/private-usage'
    def __init__(self):self.files={};self.writes=[]
    def check_private(self):return {'private':True}
    def put(self,path,value):self.files[path]=copy.deepcopy(value);self.writes.append(path);return True
    def file(self,path):
        if path=='devices':return [{'type':'dir','name':v} for v in sorted({p.split('/')[1] for p in self.files if p.startswith('devices/')})]
        return None
    def decoded(self,path):return copy.deepcopy(self.files.get(path))


class ThreeDeviceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.cloud=Cloud();self.roots=[]
        tag,marker=api.account_tags('PRIVATE_ACCOUNT_SENTINEL')
        for index,uid in enumerate(IDS):
            root=self.root/uid;root.mkdir();self.roots.append(root)
            api.save(root/'github-sync-device.json',{'id':uid})
            api.save(root/'github-sync-settings.json',{'enabled':True,'repository':self.cloud.repository,'label':'PC '+str(index+1),'upload_local':True})
            c=sqlite3.connect(root/'records.sqlite')
            c.executescript('CREATE TABLE events(host TEXT,time TEXT,model TEXT,speed TEXT,account_id TEXT,input_tokens INTEGER,cached_input_tokens INTEGER,output_tokens INTEGER,reasoning_output_tokens INTEGER,total_tokens INTEGER);CREATE TABLE samples(id INTEGER PRIMARY KEY,time TEXT,quota_json TEXT);')
            c.execute('INSERT INTO events VALUES(?,?,?,?,?,?,?,?,?,?)',('A',api.iso(AT.timestamp()-100),'gpt-6.1-terra','standard','PRIVATE_ACCOUNT_SENTINEL',100,90,10,3,110))
            for n,(seconds,used) in enumerate([(-120,84),(-60+20*index,85)]):
                q={'account_marker':marker,'primary':{'usedPercent':used,'windowDurationMins':10080,'resetsAt':AT.timestamp()-3600+604800}}
                c.execute('INSERT INTO samples VALUES(?,?,?)',(n,api.iso(AT.timestamp()+seconds),json.dumps(q)))
            c.commit();c.close()
            ledger=api.open_ledger(root)
            with ledger:
                api._insert(ledger,'first:'+uid,AT.timestamp()-100,uid,'gpt-6.1-terra',tag,marker,U,'response')
                api._insert(ledger,'later:'+uid,AT.timestamp()-50,uid,'gpt-6.1-terra',tag,marker,U,'response')
            ledger.close();self.refresh(root)
    def tearDown(self):self.temp.cleanup()
    def refresh(self,root):
        c=api.open_ledger(root)
        try:return api.report(c,root,root,{'complete':True,'files':1,'errors':[]},AT.timestamp())
        finally:c.close()
    def connect_all(self):
        for root in self.roots:sync.sync_once(root,self.cloud,AT)
    def test_common_85_cutoff_converges_and_allocations_sum_to_170(self):
        self.connect_all()
        mid=api.read(self.roots[1]/'api-combined.json')
        self.assertTrue(any(r['state']=='aligning_cutoff' for p in mid['periods'] if p['mode']=='at85' for r in p['devices']))
        for root in self.roots:self.refresh(root)
        self.connect_all();sync.sync_once(self.roots[0],self.cloud,AT)
        final=api.read(self.roots[0]/'api-combined.json')
        view=next(p for p in final['periods'] if p['mode']=='at85')
        self.assertTrue(view['complete']);self.assertEqual(len(view['devices']),3)
        self.assertEqual(Decimal(view['total_usd']),prices.price('gpt-6.1-terra',U)*3)
        self.assertEqual(sum(Decimal(r['budget_usd']) for r in view['devices']),Decimal(170))
        self.assertLessEqual(abs(sum(Decimal(r['budget_percent']) for r in view['devices'])-Decimal(85)),Decimal('.01'))
        self.assertLessEqual(abs(sum(Decimal(r['share_percent']) for r in view['devices'])-Decimal(100)),Decimal('.01'))
        body=json.dumps(self.cloud.files);self.assertNotIn('PRIVATE_',body);self.assertNotIn(str(self.root),body)
        self.assertEqual({p.split('/')[1] for p in self.cloud.writes},set(IDS))
    def test_read_only_viewer_is_not_a_fourth_billed_device(self):
        self.connect_all()
        for root in self.roots:self.refresh(root)
        self.connect_all()
        viewer=self.root/'viewer';viewer.mkdir();uid='d1111111-1111-4111-8111-111111111111'
        api.save(viewer/'github-sync-device.json',{'id':uid})
        api.save(viewer/'github-sync-settings.json',{'enabled':True,'repository':self.cloud.repository,'upload_local':False})
        prior=len(self.cloud.writes);sync.sync_once(viewer,self.cloud,AT)
        self.assertEqual(len(self.cloud.writes),prior)
        local={'device_id':uid,'label':'viewer','generated_at':AT.isoformat(),'periods':[]}
        result=api.combined(viewer,local,AT.timestamp())
        self.assertEqual(len(result['periods'][0]['devices']),3)
        self.assertNotIn(uid,[r['device_id'] for r in result['periods'][0]['devices']])
    def test_stale_api_is_not_refreshed_by_new_upload_timestamp(self):
        self.connect_all();root=self.roots[0]
        cached=api.read(root/'github-sync-devices.json')
        other=next(d for d in cached['devices'] if d['device_id']!=IDS[0]) if len(cached['devices'])>1 else None
        sync.sync_once(root,self.cloud,AT);cached=api.read(root/'github-sync-devices.json')
        other=next(d for d in cached['devices'] if d['device_id']!=IDS[0])
        other['api_usage']['generated_at']=api.iso(AT.timestamp()-1900);other['uploaded_at']=AT.isoformat()
        api.save(root/'github-sync-devices.json',cached)
        result=api.combined(root,api.read(root/'api-usage-status.json'),AT.timestamp())
        self.assertIn('stale',[d['state'] for p in result['periods'] if p['mode']=='full' for d in p['devices']])
    def test_corrupt_remote_api_amount_rejected(self):
        self.connect_all()
        key='devices/'+IDS[1]+'/latest.json';bad=self.cloud.files[key]
        bad['api_usage']['periods'][0]['full']['usd']='999'
        result=sync.sync_once(self.roots[0],self.cloud,AT)
        self.assertEqual(result['rejected'],1)
        self.assertNotIn(IDS[1],[d['device_id'] for d in api.read(self.roots[0]/'github-sync-devices.json')['devices']])
    def test_aligned_historical_85_snapshot_survives_offline_devices(self):
        self.connect_all()
        for root in self.roots:self.refresh(root)
        self.connect_all();sync.sync_once(self.roots[0],self.cloud,AT)
        root=self.roots[0]
        result=api.combined(root,api.read(root/'api-usage-status.json'),AT.timestamp()+1900)
        snapshot=next(p for p in result['periods'] if p['mode']=='at85')
        self.assertTrue(snapshot['complete']);self.assertTrue(all(d['state']=='saved_snapshot' for d in snapshot['devices']))
        self.assertEqual(sum(Decimal(r['budget_usd']) for r in snapshot['devices']),Decimal(170))


if __name__=='__main__':unittest.main()
