"""Persist recorder progress with the history it describes."""
import datetime as dt
import json
import os
import sys
import tempfile
from pathlib import Path


def read_json(path):
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (FileNotFoundError, ValueError):
        return None


def valid_timestamp(value):
    if not isinstance(value, str):
        return False
    try:
        return dt.datetime.fromisoformat(value).utcoffset() is not None
    except ValueError:
        return False


def valid_state(state, deepseek):
    if not isinstance(state, dict) or not valid_timestamp(state.get('start')):
        return False
    cursors = state.get('cursors')
    if not isinstance(cursors, dict) or not all(isinstance(cursors.get(host), dict) for host in ('A', 'B')):
        return False
    count = state.get('sample_number')
    return deepseek or (type(count) is int and count >= 0)


def load_state(connection, path, *, deepseek=False):
    connection.execute('CREATE TABLE IF NOT EXISTS recorder_checkpoints '
                       '(name TEXT PRIMARY KEY, state_json TEXT NOT NULL)')
    saved = connection.execute('SELECT state_json FROM recorder_checkpoints WHERE name=?', (path.name,)).fetchone()
    if saved is not None:
        try:
            state = json.loads(saved[0])
        except ValueError:
            state = None
        if valid_state(state, deepseek):
            return state

    state = read_json(path)
    if valid_state(state, deepseek):
        return state

    if deepseek:
        status_path = path.with_name('deepseek-status.json')
        first_sample, = connection.execute('SELECT min(time) FROM deepseek_health').fetchone()
    else:
        status_path = path.with_name('status.json')
        sample_count, first_sample = connection.execute('SELECT count(*), min(time) FROM samples').fetchone()
    status = read_json(status_path)
    started_at = status.get('recording_started_at') if isinstance(status, dict) else None
    if not valid_timestamp(started_at):
        started_at = first_sample if valid_timestamp(first_sample) else dt.datetime.now(dt.timezone.utc).isoformat()
    if first_sample or path.exists():
        print(f'Missing or invalid {path.name}; rebuilding cursors from {started_at}', file=sys.stderr, flush=True)
    state = {'start': started_at, 'cursors': {'A': {}, 'B': {}}}
    if not deepseek:
        state['sample_number'] = sample_count
    return state


def write_json(path, data):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent,
                                         prefix=path.name + '.', suffix='.tmp', delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def save_state(connection, path, state):
    with connection:
        connection.execute('INSERT INTO recorder_checkpoints(name, state_json) VALUES(?, ?) '
                           'ON CONFLICT(name) DO UPDATE SET state_json=excluded.state_json',
                           (path.name, json.dumps(state, ensure_ascii=False)))
    write_json(path, state)
