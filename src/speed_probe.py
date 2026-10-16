"""Read-only local response-phase TPS. No prompts, credentials or DB writes.

Effective output rate = output tokens (including reasoning) divided by the time the
session actually showed generation running. The phase anchor alone is not usable as a
generation clock: a turn can sit silent for minutes after a tool returns (queueing,
pacing, rate limits) while producing very few tokens, and charging that silence to the
denominator reports a throttled session as a slow model. Each model output item that
the client records is evidence that generation was live at that moment, so the measured
window starts one item-duration before the first observed item of the response, and the
silent prefix before that is reported separately as waiting.
"""
import argparse, datetime as dt, json, math, os, pathlib, sqlite3, statistics, tempfile
from contextlib import contextmanager

FIELDS = ('input_tokens', 'cached_input_tokens', 'output_tokens', 'reasoning_output_tokens', 'total_tokens')
CACHE_VERSION = 4
ITEM_TYPES = ('reasoning', 'message', 'custom_tool_call', 'function_call', 'local_shell_call',
              'apply_patch_call', 'web_search_call', 'mcp_tool_call')
OUTPUT_TYPES = ('custom_tool_call_output', 'function_call_output', 'local_shell_call_output',
                'apply_patch_call_output', 'web_search_call_output', 'mcp_tool_call_output')
KEEP_WORDS = (b'"token_usage_record"', b'"turn_context"', b'"task_started"', b'"task_complete"',
              b'"turn_aborted"', b'"custom_tool_call"', b'"custom_tool_call_output"', b'"function_call"',
              b'"function_call_output"', b'"local_shell_call"', b'"local_shell_call_output"',
              b'"apply_patch_call"', b'"apply_patch_call_output"', b'"web_search_call"',
              b'"web_search_call_output"', b'"mcp_tool_call"', b'"mcp_tool_call_output"',
              b'"reasoning"', b'"message"')
ITEM_GRACE_SECONDS = 2.0
GAP_MIN_SECONDS, GAP_MAX_SECONDS = 0.5, 120.0
MIN_WINDOW_SECONDS, MAX_PLAUSIBLE_TPS = 1.0, 300.0
METHOD = '近30分单请求生成窗口有效输出；含思考；剔除首项前的静默等待与显式工具执行；非端到端速度'

def stamp(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()

@contextmanager
def readonly(path):
    connection = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=2)
    connection.execute('PRAGMA query_only=ON')
    connection.row_factory = sqlite3.Row
    try:
        yield connection
    finally:
        connection.close()

def load_cache(path, identity):
    try:
        cache = json.loads(path.read_text(encoding='utf-8'))
        if cache.get('version') == CACHE_VERSION and cache.get('identity') == identity:
            return cache
    except (OSError, ValueError, TypeError):
        pass
    return dict(version=CACHE_VERSION, identity=identity, files={})

def write_cache(path, cache):
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp = tempfile.mkstemp(prefix='speed-cache-', dir=str(path.parent))
    try:
        with os.fdopen(handle, 'w', encoding='utf-8') as stream:
            json.dump(cache, stream, ensure_ascii=True, separators=(',', ':'))
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)

