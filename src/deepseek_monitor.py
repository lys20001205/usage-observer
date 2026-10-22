"""Account wallet snapshots and provider-reported DSH usage; no inference calls."""
import json,os,pathlib,subprocess,datetime as dt,decimal,csv,base64,gzip
from portable import DATA,APP,detect_dsh
BASE=DATA
NODE=APP/'runtime/node/node.exe'
MODULES=APP/'node_modules'
from machine_time import local_day_start_utc, format_local
def now():return dt.datetime.now(dt.timezone.utc).isoformat()
def atomic(path,data):
 temp=path.with_suffix(path.suffix+'.tmp');temp.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(path)
class DeepSeekMonitor:
 def __init__(self,connection,codex_remote_class):
  self.c=connection;self.state_path=BASE/'deepseek-state.json'
  self.state=json.loads(self.state_path.read_text(encoding='utf-8')) if self.state_path.exists() else {'start':now(),'cursors':{'A':{},'B':{}}}
  self.c.executescript('''CREATE TABLE IF NOT EXISTS deepseek_events(host TEXT,event_id TEXT,session_id TEXT,time TEXT,model TEXT,provider TEXT,input_tokens INTEGER,uncached_input_tokens INTEGER,cached_input_tokens INTEGER,output_tokens INTEGER,reasoning_output_tokens INTEGER,total_tokens INTEGER,seq INTEGER,PRIMARY KEY(host,event_id));
  CREATE TABLE IF NOT EXISTS deepseek_balances(id INTEGER PRIMARY KEY,time TEXT,account_fingerprint TEXT,currency TEXT,topped_up_balance TEXT,granted_balance TEXT,total_balance TEXT);
  CREATE TABLE IF NOT EXISTS deepseek_health(id INTEGER PRIMARY KEY,time TEXT,data_json TEXT);''')
 def since(self):return local_day_start_utc(6).isoformat()
 def read_local(self):
  args={'home':str(detect_dsh()),'since':self.since(),'cursors':self.state['cursors']['A'],'balance':True,'modules':str(MODULES)}
  p=subprocess.run([str(NODE),str(APP/'deepseek-reader.cjs')],input=json.dumps(args),capture_output=True,text=True,encoding='utf-8',timeout=45,creationflags=subprocess.CREATE_NO_WINDOW)
  result=json.loads(p.stdout)
  if result.get('host','').lower()!=os.environ.get('COMPUTERNAME','').lower():raise RuntimeError('DeepSeek local identity mismatch')
  return result
 def record(self,results):
  health={};stamp=now();balance=None
  columns=['host','event_id','session_id','time','model','provider','input_tokens','uncached_input_tokens','cached_input_tokens','output_tokens','reasoning_output_tokens','total_tokens','seq']
  for host,result in results.items():
   if isinstance(result,Exception):health[host]={'status':'unavailable','error':type(result).__name__};continue
   if result.get('status') in ['ok','parse_errors']:
    for e in result['events']:
     values=[host]+[e.get(k) for k in columns[1:]]
     self.c.execute('INSERT INTO deepseek_events VALUES('+','.join('?'*len(values))+') ON CONFLICT(host,event_id) DO UPDATE SET '+','.join(k+'=excluded.'+k for k in columns[2:])+' WHERE excluded.seq>=deepseek_events.seq',values)
    self.state['cursors'][host]=result['cursors']
   health[host]={k:result.get(k) for k in ['status','parse_errors','partial_frames','changed_files','error']}
   if host=='A':balance=result.get('balance')
  if balance and balance.get('status')=='ok':
   for row in balance['wallets']:
    top=decimal.Decimal(row['topped_up_balance']);bonus=decimal.Decimal(row['granted_balance'])
    self.c.execute('INSERT INTO deepseek_balances(time,account_fingerprint,currency,topped_up_balance,granted_balance,total_balance) VALUES(?,?,?,?,?,?)',(balance['sampled_at'],balance['account_fingerprint'],row['currency'],str(top),str(bonus),str(top+bonus)))
  health['balance']={'status':(balance or {}).get('status','unavailable'),'error':(balance or {}).get('error')}
  self.c.execute('INSERT INTO deepseek_health(time,data_json) VALUES(?,?)',(stamp,json.dumps(health)));self.c.commit();atomic(self.state_path,self.state);self.export(health,stamp)
 def export(self,health,stamp):
  today=local_day_start_utc().isoformat()
  totals={host:int(self.c.execute('SELECT coalesce(sum(total_tokens),0) FROM deepseek_events WHERE host=? AND time>=?',(host,today)).fetchone()[0]) for host in ['A','B']}
  since_totals={host:int(self.c.execute('SELECT coalesce(sum(total_tokens),0) FROM deepseek_events WHERE host=? AND time>=?',(host,self.state['start'])).fetchone()[0]) for host in ['A','B']}
  last=self.c.execute('SELECT * FROM deepseek_balances ORDER BY id DESC LIMIT 1').fetchone();wallets=[]
  if last:
   currencies=[r[0] for r in self.c.execute('SELECT DISTINCT currency FROM deepseek_balances WHERE account_fingerprint=?',(last['account_fingerprint'],))]
   for currency in currencies:
    history=list(self.c.execute('SELECT * FROM deepseek_balances WHERE account_fingerprint=? AND currency=? ORDER BY id',(last['account_fingerprint'],currency)))
    decreases=sum((max(decimal.Decimal(a['total_balance'])-decimal.Decimal(b['total_balance']),decimal.Decimal(0)) for a,b in zip(history,history[1:])),decimal.Decimal(0))
    today_history=[r for r in history if r['time']>=today]
    today_decreases=sum((max(decimal.Decimal(a['total_balance'])-decimal.Decimal(b['total_balance']),decimal.Decimal(0)) for a,b in zip(today_history,today_history[1:])),decimal.Decimal(0))
    row=dict(history[-1]);row.update(observed_decrease=str(decreases),today_observed_decrease=str(today_decreases));wallets.append(row)
  status={'recording_started_at':self.state['start'],'last_sample_at':stamp,'health':health,'wallets':wallets,'today_tokens':totals,'recorded_tokens':since_totals,'token_coverage':'provider-reported local DSH logs; other clients and accounts are not covered','balance_coverage':'DSH login wallet, account-wide; decreases are observed net changes, not an itemized bill'}
  atomic(BASE/'deepseek-status.json',status)
  for filename,query in [('deepseek-usage.csv','SELECT * FROM deepseek_events ORDER BY time'),('deepseek-balances.csv','SELECT * FROM deepseek_balances ORDER BY id')]:
   cursor=self.c.execute(query)
   with (BASE/filename).open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.writer(f);writer.writerow([x[0] for x in cursor.description]);writer.writerows(cursor)
  text='# DeepSeek 账户与 DSH 用量记录\n\n记录起点：'+format_local(self.state['start'])+'。\n\n最后采样：'+format_local(stamp)+'。\n\n'
  for row in wallets:text+=row['currency']+' 总余额 '+row['total_balance']+'；充值余额 '+row['topped_up_balance']+'；赠送余额 '+row['granted_balance']+'；记录期间观察到净减少 '+row['observed_decrease']+'。\n\n'
  text+='今日本机 DSH 已记录 token：'+str(totals['A'])+'。\n\n余额健康状态：'+health['balance']['status']+'；本机 '+health['A']['status']+'。\n\n余额为账号共享钱包；token 只覆盖本机所选 DSH 日志目录，不包括其他程序/API Key/云端。净减少可能含赠送余额到期，并受充值与结算延迟影响；不能直接当作完整账单。读取失败时保留上次余额并标记异常。缓存输入已计入总输入，推理输出包含在输出中。只保存响应元数据、金额及时间，不保存密钥、登录令牌、提示词或源码。\n'
  (BASE/'deepseek-report.md').write_text(text,encoding='utf-8')
