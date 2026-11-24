import importlib.util
import json
import pathlib
import sqlite3
import tempfile
import unittest
from contextlib import closing

root = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('repair_legacy', root / 'packaging/repair-legacy-recorder.py')
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)
state_spec = importlib.util.spec_from_file_location('legacy_state', root / 'src/recorder_state.py')
legacy_state = importlib.util.module_from_spec(state_spec)
state_spec.loader.exec_module(legacy_state)

MONITOR = '''import datetime as dt
TZ=dt.timezone(dt.timedelta(hours=8))
def show(x):return dt.datetime.fromisoformat(x).astimezone(TZ).strftime('%Y-%m-%d %H:%M:%S')
def write_json(p,x):
 temp=p.with_suffix(p.suffix+'.tmp');temp.write_text(json.dumps(x,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(p)
class RemoteReader:
 def __init__(self):
  self.client=load_config()
 def read(self,start,cursors):
  return self.client.read()
def main():
 state_path=BASE/'state.json';state=json.loads(state_path.read_text(encoding='utf-8')) if state_path.exists() else {'start':now(),'cursors':{'A':{},'B':{}},'sample_number':0}
 write_json(state_path,state)
 c=database();exe=None
 sample(c);c.commit();state['sample_number']+=1;write_json(state_path,state)
 write_json(state_path,state)
'''
DEEPSEEK = """import datetime as dt
TZ=dt.timezone(dt.timedelta(hours=8))
def atomic(path,data):
 temp=path.with_suffix(path.suffix+'.tmp');temp.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(path)
class DeepSeekMonitor:
 def __init__(self,connection,codex_remote_class):
  self.c=connection;self.state_path=BASE/'deepseek-state.json'
  self.state=json.loads(self.state_path.read_text(encoding='utf-8')) if self.state_path.exists() else {'start':now(),'cursors':{'A':{},'B':{}}}
  self.remote=codex_remote_class()
  # ASCII-only compressed read script
  self.remote.client.configure()
  self.c.executescript('''CREATE TABLE IF NOT EXISTS deepseek_health(id INTEGER PRIMARY KEY,time TEXT,data_json TEXT);''')
 def since(self):return (dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0)-dt.timedelta(days=6)).astimezone(dt.timezone.utc).isoformat()
 def read_remote(self):
  return self.remote.client.read()
 def record(self):
  self.c.commit();atomic(self.state_path,self.state)
 def export(self):
  today=dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0).astimezone(dt.timezone.utc).isoformat()
"""


class LegacyRepairTests(unittest.TestCase):
    def test_transform_is_idempotent_and_retains_grant_check(self):
        changed = repair.transform_monitor(MONITOR)
        compile(changed, 'monitor.py', 'exec')
        self.assertEqual(repair.transform_monitor(changed), changed)
        self.assertIn('def connect(self):\n  self.client=load_config()', changed)
        self.assertIn('def read(self,start,cursors):\n  self.connect()', changed)
        self.assertIn('def show(x):return format_local(x)', changed)
        self.assertIn('state=load_state(c,state_path)', changed)
        self.assertEqual(changed.count('save_state(c,state_path,state)'), 3)
        wallet = repair.transform_deepseek(DEEPSEEK)
        compile(wallet, 'deepseek_monitor.py', 'exec')
        self.assertEqual(repair.transform_deepseek(wallet), wallet)
        self.assertIn('self.remote.connect()', wallet)
        self.assertIn('local_day_start_utc(6)', wallet)
        self.assertIn('load_state(self.c,self.state_path,deepseek=True)', wallet)
        self.assertNotIn('timedelta(hours=8)', wallet)

    def test_running_recorder_is_not_modified(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = pathlib.Path(directory)
            (directory / 'monitor.pid').write_text('123')
            with self.assertRaisesRegex(RuntimeError, 'Stop the legacy recorder'):
                repair.repair(directory, directory / 'backup')
            self.assertFalse((directory / 'backup').exists())

    def test_backup_and_repair_leave_database_and_settings_intact(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = pathlib.Path(directory)
            (directory / 'monitor.py').write_text(MONITOR, encoding='utf-8')
            (directory / 'deepseek_monitor.py').write_text(DEEPSEEK, encoding='utf-8')
            for name in ('records.sqlite', 'state.json', 'ui-settings.json'):
                (directory / name).write_bytes(b'preserve existing authored data')
            repair.repair(directory, directory / 'backup')
            self.assertEqual((directory / 'backup/monitor.py').read_text(), MONITOR)
            self.assertTrue((directory / 'machine_time.py').exists())
            self.assertTrue((directory / 'legacy_state.py').exists())
            for name in ('records.sqlite', 'state.json', 'ui-settings.json'):
                self.assertEqual((directory / name).read_bytes(), b'preserve existing authored data')

    def test_recovers_corrupt_state_from_status_and_database(self):
        with tempfile.TemporaryDirectory() as folder, closing(sqlite3.connect(':memory:')) as db:
            folder = pathlib.Path(folder)
            started = '2026-10-09T07:47:41+00:00'
            sampled = '2026-10-09T07:47:43+00:00'
            db.execute('CREATE TABLE samples(time TEXT)')
            db.execute('CREATE TABLE deepseek_health(time TEXT)')
            db.execute('INSERT INTO samples VALUES (?)', (sampled,))
            db.execute('INSERT INTO deepseek_health VALUES (?)', (sampled,))
            for name in ('state.json', 'deepseek-state.json', 'deepseek-status.json'):
                (folder / name).write_bytes(b'\0' * 64)
            (folder / 'status.json').write_text(json.dumps({'recording_started_at': started}), encoding='utf-8')

            codex = legacy_state.load_state(db, folder / 'state.json')
            wallet = legacy_state.load_state(db, folder / 'deepseek-state.json', deepseek=True)

            self.assertEqual(codex, {'start': started, 'sample_number': 1, 'cursors': {'A': {}, 'B': {}}})
            self.assertEqual(wallet, {'start': sampled, 'cursors': {'A': {}, 'B': {}}})

    def test_durable_checkpoint_survives_lost_json_mirror(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = pathlib.Path(folder)
            path = folder / 'state.json'
            state = {'start': '2026-10-09T07:47:41+00:00', 'sample_number': 4,
                     'cursors': {'A': {'file.jsonl': {'offset': 5}}, 'B': {}}}
            path.write_bytes(b'\xef\xbb\xbf' + json.dumps(state).encode('utf-8'))
            with closing(sqlite3.connect(folder / 'records.sqlite')) as db:
                self.assertEqual(legacy_state.load_state(db, path), state)
                legacy_state.save_state(db, path, state)
            self.assertEqual(json.loads(path.read_text(encoding='utf-8')), state)
            path.write_text('{"start":', encoding='utf-8')

            with closing(sqlite3.connect(folder / 'records.sqlite')) as db:
                self.assertEqual(legacy_state.load_state(db, path), state)


if __name__ == '__main__':
    unittest.main()
