"""Local account recorder plus private-LAN metadata discovery; no inference."""
import argparse,concurrent.futures,datetime as dt,json,os,pathlib,sqlite3,subprocess,time,msvcrt,csv
from portable import DATA,APP,settings,detect_codex,detect_dsh,lan_config,codex_login_marker
from quota_core import RateReader,quota_error,database,ingest,last_quota,write_json,now
from collector import collect
from deepseek_monitor import DeepSeekMonitor
from machine_time import format_local
HOST=os.environ.get('COMPUTERNAME','')
ORIGINAL_CODEX_HOME=os.environ.get('CODEX_HOME')
def safe_read(path):
 try:return json.loads(path.read_text(encoding='utf-8-sig'))
 except (OSError,ValueError):return {}
def export(c,state,health,quota):
 best={};quota_time=None
 for row in c.execute('SELECT time,quota_json FROM samples ORDER BY id DESC'):
  candidate=json.loads(row['quota_json'])
  if candidate.get('account_marker')==state.get('account_marker')and not candidate.get('error')and(candidate.get('primary')or{}).get('usedPercent')is not None:best=candidate;quota_time=row['time'];break
 used=(best.get('primary') or {}).get('usedPercent')
 totals={row[0]:row[1] for row in c.execute('SELECT host,sum(total_tokens) FROM events GROUP BY host')}
 stamp=now();status={'running':state['running'],'pid':os.getpid(),'recording_started_at':state['start'],'last_sample_at':stamp,'quota_sample_at':quota_time,'quota_stale':bool(quota.get('error')) or (quota.get('primary') or {}).get('usedPercent') is None,'quota_error':quota.get('error_details'),'interval_seconds':state['interval'],'used_percent':used,'remaining_percent':100-used if used is not None else None,'recorded_tokens':{'A':totals.get('A',0),'B':0},'health':health,'quota_is_account_wide':True,'hostname':HOST}
 status.update(primary_resets_at=(best.get('primary') or {}).get('resetsAt'),primary_window_minutes=(best.get('primary') or {}).get('windowDurationMins'),secondary_resets_at=(best.get('secondary') or {}).get('resetsAt'),secondary_window_minutes=(best.get('secondary') or {}).get('windowDurationMins'))
 write_json(DATA/'status.json',status)
 ds=safe_read(DATA/'deepseek-status.json');wallets=ds.get('wallets',[])
 # Only explicitly allowlisted aggregates cross the network. No IDs, paths or credentials.
 summary={'hostname':HOST,'sampled_at':stamp,'recording_started_at':state['start'],'running':state['running'],'remaining_percent':status['remaining_percent'],'quota_stale':status['quota_stale'],'quota_sample_at':quota_time,'codex_tokens':totals.get('A',0),'codex_health':health['A']['status'],'dsh_today_tokens':ds.get('today_tokens',{}).get('A',0),'dsh_health':ds.get('health',{}).get('A',{}).get('status','unavailable'),'balance_health':ds.get('health',{}).get('balance',{}).get('status','unavailable'),'wallets':[{k:w.get(k) for k in ['currency','total_balance','time']} for w in wallets]}
 summary.update({k:status[k] for k in ['primary_resets_at','primary_window_minutes','secondary_resets_at','secondary_window_minutes']})
 write_json(DATA/'lan-local.json',summary)
 rows=c.execute('SELECT response_id,time,session_id,model,input_tokens,cached_input_tokens,output_tokens,reasoning_output_tokens,total_tokens FROM events ORDER BY time')
 with (DATA/'tasks.csv').open('w',encoding='utf-8-sig',newline='')as f:
  writer=csv.writer(f);writer.writerow([r[0] for r in rows.description]);writer.writerows(rows)
 rows=c.execute('SELECT time,quota_json,health_json FROM samples ORDER BY id')
 with (DATA/'timeline.csv').open('w',encoding='utf-8-sig',newline='')as f:
  writer=csv.writer(f);writer.writerow(['time','used_percent','quota_error','local_health'])
  for row in rows:
   q=json.loads(row[1]);writer.writerow([row[0],(q.get('primary')or{}).get('usedPercent'),q.get('error'),json.loads(row[2])['A']['status']])
 lines=['# 本机 Codex 与 DSH 用量','',f'设备：{HOST}；记录起点：{format_local(state["start"])}',f'本机 Codex 已记录 token：{totals.get("A",0)}；账号额度剩余：{status["remaining_percent"]}%；额度状态：'+('上次结果，等待更新' if status['quota_stale'] else '正常'),'', '局域网只交换配对设备的金额和 token 汇总，不交换登录信息、任务 ID、对话或源码。每台电脑的记录起点可能不同；离线设备不会算成零。','同一账号的额度/余额是共享快照，不能相加；不同账号也不能折算为机器消耗百分点。历史未安装/未运行本程序的电脑无法追溯。','Codex token 包含本机所选日志目录中的账户；本机切换登录不会把历史 token 自动归给新账户。','DSH token 来自 deepseek-account provider；其他 API Key/外部客户端不在范围内。余额净减少并非逐笔账单。','','当前路径检测：'+json.dumps({'codex_exe':str(detect_codex()) if detect_codex()else None,'codex_home':os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex')),'dsh_home':str(detect_dsh())},ensure_ascii=False)]
 lines.insert(3,'最后采样：'+format_local(stamp))
 (DATA/'report.md').write_text('\n'.join(lines),encoding='utf-8')
 # Persist peer observations independently; never add shared quota percentages or wallets.
 lan=safe_read(DATA/'lan-status.json')
 for peer in lan.get('devices',[]):
  if peer.get('paired')and peer.get('summary'):
   s=peer['summary'];c.execute('INSERT OR IGNORE INTO peer_samples(device_id,time,data_json)VALUES(?,?,?)',(peer['id'],s.get('sampled_at'),json.dumps(s)))
 c.commit()
def main():
 parser=argparse.ArgumentParser();parser.add_argument('--interval',type=int,default=60);parser.add_argument('--cycles',type=int);parser.add_argument('--offline',action='store_true');a=parser.parse_args()
 if a.interval<15:raise SystemExit('Minimum interval 15 seconds')
 lock=(DATA/'monitor.lock').open('a+b');lock.seek(0)
 try:msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
 except OSError:raise SystemExit('Recorder already running')
 state=safe_read(DATA/'state.json')or{'start':now(),'cursors':{'A':{},'B':{}},'sample_number':0}
 state.update(interval=a.interval,running=True);write_json(DATA/'state.json',state)
 (DATA/'stop.request').unlink(missing_ok=True);(DATA/'monitor.pid').write_text(str(os.getpid()))
 c=database();c.executescript('CREATE TABLE IF NOT EXISTS peer_samples(device_id TEXT,time TEXT,data_json TEXT,PRIMARY KEY(device_id,time));')
 rate=None;deepseek=DeepSeekMonitor(c,None);pool=concurrent.futures.ThreadPoolExecutor(max_workers=2);lan=None;lan_signature=None;cycles=0;config_stamp=None
 health={'A':{'status':'unavailable'},'B':{'status':'ok'},'classification':'本机记录；共享额度不能换算为机器百分点'};quota={}
 try:
  while not(DATA/'stop.request').exists():
   tick=time.monotonic();config=settings();stamp=json.dumps(config,sort_keys=True);config_changed=stamp!=config_stamp
   if config_changed:
    if rate:rate.close();rate=None
    if config.get('codex_home'):os.environ['CODEX_HOME']=config['codex_home']
    elif ORIGINAL_CODEX_HOME:os.environ['CODEX_HOME']=ORIGINAL_CODEX_HOME
    else:os.environ.pop('CODEX_HOME',None)
    if config_stamp is not None:state['cursors']['A']={};deepseek.state['cursors']['A']={}
    config_stamp=stamp
   proposed=lan_config()if cycles%5==0 or lan is None or config_changed else None
   signature=json.dumps(proposed,sort_keys=True)if proposed else lan_signature
   if lan is None or lan.poll()is not None or signature!=lan_signature:
    if lan:
     try:lan.stdin.close();lan.wait(timeout=3)
     except Exception:lan.terminate()
    lan=subprocess.Popen([str(APP/'runtime/node/node.exe'),str(APP/'lan-agent.cjs')],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    lan.stdin.write(json.dumps({'data':str(DATA),'config':proposed or lan_config()})+'\n');lan.stdin.flush();lan_signature=signature
   marker,auth_mtime=codex_login_marker()
   if marker!=state.get('account_marker')or auth_mtime!=state.get('auth_mtime'):
    if rate:rate.close();rate=None
    state.update(account_marker=marker,auth_mtime=auth_mtime)
   local=pool.submit(collect,state['start'],state['cursors']['A'],HOST)
   ds=pool.submit(deepseek.read_local)if not a.offline else None
   exe=detect_codex()
   try:
    if a.offline or not exe:raise FileNotFoundError('Codex login/executable needed')
    if rate is None:rate=RateReader(str(exe))
    quota=rate.read_with_retry(include_usage=False)
   except Exception as e:
    quota={'time':now(),'error':type(e).__name__,'error_details':quota_error(e)}
    if rate:rate.close();rate=None
   try:
    result=local.result();added=ingest(c,'A',result);state['cursors']['A']=result['cursors']
    exists=pathlib.Path(result['root']).is_dir();health['A']={'status':('parse_errors'if result['parse_errors']else'ok')if exists else'unavailable','added_tokens':added,'parse_errors':result['parse_errors']}
   except Exception as e:health['A']={'status':'unavailable','error':type(e).__name__}
   try:deepseek.record({'A':ds.result()if ds else RuntimeError('offline'),'B':{'status':'ok','events':[],'cursors':{}}})
   except Exception as e:
    (DATA/'errors.log').open('a',encoding='utf-8').write('DeepSeek: '+type(e).__name__+'\n')
   quota['account_marker']=marker
   c.execute('INSERT INTO samples(time,quota_json,health_json)VALUES(?,?,?)',(quota['time'],json.dumps(quota),json.dumps(health)));c.commit()
   state['sample_number']+=1;write_json(DATA/'state.json',state);export(c,state,health,quota);cycles+=1
   if a.cycles and cycles>=a.cycles:break
   while time.monotonic()-tick<a.interval and not(DATA/'stop.request').exists():time.sleep(.5)
 finally:
  state['running']=False;write_json(DATA/'state.json',state);export(c,state,health,quota)
  if rate:rate.close()
  if lan:
   try:lan.stdin.close();lan.wait(timeout=3)
   except Exception:lan.terminate()
  pool.shutdown(wait=True);c.close();(DATA/'monitor.pid').unlink(missing_ok=True);lock.close()
if __name__=='__main__':main()
