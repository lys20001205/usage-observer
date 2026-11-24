import datetime as dt,hashlib,importlib.util,json,pathlib,sqlite3,tempfile,unittest
from contextlib import closing
REPO=pathlib.Path(__file__).resolve().parents[1]
ROOT=REPO/'work/speed-tests';ROOT.mkdir(parents=True,exist_ok=True)
spec=importlib.util.spec_from_file_location('speed_probe',REPO/'src/speed_probe.py');speed=importlib.util.module_from_spec(spec);spec.loader.exec_module(speed)
NOW=dt.datetime(2026,10,14,9,0,tzinfo=dt.timezone.utc).timestamp()
def iso(value):return dt.datetime.fromtimestamp(value,dt.timezone.utc).isoformat()
class ProbeTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory(dir=ROOT);self.root=pathlib.Path(self.temp.name);self.home=self.root/'codex';self.home.mkdir();self.folder=self.home/'sessions';self.folder.mkdir();self.sid='00000000-0000-0000-0000-000000000001';self.log=self.folder/('rollout-'+self.sid+'.jsonl');self.log.write_text('');self.db=self.root/'records.sqlite';self.cache=self.root/'cache.json'
  with closing(sqlite3.connect(self.db)) as c,c:c.execute('CREATE TABLE events(host TEXT,response_id TEXT,time TEXT,session_id TEXT,model TEXT,output_tokens INTEGER,reasoning_output_tokens INTEGER)')
  with closing(sqlite3.connect(self.home/'state_5.sqlite')) as c,c:c.execute('CREATE TABLE threads(id TEXT,rollout_path TEXT)');c.execute('INSERT INTO threads VALUES (?,?)',(self.sid,str(self.log)))
 def tearDown(self):self.temp.cleanup()
 def append(self,t,kind,payload):
  with self.log.open('a',encoding='utf-8')as f:f.write(json.dumps(dict(timestamp=iso(t),type=kind,payload=payload))+'\n')
 def start(self,t=NOW-300,speed=None):
  if speed is not None:self.settings(t-.001,speed)
  self.append(t,'event_msg',dict(type='task_started'));self.append(t,'turn_context',dict(model='gpt-6.1-sol',effort='xhigh'))
 def response(self,rid,end,output=200,reasoning=100,host='A',record=True):
  if record:self.append(end,'token_usage_record',dict(response_id=rid,usage=dict(output_tokens=output,reasoning_output_tokens=reasoning,input_tokens=100000,total_tokens=100000+output)))
  with closing(sqlite3.connect(self.db)) as c,c:c.execute('INSERT INTO events VALUES (?,?,?,?,?,?,?)',(host,rid,iso(end),self.sid,'gpt-6.1-sol',output,reasoning))
 def probe(self):return speed.probe(self.db,self.home,self.cache,NOW)
 def standard(self):
  self.start();self.response('r1',NOW-280);self.append(NOW-100,'response_item',dict(type='custom_tool_call_output',output='private fixture'));self.response('r2',NOW-90,100,50)
 def test_effective_rate_excludes_tools(self):
  self.standard();d=self.probe();self.assertEqual(d['effective_tps'],10);self.assertEqual(d['visible_equivalent_tps'],5);self.assertEqual(d['samples'],2)
 def test_input_not_counted_as_generation(self):
  self.start();self.response('r',NOW-280);self.assertEqual(self.probe()['effective_tps'],10)
 def test_deduplicate_response_records(self):
  self.start();self.response('r',NOW-280);self.append(NOW-280,'token_usage_record',dict(response_id='r',usage=dict(output_tokens=200,reasoning_output_tokens=100)));self.assertEqual(self.probe()['samples'],1)
 def test_no_samples_is_null(self):self.assertIsNone(self.probe()['effective_tps'])
 def test_other_host_excluded(self):
  self.start();self.response('r',NOW-280,host='B');self.assertIsNone(self.probe()['effective_tps'])
 def test_future_excluded(self):
  self.start();self.response('r',NOW+60);self.assertIsNone(self.probe()['effective_tps'])
 def test_old_completion_excluded(self):
  self.start(NOW-1900);self.response('r',NOW-1850);self.assertIsNone(self.probe()['effective_tps'])
 def test_unmatched_coverage_explicit(self):
  self.response('r',NOW-280,record=False);d=self.probe();self.assertEqual(d['coverage'],'partial');self.assertIsNone(d['effective_tps'])
 def test_counter_mismatch_excluded(self):
  self.start();self.response('r',NOW-280);self.log.write_text(self.log.read_text().replace('"output_tokens": 200','"output_tokens": 201'));d=self.probe();self.assertEqual(d['mismatched_responses'],1);self.assertIsNone(d['effective_tps'])
 def test_aborted_anchor_not_reused(self):
  self.start();self.append(NOW-290,'event_msg',dict(type='turn_aborted'));self.response('r',NOW-280);self.assertIsNone(self.probe()['effective_tps'])
 def test_small_completion_retained(self):
  self.start();self.response('r',NOW-280,31,0);self.assertEqual(self.probe()['samples'],1)
 def test_incremental_matches_full_scan(self):
  self.start();self.response('r1',NOW-280);self.probe();self.append(NOW-100,'response_item',dict(type='function_call_output'));self.response('r2',NOW-90,100,50);a=self.probe();self.cache.unlink();b=self.probe();self.assertEqual(a,b)
 def test_partial_line_not_consumed(self):
  self.start();record=json.dumps(dict(timestamp=iso(NOW-280),type='token_usage_record',payload=dict(response_id='r',usage=dict(output_tokens=200,reasoning_output_tokens=100))));self.log.write_text(self.log.read_text()+record[:20]);self.probe()
  with self.log.open('a')as f:f.write(record[20:]+'\n')
  self.response('r',NOW-280,record=False);self.assertEqual(self.probe()['effective_tps'],10)
 def test_truncated_file_resets_cursor(self):
  self.standard();self.probe();self.log.write_text('');self.start(NOW-100);self.response('r3',NOW-90,100,50)
  with closing(sqlite3.connect(self.db)) as c,c:c.execute("DELETE FROM events WHERE response_id<>'r3'")
  self.assertEqual(self.probe()['samples'],1)
 def test_corrupt_cache_recovers(self):
  self.standard();self.cache.write_text('{bad');self.assertEqual(self.probe()['effective_tps'],10)
 def test_cache_has_no_prompt_content(self):
  self.standard();self.probe();self.assertNotIn('private fixture',self.cache.read_text())
 def test_database_and_rollout_unchanged(self):
  self.standard();before={p:hashlib.sha256(p.read_bytes()).hexdigest()for p in [self.db,self.log,self.home/'state_5.sqlite']};self.probe();self.assertEqual(before,{p:hashlib.sha256(p.read_bytes()).hexdigest()for p in before})
 def test_readonly_connection_rejects_writes(self):
  with speed.readonly(self.db)as c:
   with self.assertRaises(sqlite3.OperationalError):c.execute('CREATE TABLE forbidden(x)')
 def test_missing_database(self):
  self.db.unlink();d=self.probe();self.assertIsNone(d['effective_tps']);self.assertEqual(d['error'],'no_database')
 def test_invalid_reasoning_subset(self):
  self.start();self.response('r',NOW-280,200,201);self.assertIsNone(self.probe()['effective_tps'])
 def test_waiting_is_measured_without_inventing_generation_start(self):
  self.start(NOW-1800);self.append(NOW-1700,'response_item',dict(type='custom_tool_call_output',output='fixture'))
  for offset in (140,130,120,110,100):self.append(NOW-offset,'response_item',dict(type='reasoning'))
  self.response('r',NOW-90,1200,600);d=self.probe()
  self.assertAlmostEqual(d['effective_tps'],1200/1610,places=3)
  self.assertEqual(d['groups'][0]['history']['seconds'],1610)
 def test_trailing_item_after_usage_is_not_the_next_start(self):
  self.start(NOW-1600);self.append(NOW-1500,'response_item',dict(type='custom_tool_call_output',output='fixture'))
  for offset in (140,130,120,110,100):self.append(NOW-offset,'response_item',dict(type='reasoning'))
  self.response('r1',NOW-90,1200,600)
  self.append(NOW-89.5,'response_item',dict(type='message'))
  for offset in (40,30,20,10):self.append(NOW-offset,'response_item',dict(type='reasoning'))
  self.response('r2',NOW-5,600,300);d=self.probe()
  self.assertAlmostEqual(d['effective_tps'],1800/1495,places=3)
 def test_no_item_cadence_falls_back_to_anchor(self):
  self.start(NOW-280);self.append(NOW-270,'response_item',dict(type='custom_tool_call_output',output='fixture'))
  for offset in (10,9.9,9.8,9.7,9.6):self.append(NOW-offset,'response_item',dict(type='reasoning'))
  self.response('r',NOW-9,100,50);d=self.probe()
  self.assertAlmostEqual(d['effective_tps'],100/261,places=3)
 def settings(self,t,value):
  self.append(t,'event_msg',dict(type='thread_settings_applied',thread_settings=dict(model='gpt-6.1-sol',service_tier=value,reasoning_effort='max')))
 def test_rates_above_300_are_retained(self):
  self.start(NOW-3,speed='ultrafast');self.response('r',NOW-1,1600,100)
  d=self.probe();self.assertEqual(d['effective_tps'],800);self.assertEqual(d['excluded_responses'],0)
 def test_subsecond_response_is_retained(self):
  self.start(NOW-1);self.response('r',NOW-.8,400,100)
  self.assertAlmostEqual(self.probe()['effective_tps'],2000,places=2)
 def test_settings_event_supplies_tier_missing_from_turn_context(self):
  self.start(speed='ultrafast');self.response('r',NOW-280)
  self.assertEqual(self.probe()['groups'][0]['speed'],'ultrafast')
 def test_fast_aliases_are_grouped_together(self):
  self.start(speed='priority');self.response('r1',NOW-280)
  self.settings(NOW-279,'fast');self.response('r2',NOW-270)
  d=self.probe();self.assertEqual(len(d['groups']),1);self.assertEqual(d['groups'][0]['speed'],'fast')
 def test_unknown_tier_is_not_assumed_standard(self):
  self.start();self.response('r',NOW-280)
  self.assertEqual(self.probe()['groups'][0]['speed'],'unknown')
 def test_tiers_have_independent_statistics_and_latest_selection(self):
  self.start(speed='default');self.response('slow',NOW-280,200,100)
  self.append(NOW-279,'event_msg',dict(type='task_complete'))
  self.start(NOW-278,speed='ultrafast');self.response('fast',NOW-273,2000,100)
  d=self.probe();g={g['speed']:g for g in d['groups']}
  self.assertEqual(g['standard']['recent']['effective_tps'],10)
  self.assertEqual(g['ultrafast']['recent']['effective_tps'],400)
  self.assertEqual(d['effective_tps'],400)
 def test_setting_change_during_output_does_not_relabel_started_response(self):
  self.start(speed='default')
  self.append(NOW-290,'response_item',dict(type='reasoning'));self.settings(NOW-289,'ultrafast')
  self.response('r1',NOW-280);self.response('r2',NOW-270)
  d=self.probe();self.assertEqual(len(d['groups']),1);self.assertEqual(d['groups'][0]['speed'],'standard')
  self.assertEqual(d['groups'][0]['configured_speed'],'ultrafast');self.assertTrue(d['groups'][0]['active_turn'])
 def test_response_reported_tier_overrides_chat_setting(self):
  self.start(speed='default');self.response('r',NOW-280,record=False)
  self.append(NOW-280,'token_usage_record',dict(response_id='r',service_tier='ultrafast',usage=dict(output_tokens=200,reasoning_output_tokens=100)))
  self.assertEqual(self.probe()['groups'][0]['speed'],'ultrafast')
 def test_old_samples_are_only_in_history(self):
  self.start(NOW-1000);self.response('old',NOW-900)
  d=self.probe();self.assertIsNone(d['effective_tps']);self.assertEqual(d['groups'][0]['history']['samples'],1);self.assertEqual(d['groups'][0]['recent']['samples'],0)
 def test_zero_duration_does_not_become_infinite_speed(self):
  self.start(NOW-10);self.response('r',NOW-10)
  self.assertIsNone(self.probe()['effective_tps'])
 def test_completed_tool_spans_are_subtracted_once(self):
  self.start(NOW-100)
  self.append(NOW-99,'response_item',dict(type='reasoning'))
  self.append(NOW-90,'response_item',dict(type='function_call',call_id='c'))
  self.append(NOW-50,'event_msg',dict(type='item_completed',item=dict(type='CommandExecution'),started_at_ms=(NOW-90)*1000,completed_at_ms=(NOW-50)*1000))
  self.append(NOW-50,'response_item',dict(type='function_call_output',call_id='c',output='private command output'))
  self.response('r',NOW-40,1000,100)
  d=self.probe();self.assertEqual(d['effective_tps'],50);self.assertEqual(d['groups'][0]['recent']['tool_seconds'],40)
 def test_overlap_of_parallel_tools_is_counted_once(self):
  self.assertEqual(speed.elapsed_tools([[10,30],[20,40]],0,50),30)
 def test_other_chats_are_not_averaged(self):
  self.start(NOW-100,speed='ultrafast');self.response('r1',NOW-90,1000,100)
  sid='00000000-0000-0000-0000-000000000002';p=self.folder/('rollout-'+sid+'.jsonl')
  rows=[dict(timestamp=iso(NOW-20),type='event_msg',payload=dict(type='task_started')),dict(timestamp=iso(NOW-19),type='event_msg',payload=dict(type='thread_settings_applied',thread_settings=dict(model='gpt-6.1-sol',service_tier='ultrafast'))),dict(timestamp=iso(NOW-10),type='token_usage_record',payload=dict(response_id='r2',usage=dict(output_tokens=200,reasoning_output_tokens=100)))]
  p.write_text(''.join(json.dumps(r)+'\n' for r in rows))
  with closing(sqlite3.connect(self.db)) as c,c:c.execute('INSERT INTO events VALUES (?,?,?,?,?,?,?)',('A','r2',iso(NOW-10),sid,'gpt-6.1-sol',200,100))
  with closing(sqlite3.connect(self.home/'state_5.sqlite')) as c,c:c.execute('INSERT INTO threads VALUES (?,?)',(sid,str(p)))
  d=self.probe();self.assertEqual(len(d['groups']),2);self.assertEqual(d['effective_tps'],20);self.assertEqual(d['samples'],1)
 def test_chat_title_is_read_without_changing_state(self):
  self.start();self.response('r',NOW-280)
  with closing(sqlite3.connect(self.home/'state_5.sqlite')) as c,c:c.execute('ALTER TABLE threads ADD COLUMN title TEXT');c.execute('UPDATE threads SET title=?',('Chat title',))
  self.assertEqual(self.probe()['groups'][0]['title'],'Chat title')
 def test_old_cadence_cache_is_invalidated(self):
  self.standard();self.cache.write_text(json.dumps(dict(version=4,identity='old',files={})))
  d=self.probe();self.assertEqual(d['effective_tps'],10);self.assertEqual(json.loads(self.cache.read_text())['version'],6)
 def test_setting_change_before_first_output_preserves_active_turn(self):
  self.start(speed='default');self.settings(NOW-290,'ultrafast');self.response('r',NOW-280)
  self.assertEqual(self.probe()['groups'][0]['speed'],'standard')
 def test_next_turn_uses_changed_setting(self):
  self.start(speed='default');self.response('r1',NOW-280);self.settings(NOW-279,'ultrafast')
  self.append(NOW-275,'event_msg',dict(type='task_complete'));self.start(NOW-270);self.response('r2',NOW-260)
  groups={g['speed']:g for g in self.probe()['groups']}
  self.assertEqual(groups['ultrafast']['recent']['samples'],1);self.assertEqual(groups['standard']['recent']['samples'],1)
 def test_incremental_cache_keeps_frozen_turn_setting(self):
  self.start(speed='default');self.response('r1',NOW-280);self.probe()
  self.settings(NOW-279,'ultrafast');self.response('r2',NOW-270)
  d=self.probe();self.assertEqual(len(d['groups']),1);self.assertEqual(d['groups'][0]['speed'],'standard')
 def test_current_chat_name_takes_priority_over_first_prompt(self):
  self.start();self.response('r',NOW-280)
  with closing(sqlite3.connect(self.home/'state_5.sqlite')) as c,c:
   c.execute('ALTER TABLE threads ADD COLUMN title TEXT');c.execute('ALTER TABLE threads ADD COLUMN name TEXT')
   c.execute('UPDATE threads SET title=?,name=?',('# Files mentioned by the user','Friendly chat name'))
  self.assertEqual(self.probe()['groups'][0]['title'],'Friendly chat name')
 def test_execution_tier_overrides_thread_default_for_exact_turn(self):
  turn='00000000-0000-0000-0000-000000000003'
  self.settings(NOW-101,'ultrafast')
  self.append(NOW-100,'event_msg',dict(type='task_started',turn_id=turn))
  self.response('r',NOW-90)
  with closing(sqlite3.connect(self.home/'logs_2.sqlite')) as c,c:
   c.execute('CREATE TABLE logs(id INTEGER PRIMARY KEY,ts INTEGER,target TEXT,thread_id TEXT,feedback_log_body TEXT)')
   c.execute('INSERT INTO logs VALUES (1,?,?,?,?)',(int(NOW-90),'feedback_tags',self.sid,'turn{turn.id='+turn+'}: {"service_tier":"default"}'))
  self.assertEqual(self.probe()['groups'][0]['speed'],'standard')
 def test_execution_metadata_does_not_cross_turns(self):
  self.start(NOW-100,speed='ultrafast');self.response('r',NOW-90)
  with closing(sqlite3.connect(self.home/'logs_2.sqlite')) as c,c:
   c.execute('CREATE TABLE logs(id INTEGER PRIMARY KEY,ts INTEGER,target TEXT,thread_id TEXT,feedback_log_body TEXT)')
   c.execute('INSERT INTO logs VALUES (1,?,?,?,?)',(int(NOW-90),'feedback_tags',self.sid,'turn{turn.id=00000000-0000-0000-0000-000000000003}: {"service_tier":"default"}'))
  self.assertEqual(self.probe()['groups'][0]['speed'],'ultrafast')
 def test_duplicate_does_not_move_next_phase_anchor(self):
  self.start(NOW-100);self.response('r1',NOW-90,100,50)
  self.append(NOW-80,'token_usage_record',dict(response_id='r1',usage=dict(output_tokens=100,reasoning_output_tokens=50)))
  self.response('r2',NOW-70,100,50)
  self.assertAlmostEqual(self.probe()['effective_tps'],200/30,places=3)
if __name__=='__main__':unittest.main(verbosity=1)