def parse_file(path, previous, begin):
    stat = path.stat()
    cur = previous if isinstance(previous, dict) else {}
    # Reading an replaced/truncated file must not retain its earlier phase anchor.
    if stat.st_size < cur.get('offset', 0) or stat.st_mtime_ns < cur.get('mtime', 0):
        cur = {}
    cur.setdefault('offset', 0)
    cur.setdefault('responses', {})
    cur.setdefault('items', [])
    cur.setdefault('gaps', [])
    with path.open('rb') as stream:
        stream.seek(cur['offset'])
        while True:
            position = stream.tell()
            line = stream.readline()
            if not line:
                break
            if not line.endswith(b'\n'):
                stream.seek(position)
                break
            if not any(word in line for word in KEEP_WORDS):
                continue
            try:
                record = json.loads(line)
                payload = record.get('payload') or {}
                kind, sub = record.get('type'), payload.get('type')
                when = stamp(record['timestamp'])
                if kind == 'turn_context':
                    cur['model'] = payload.get('model')
                    cur['effort'] = payload.get('effort')
                elif kind == 'event_msg' and sub == 'task_started':
                    cur['anchor'] = when
                    cur['items'] = []
                elif kind == 'event_msg' and sub in ('task_complete', 'turn_aborted'):
                    cur['anchor'] = None
                    cur['items'] = []
                elif kind == 'response_item' and sub in OUTPUT_TYPES:
                    cur['anchor'] = when
                elif kind == 'response_item' and sub in ITEM_TYPES:
                    # Live evidence of generation: this item finished at `when`.
                    anchor = cur.get('anchor')
                    items = cur['items']
                    if anchor is None:
                        pass
                    elif not items and cur.get('closed_at') is not None and when - cur['closed_at'] <= ITEM_GRACE_SECONDS:
                        # A response's last item is written just after its own usage record;
                        # it must not become the first item of the following response.
                        pass
                    else:
                        if items and 0 < when - items[-1] < 600:
                            cur['gaps'] = (cur['gaps'] + [when - items[-1]])[-60:]
                        items.append(when)
                        cur['items'] = items
                elif kind == 'token_usage_record':
                    response_id = payload.get('response_id')
                    if not response_id or response_id in cur['responses']:
                        cur['items'] = []
                        cur['closed_at'] = when
                        continue
                    anchor = cur.get('anchor')
                    items = [value for value in cur.get('items', []) if anchor is None or value >= anchor]
                    if when >= begin and anchor is not None and 0 < when - anchor:
                        usage = payload.get('usage') or {}
                        # One item duration is only borrowed from the session's own cadence,
                        # and only when that cadence is actually observable.
                        cadence = sorted(value for value in cur['gaps'] if GAP_MIN_SECONDS <= value <= GAP_MAX_SECONDS)
                        unit = statistics.median(cadence) if len(cadence) >= 3 else 0.0
                        start = max(anchor, items[0] - unit) if (items and unit) else anchor
                        seconds = when - start
                        if seconds >= .1:
                            cur['responses'][response_id] = dict(
                                end=when, seconds=seconds, unit=unit, items=len(items),
                                waiting=max(0.0, (items[0] - unit) - anchor) if (items and unit) else 0.0,
                                model=cur.get('model'),
                                usage={key: usage.get(key, 0) for key in FIELDS})
                    cur['anchor'] = when
                    cur['closed_at'] = when
                    cur['items'] = []
            except (ValueError, KeyError, TypeError, AttributeError, OverflowError):
                # An unparseable boundary cannot be used as a trustworthy phase start.
                cur['anchor'] = None
                cur['items'] = []
                cur['parse_errors'] = cur.get('parse_errors', 0) + 1
        cur['offset'] = stream.tell()
    cur['mtime'] = stat.st_mtime_ns
    cur['responses'] = {key:value for key,value in cur['responses'].items() if value['end'] >= begin-900}
    return cur

