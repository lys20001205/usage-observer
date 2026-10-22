import importlib.util
import pathlib
import tempfile
import unittest

root = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('repair_legacy', root / 'packaging/repair-legacy-recorder.py')
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)

MONITOR = '''import datetime as dt
TZ=dt.timezone(dt.timedelta(hours=8))
def show(x):return dt.datetime.fromisoformat(x).astimezone(TZ).strftime('%Y-%m-%d %H:%M:%S')
class RemoteReader:
 def __init__(self):
  self.client=load_config()
 def read(self,start,cursors):
  return self.client.read()
'''
DEEPSEEK = '''import datetime as dt
TZ=dt.timezone(dt.timedelta(hours=8))
class DeepSeekMonitor:
 def __init__(self,connection,codex_remote_class):
  self.remote=codex_remote_class()
  # ASCII-only compressed read script
  self.remote.client.configure()
  self.c.executescript('')
 def since(self):return (dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0)-dt.timedelta(days=6)).astimezone(dt.timezone.utc).isoformat()
 def read_remote(self):
  return self.remote.client.read()
 def export(self):
  today=dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0).astimezone(dt.timezone.utc).isoformat()
'''


class LegacyRepairTests(unittest.TestCase):
    def test_transform_is_idempotent_and_retains_grant_check(self):
        changed = repair.transform_monitor(MONITOR)
        compile(changed, 'monitor.py', 'exec')
        self.assertEqual(repair.transform_monitor(changed), changed)
        self.assertIn('def connect(self):\n  self.client=load_config()', changed)
        self.assertIn('def read(self,start,cursors):\n  self.connect()', changed)
        self.assertIn('def show(x):return format_local(x)', changed)
        wallet = repair.transform_deepseek(DEEPSEEK)
        compile(wallet, 'deepseek_monitor.py', 'exec')
        self.assertEqual(repair.transform_deepseek(wallet), wallet)
        self.assertIn('self.remote.connect()', wallet)
        self.assertIn('local_day_start_utc(6)', wallet)
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
            for name in ('records.sqlite', 'state.json', 'ui-settings.json'):
                self.assertEqual((directory / name).read_bytes(), b'preserve existing authored data')


if __name__ == '__main__':
    unittest.main()
