"""Upgrade a stopped legacy A/B recorder without changing data or remote grants."""
import argparse
import hashlib
import json
import pathlib
import shutil


def transform_monitor(text):
    if 'class RemoteReader:\n def __init__(self):\n' not in text:
        raise ValueError('Unsupported legacy recorder')
    if ' def connect(self):\n' not in text:
        text = text.replace('class RemoteReader:\n def __init__(self):\n', 'class RemoteReader:\n def __init__(self):\n  self.client=None\n def connect(self):\n', 1)
        text = text.replace(' def read(self,start,cursors):\n', ' def read(self,start,cursors):\n  self.connect()\n', 1)
    text = text.replace("  for part in os.environ['PYTHONPATH'].split(os.pathsep):sys.path.insert(0,part)\n", "  for part in os.environ['PYTHONPATH'].split(os.pathsep):\n   if part and part not in sys.path:sys.path.insert(0,part)\n")
    text = text.replace('TZ=dt.timezone(dt.timedelta(hours=8))', 'from machine_time import format_local')
    text = text.replace("def show(x):return dt.datetime.fromisoformat(x).astimezone(TZ).strftime('%Y-%m-%d %H:%M:%S')", 'def show(x):return format_local(x)')
    text = text.replace("'time_beijing'", "'time_local'").replace(' 北京时间', ' 本机时间')
    return text


def transform_deepseek(text):
    if 'self.remote=codex_remote_class()' not in text:
        raise ValueError('Unsupported legacy wallet recorder')
    if ' def connect_remote(self):\n' not in text:
        start = text.index('  # ASCII-only compressed read script')
        end = text.index('  self.c.executescript', start)
        connection = text[start:end]
        text = text[:start] + text[end:]
        text = text.replace(' def since(self):', ' def connect_remote(self):\n  self.remote.connect()\n' + connection + ' def since(self):', 1)
        text = text.replace(' def read_remote(self):\n', ' def read_remote(self):\n  self.connect_remote()\n', 1)
    text = text.replace('TZ=dt.timezone(dt.timedelta(hours=8))', 'from machine_time import local_day_start_utc, format_local')
    text = text.replace('(dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0)-dt.timedelta(days=6)).astimezone(dt.timezone.utc).isoformat()', 'local_day_start_utc(6).isoformat()')
    text = text.replace('dt.datetime.now(TZ).replace(hour=0,minute=0,second=0,microsecond=0).astimezone(dt.timezone.utc).isoformat()', 'local_day_start_utc().isoformat()')
    # Keep raw snapshot/CSV instants intact; convert only the generated report.
    text = text.replace("'最后采样：'+stamp", "'最后采样：'+format_local(stamp)")
    text = text.replace("起点：'+self.state['start']", "起点：'+format_local(self.state['start'])")
    return text


def repair(root, backup):
    root = pathlib.Path(root).resolve()
    backup = pathlib.Path(backup).resolve()
    if (root / 'monitor.pid').exists():
        raise RuntimeError('Stop the legacy recorder before replacing its source')
    originals = {name: (root / name).read_text(encoding='utf-8-sig') for name in ('monitor.py', 'deepseek_monitor.py')}
    updated = {'monitor.py': transform_monitor(originals['monitor.py']), 'deepseek_monitor.py': transform_deepseek(originals['deepseek_monitor.py'])}
    helper = pathlib.Path(__file__).resolve().parents[1] / 'src' / 'machine_time.py'
    updated['machine_time.py'] = helper.read_text(encoding='utf-8')
    for name, text in updated.items():
        compile(text, name, 'exec')
        if name != 'machine_time.py' and ('timedelta(hours=8)' in text or '北京时间' in text):
            raise ValueError('Unconverted legacy local-time expression: ' + name)
    backup.mkdir(parents=True, exist_ok=False)
    hashes = {}
    for name in updated:
        target = root / name
        if target.exists():
            shutil.copy2(target, backup / name)
            hashes[name] = hashlib.sha256(target.read_bytes()).hexdigest()
    (backup / 'before.json').write_text(json.dumps(hashes, indent=2), encoding='utf-8')
    # Dependencies first, entry point last. No database, settings or grant writes.
    for name in ('machine_time.py', 'deepseek_monitor.py', 'monitor.py'):
        temporary = root / (name + '.upgrade-tmp')
        temporary.write_text(updated[name], encoding='utf-8')
        temporary.replace(root / name)
    print(json.dumps({'updated': list(updated), 'data_modified': False, 'remote_grants_modified': False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--backend', required=True)
    parser.add_argument('--backup-dir', required=True)
    args = parser.parse_args()
    repair(args.backend, args.backup_dir)