def probe(database, home, cache_path, now):
    begin = now-1800
    identity = str(database.resolve()) + '|' + str(home.resolve())
    cache = load_cache(cache_path, identity)
    empty = dict(sampled_at=dt.datetime.fromtimestamp(now, dt.timezone.utc).isoformat(), window_seconds=1800, method=METHOD, effective_tps=None, median_tps=None, visible_equivalent_tps=None, waiting_seconds=0.0, samples=0, excluded_responses=0, last_response_at=None, models={}, coverage='unavailable', error=None)
    if not database.is_file():
        return dict(empty, error='no_database')
    with readonly(database) as connection:
        lower = dt.datetime.fromtimestamp(begin, dt.timezone.utc).isoformat()
        rows = connection.execute("SELECT response_id,time,session_id,model,output_tokens,reasoning_output_tokens FROM events WHERE host='A' AND time>=? ORDER BY time DESC LIMIT 2001", (lower,)).fetchall()
    if len(rows) > 2000:
        return dict(empty, error='response_limit_exceeded')
    rows = [row for row in rows if begin <= stamp(row['time']) <= now+1]
    if not rows:
        return dict(empty, coverage='no_samples', error=None)
    ids = {row['session_id'] for row in rows}
    paths = {}
    state = home/'state_5.sqlite'
    if state.is_file():
        with readonly(state) as connection:
            marks = ','.join('?' for _ in ids)
            paths = {row['id']: pathlib.Path(row['rollout_path']) for row in connection.execute('SELECT id,rollout_path FROM threads WHERE id IN ('+marks+')', tuple(ids)) if pathlib.Path(row['rollout_path']).is_file()}
    missing = ids-paths.keys()
    if missing:
        for folder in ('sessions', 'archived_sessions'):
            for path in (home/folder).rglob('*.jsonl'):
                sid = path.stem[-36:]
                if sid in missing:
                    paths[sid] = path
                    missing.remove(sid)
    data = {};active = {}
    for sid, path in paths.items():
        key = str(path)
        cur = parse_file(path, cache['files'].get(key), begin)
        active[key] = cur
        data.update(cur['responses'])
    cache['files'] = active
    cache['sampled_at'] = now
    write_cache(cache_path, cache)
    valid = [];matched = 0;mismatched = 0;excluded = 0
    for row in rows:
        record = data.get(row['response_id'])
        if not record:
            continue
        usage = record['usage']
        if usage['output_tokens'] != row['output_tokens'] or usage['reasoning_output_tokens'] != row['reasoning_output_tokens']:
            mismatched += 1
            continue
        matched += 1
        seconds = record['seconds']
        if (math.isfinite(seconds) and MIN_WINDOW_SECONDS <= seconds and row['output_tokens'] >= 32
                and 0 <= row['reasoning_output_tokens'] <= row['output_tokens']
                and row['output_tokens']/seconds <= MAX_PLAUSIBLE_TPS):
            valid.append(dict(seconds=seconds, output=row['output_tokens'], reasoning=row['reasoning_output_tokens'], waiting=record.get('waiting', 0.0), model=row['model'] or 'unknown'))
        else:
            excluded += 1
    seconds = sum(row['seconds'] for row in valid)
    rates = [row['output']/row['seconds'] for row in valid]
    groups = {}
    for row in valid:
        group = groups.setdefault(row['model'], dict(samples=0, output=0, seconds=0, rates=[]))
        group['samples'] += 1;group['output'] += row['output'];group['seconds'] += row['seconds'];group['rates'].append(row['output']/row['seconds'])
    for group in groups.values():
        group['effective_tps'] = round(group['output']/group['seconds'], 3)
        group['median_tps'] = round(statistics.median(group['rates']), 3)
        del group['rates']
    return dict(empty, effective_tps=round(sum(row['output'] for row in valid)/seconds, 3) if seconds else None, median_tps=round(statistics.median(rates), 3) if rates else None, visible_equivalent_tps=round(sum(row['output']-row['reasoning'] for row in valid)/seconds, 3) if seconds else None, waiting_seconds=round(sum(row['waiting'] for row in valid), 1), samples=len(valid), excluded_responses=excluded, recorded_responses=len(rows), matched_responses=matched, mismatched_responses=mismatched, last_response_at=max(row['time'] for row in rows), models=groups, coverage='complete' if matched==len(rows) else 'partial', error='usage_mismatch' if mismatched else None)

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--database', type=pathlib.Path, required=True)
    parser.add_argument('--codex-home', type=pathlib.Path, required=True)
    parser.add_argument('--cache', type=pathlib.Path, required=True)
    args=parser.parse_args()
    try:
        result=probe(args.database, args.codex_home, args.cache, dt.datetime.now(dt.timezone.utc).timestamp())
    except (OSError,sqlite3.Error,ValueError,TypeError,KeyError) as error:
        result=dict(effective_tps=None, samples=0, coverage='unavailable', error=type(error).__name__)
    print(json.dumps(result,ensure_ascii=True,separators=(',',':'),allow_nan=False))
