import copy,datetime as dt,io,json,pathlib,sqlite3,sys,tempfile,unittest
from decimal import Decimal
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
import api_pricing as prices
import api_usage as api


def usage(inp=100,cache=90,out=10,write=0,reason=3):
    return dict(input_tokens=inp,cached_input_tokens=cache,cache_write_input_tokens=write,
                output_tokens=out,reasoning_output_tokens=reason,total_tokens=inp+out)


class PricingTests(unittest.TestCase):
    def test_actual_api_math_and_reasoning_once(self):
        self.assertEqual(prices.price('gpt-6-astra',usage()),Decimal('.00069'))
        self.assertEqual(prices.price('gpt-6.1-terra',usage()),Decimal('.0000645'))
        self.assertEqual(prices.price('gpt-6-astra',usage(),True),Decimal('.00113'))
    def test_threshold_is_per_request_not_aggregate(self):
        small=dict(model='gpt-6.1-terra',**usage(200000,190000,100))
        a=prices.summarize([small,small]);self.assertEqual(Decimal(a['usd']),Decimal('.04'))
        long=dict(model='gpt-6.1-terra',**usage(272001,272000,100))
        self.assertEqual(prices.summarize([long])['usd'],'0.027952')
        prices.validate_summary(a)
    def test_cache_write_and_unknown_models(self):
        u=usage(100,80,10,10)
        self.assertEqual(prices.price('gpt-6.1-terra',u),Decimal('.0000765'))
        s=prices.summarize([dict(model='future',**u)])
        self.assertIsNone(s['usd']);self.assertEqual(s['unpriced_tokens'],110)
        prices.validate_summary(s)
    def test_remote_tampering_rejected(self):
        s=prices.summarize([dict(model='gpt-6-astra',**usage())]);s['usd']='999'
        with self.assertRaises(ValueError):prices.validate_summary(s)
        for bad in [usage(1,2,0,reason=0),usage(1,0,-1,reason=0)]:
            with self.assertRaises(ValueError):prices.tokens(bad)
    def test_budget_200_and_85_percent(self):
        shares=prices.allocate({'A':'1277.53','B':'23.81','C':'291.91'},200,85)
        self.assertEqual(sum(Decimal(x['budget_usd']) for x in shares.values()),Decimal(170))
        self.assertEqual(Decimal(shares['A']['share_percent']).quantize(Decimal('.01')),Decimal('80.18'))
        self.assertIsNone(prices.allocate({'A':'0','B':None},200,85))


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name)
        self.home=self.root/'codex';self.logs=self.home/'sessions';self.logs.mkdir(parents=True)
        (self.home/'archived_sessions').mkdir();self.data=self.root/'data';self.data.mkdir()
        self.c=api.open_ledger(self.data);self.t=dt.datetime(2026,11,7,12,tzinfo=dt.timezone.utc).timestamp()
        self.file=self.logs/'one.jsonl';self.sid='session-one'
        self.records=[self.event('session_meta',{'id':self.sid,'creator_account_id':'ACCOUNT_SENTINEL'},-20),
                      self.event('turn_context',{'model':'gpt-6.1-terra'},-19)]
    def tearDown(self):self.c.close();self.temp.cleanup()
    def event(self,kind,payload,seconds):return dict(timestamp=api.iso(self.t+seconds),type=kind,payload=payload)
    def write(self):self.file.write_text(''.join(json.dumps(x)+'\n' for x in self.records),encoding='utf-8')
    def scan(self):
        with self.c:return api.scan_file(self.c,self.sid,self.file,None,self.t-86400)
    def test_modern_dedup_incremental_final_and_archive(self):
        one=self.event('token_usage_record',dict(response_id='r1',usage=usage()),-10)
        self.records+=[one,self.event('event_msg',{'type':'token_count','info':{'total_token_usage':usage()}},-9),one]
        self.write();self.scan();self.assertEqual(self.c.execute('select count(*) from events').fetchone()[0],1)
        self.assertFalse(self.scan())
        with self.file.open('a') as f:f.write(json.dumps(self.event('token_usage_record',dict(response_id='r2',usage=usage(200,180,20)),0))+'\n')
        self.scan();self.assertEqual(self.c.execute('select sum(total_tokens) from events').fetchone()[0],330)
        import zstandard
        archive=self.home/'archived_sessions'/'one.jsonl.zst';archive.write_bytes(zstandard.ZstdCompressor().compress(self.file.read_bytes()))
        with self.c:api.scan_file(self.c,self.sid,archive,None,self.t-86400)
        self.assertEqual(self.c.execute('select count(*) from events').fetchone()[0],2)
    def test_legacy_then_new_response_no_double_count(self):
        self.records += [self.event('event_msg',{'type':'token_count','info':{'total_token_usage':usage()}},-10),
                         self.event('event_msg',{'type':'token_count','info':{'total_token_usage':usage()}},-9),
                         self.event('token_usage_record',dict(response_id='r2',usage=usage()),-5),
                         self.event('event_msg',{'type':'token_count','info':{'total_token_usage':usage(200,180,20)}},-4)]
        self.write();self.scan();self.assertEqual(self.c.execute('select sum(total_tokens) from events').fetchone()[0],220)
    def test_partial_line_retry_and_failed_records_do_not_advance(self):
        self.write();self.scan()
        encoded=json.dumps(self.event('token_usage_record',dict(response_id='new',usage=usage()),0))
        with self.file.open('a') as f:f.write(encoded[:-1])
        self.scan();self.assertEqual(self.c.execute('select count(*) from events').fetchone()[0],0)
        with self.file.open('a') as f:f.write('}\n')
        self.scan();self.assertEqual(self.c.execute('select count(*) from events').fetchone()[0],1)
    def test_window_jitter_and_earlier_shared_85_cutoff(self):
        tag,marker=api.account_tags('ACCOUNT_SENTINEL')
        with self.c:
            api._insert(self.c,'r1',self.t-90,self.sid,'gpt-6.1-terra',tag,marker,usage(),'response')
            api._insert(self.c,'r2',self.t-10,self.sid,'gpt-6.1-terra',tag,marker,usage(),'response')
        db=self.data/'records.sqlite';c=sqlite3.connect(db)
        c.execute('create table samples(id integer primary key,time text,quota_json text)')
        start=self.t-1000;end=start+604800
        for i,(when,used,jitter) in enumerate([(self.t-100,84,0),(self.t,85,1)]):
            c.execute('insert into samples values(?,?,?)',(i,api.iso(when),json.dumps({'account_marker':marker,'primary':{'usedPercent':used,'windowDurationMins':10080,'resetsAt':end+jitter}})))
        c.commit();c.close()
        shared=[dict(start=start,scheduled_reset=end,account_tag=tag,first85=self.t-50,percent=85,quota_at=self.t)]
        api.save(self.data/'api-shared-windows.json',{'windows':shared})
        report=api.report(self.c,self.data,self.data,{'complete':True,'files':1,'errors':[]},self.t)
        self.assertEqual(len(report['periods']),1)
        self.assertEqual(report['periods'][0]['at85']['tokens']['total_tokens'],110)
        api.validate_report(report,self.t)
        body=json.dumps(report);self.assertNotIn('ACCOUNT_SENTINEL',body);self.assertNotIn(str(self.file),body)
    def test_incomplete_three_devices_does_not_allocate_everything_to_local(self):
        self.test_window_jitter_and_earlier_shared_85_cutoff()
        report=api.read(self.data/'api-usage-status.json');out=api.combined(self.data,report,self.t)
        self.assertFalse(out['periods'][0]['complete'])
        self.assertNotIn('budget_percent',out['periods'][0]['devices'][0])


if __name__=='__main__':unittest.main()
