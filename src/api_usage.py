"""Read local Codex rollouts into an independent incremental API-cost ledger."""
import argparse, collections, contextlib, datetime as dt, hashlib, io, json, os
import pathlib, re, socket, sqlite3, sys, time, uuid
from decimal import Decimal
import api_pricing as pricing

UTC = dt.timezone.utc
TAG = re.compile(r'^(unknown|[0-9a-f]{24})$')
MAX_PERIODS = 32


def now(): return dt.datetime.now(UTC)
def iso(epoch): return dt.datetime.fromtimestamp(epoch, UTC).isoformat()
def stamp(value): return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


def read(path, default=None):
    try: return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError): return {} if default is None else default


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + '.tmp')
    with temp.open('w', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False, separators=(',', ':'))
        f.flush()
    temp.replace(path)


def backend_root(data):
    value = read(data / 'legacy-source.json').get('root')
    if value:
        path = pathlib.Path(value)
        if path.is_absolute() and (path / 'monitor.py').is_file() and (path / 'status.json').is_file():
            return path
    return data


def device_identity(data):
    for name in ('github-sync-device.json', 'device-id.json'):
        value = read(data / name).get('id')
        try:
            if str(uuid.UUID(str(value))) == value:
                return value
        except ValueError: pass
    value = str(uuid.uuid4())
    save(data / 'github-sync-device.json', {'id': value})
    return value


def account_tags(value):
    if not value: return 'unknown', 'unknown'
    value = str(value)
    return (hashlib.sha256(('UsageObserver-account:' + value).encode()).hexdigest()[:24],
            hashlib.sha256(('chatgpt:' + value).encode()).hexdigest())


def canonical_path(path):
    value=os.path.abspath(os.path.normpath(str(path)))
    if value.startswith('\\\\?\\UNC\\'):value='\\\\'+value[8:]
    elif value.startswith('\\\\?\\'):value=value[4:]
    return pathlib.Path(value)


def open_ledger(data):
    c = sqlite3.connect(data / 'api-usage.sqlite', timeout=10)
    c.row_factory = sqlite3.Row
    c.executescript('''
        CREATE TABLE IF NOT EXISTS events(
            response_id TEXT PRIMARY KEY, ts REAL NOT NULL, session_id TEXT, model TEXT,
            account_tag TEXT, account_marker TEXT, input_tokens INTEGER, cached_input_tokens INTEGER,
            cache_write_input_tokens INTEGER, output_tokens INTEGER, reasoning_output_tokens INTEGER,
            total_tokens INTEGER, source TEXT);
        CREATE INDEX IF NOT EXISTS api_event_time ON events(ts);
        CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY,size INTEGER,mtime INTEGER,cursor TEXT);
    ''')
    return c


def manifest(home, since):
    db = home / 'state_5.sqlite'
    entries = []
    if db.is_file():
        with sqlite3.connect(db.resolve().as_uri() + '?mode=ro', uri=True, timeout=5) as c:
            columns = {r[1] for r in c.execute('PRAGMA table_info(threads)')}
            acct = 'creator_account_id' if 'creator_account_id' in columns else 'NULL'
            for sid, value, account in c.execute('SELECT id,rollout_path,' + acct + ' FROM threads WHERE updated_at>=?', (int(since),)):
                path = pathlib.Path(value)
                if not path.is_file() and pathlib.Path(str(path) + '.zst').is_file():
                    path = pathlib.Path(str(path) + '.zst')
                # Read only the selected Codex home's rollout folders.
                resolved = canonical_path(path.resolve())
                if not any(resolved.is_relative_to(canonical_path((home / folder).resolve())) for folder in ('sessions', 'archived_sessions')):
                    raise ValueError('rollout_outside_selected_home')
                entries.append((sid, path, account))
    else:
        for folder in ('sessions', 'archived_sessions'):
            for path in (home / folder).rglob('*.jsonl*'):
                if path.suffix not in ('.jsonl', '.zst'): continue
                entries.append((path.name, path, None))
    return entries


def _insert(c, key, when, sid, model, tag, marker, usage, source):
    usage = pricing.tokens(usage)
    if not isinstance(model,str) or not re.fullmatch(r'[A-Za-z0-9_.:-]{1,80}',model):model='unknown'
    c.execute('INSERT OR IGNORE INTO events VALUES(' + ','.join('?' * 13) + ')',
              (key, when, sid, model or 'unknown', tag, marker,
               *(usage[k] for k in pricing.FIELDS), source))


def scan_file(c, sid, path, account, since):
    stat = path.stat()
    cached = c.execute('SELECT size,mtime,cursor FROM files WHERE path=?', (str(path),)).fetchone()
    if cached and cached['size'] == stat.st_size and cached['mtime'] == stat.st_mtime_ns:
        return False
    compressed = path.suffix == '.zst'
    cursor = json.loads(cached['cursor']) if cached and not compressed and cached['size'] <= stat.st_size else {}
    offset = cursor.get('offset', 0)
    model = cursor.get('model')
    previous = cursor.get('previous') or dict.fromkeys(pricing.FIELDS, 0)
    first_modern = cursor.get('first_modern')
    tag, marker = account_tags(account)
    tag = cursor.get('tag', tag); marker = cursor.get('marker', marker)
    with path.open('rb') as raw:
        if compressed:
            import zstandard
            stream = io.BufferedReader(zstandard.ZstdDecompressor().stream_reader(raw), 4 * 1024 * 1024)
        else:
            raw.seek(offset); stream = raw
        try:
            while True:
                pos = stream.tell() if not compressed else 0
                line = stream.readline()
                if not line: break
                if not line.endswith(b'\n'):
                    if not compressed: stream.seek(pos)
                    break
                head = line[:250]
                if not any(s in head for s in (b'"session_meta"', b'"turn_context"', b'"token_usage_record"', b'"token_count"')):
                    continue
                item = json.loads(line)
                kind = item.get('type'); q = item.get('payload') or {}
                if kind == 'session_meta':
                    if q.get('creator_account_id'): tag, marker = account_tags(q['creator_account_id'])
                    continue
                if kind == 'turn_context':
                    model = q.get('model') or model; continue
                when = stamp(item['timestamp'])
                if kind == 'token_usage_record' and q.get('response_id'):
                    if first_modern is None:
                        first_modern = when
                        c.execute("DELETE FROM events WHERE session_id=? AND source='legacy' AND ts>=?", (sid, when - 2))
                    if when >= since:
                        _insert(c, q['response_id'], when, q.get('thread_id') or q.get('session_id') or sid,
                                model, tag, marker, q.get('usage') or {}, 'response')
                elif kind == 'event_msg' and q.get('type') == 'token_count':
                    info = q.get('info') or {}; current = info.get('total_token_usage')
                    if not isinstance(current, dict): continue
                    current = {k: int(current.get(k) or 0) for k in pricing.FIELDS}
                    delta = {k: current[k] - previous[k] for k in pricing.FIELDS}
                    if delta['total_tokens'] < 0 or delta['input_tokens'] < 0:
                        delta = {k: int((info.get('last_token_usage') or current).get(k) or 0) for k in pricing.FIELDS}
                    previous = current
                    if when >= since and delta['total_tokens'] > 0 and first_modern is None:
                        key = 'legacy:' + sid + ':' + item['timestamp'] + ':' + str(current['total_tokens'])
                        _insert(c, key, when, sid, model, tag, marker, delta, 'legacy')
            offset = stream.tell() if not compressed else stat.st_size
        finally:
            if compressed: stream.close()
    cursor = dict(offset=offset, model=model, previous=previous, first_modern=first_modern, tag=tag, marker=marker)
    c.execute('INSERT OR REPLACE INTO files VALUES(?,?,?,?)', (str(path), stat.st_size, stat.st_mtime_ns, json.dumps(cursor)))
    return True


def scan(c, data, home, since):
    entries = manifest(home, since)
    if not entries and not (home / 'sessions').is_dir(): raise FileNotFoundError('codex_logs_missing')
    errors = []; changed = 0
    for i, (sid, path, account) in enumerate(entries):
        if i % 10 == 0:
            save(data / 'api-usage-progress.json', {'state': 'indexing', 'done': i, 'total': len(entries)})
        try:
            with c: changed += int(scan_file(c, sid, path, account, since))
        except (OSError, ValueError, ImportError, sqlite3.Error) as e:
            errors.append({'code': 'archive_reader_missing' if isinstance(e, ImportError) else type(e).__name__, 'session': sid})
    with c: c.execute('DELETE FROM events WHERE ts<?', (since,))
    result = {'files': len(entries), 'changed': changed, 'errors': errors, 'complete': not errors}
    save(data / 'api-usage-progress.json', {'state': 'ready' if not errors else 'partial', **result})
    return result


def _window_valid(w, at):
    if not isinstance(w, dict) or not TAG.fullmatch(str(w.get('account_tag', 'unknown'))): return False
    if any(type(w.get(k)) not in (int, float) for k in ('start', 'scheduled_reset')): return False
    if not at - 65 * 86400 <= w['start'] <= at + 300 or not w['start'] < w['scheduled_reset'] <= w['start'] + 32 * 86400: return False
    first85 = w.get('first85')
    return first85 is None or type(first85) in (int, float) and w['start'] <= first85 <= min(at + 300, w['scheduled_reset'])


def merge_windows(windows, at):
    result = []
    for item in sorted((dict(w) for w in windows if _window_valid(w, at)), key=lambda w:w['start']):
        match = next((w for w in result if w.get('account_tag', 'unknown') == item.get('account_tag', 'unknown') and abs(w['start'] - item['start']) <= 120), None)
        if match is None:
            result.append(item); continue
        match['start'] = min(match['start'], item['start'])
        candidates = [v for v in (match.get('first85'), item.get('first85')) if v is not None]
        match['first85'] = min(candidates) if candidates else None
        if item.get('quota_at', 0) >= match.get('quota_at', 0):
            for k in ('percent', 'quota_at', 'scheduled_reset'): match[k] = item.get(k)
    return sorted(result, key=lambda w:w['start'])[-MAX_PERIODS:]


def quota_windows(c, database, shared, at):
    tags = {r['account_marker']:r['account_tag'] for r in c.execute("SELECT DISTINCT account_marker,account_tag FROM events WHERE account_tag!='unknown'")}
    single_tag = next(iter(tags.values())) if len(set(tags.values())) == 1 else 'unknown'
    windows = []
    if database.is_file():
        with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True, timeout=5) as q:
            for t, raw in q.execute('SELECT time,quota_json FROM samples WHERE time>=? ORDER BY id', (iso(at - 32*86400),)):
                value = json.loads(raw); when = stamp(t)
                if value.get('error') or when > at + 300: continue
                candidates = [value.get(k) or {} for k in ('primary', 'secondary')]
                p = max(candidates, key=lambda x:x.get('windowDurationMins') or 0)
                end = p.get('resetsAt'); minutes = p.get('windowDurationMins'); percent = p.get('usedPercent')
                if type(end) not in (int,float) or type(minutes) not in (int,float) or minutes <= 0: continue
                if type(percent) not in (int,float) or not 0 <= percent <= 100: continue
                tag = tags.get(value.get('account_marker'), single_tag if not value.get('account_marker') else 'unknown')
                start = end - minutes * 60
                match = next((w for w in reversed(windows) if w['account_tag'] == tag and abs(w['scheduled_reset']-end) <= 120), None)
                if match is None:
                    match = {'start':start,'scheduled_reset':end,'account_tag':tag,'first85':None}
                    windows.append(match)
                match.update(percent=percent, quota_at=when)
                if percent >= 85 and match['first85'] is None: match['first85'] = when
    return merge_windows(windows + list(shared), at)


def report(c, data, backend, scan_result, at):
    shared = read(data / 'api-shared-windows.json').get('windows', [])
    if not isinstance(shared, list): shared = []
    windows = quota_windows(c, backend / 'records.sqlite', shared[:96], at)
    periods = []
    for i, window in enumerate(windows):
        tag = window.get('account_tag', 'unknown')
        next_start = next((w['start'] for w in windows[i+1:] if w.get('account_tag','unknown') == tag), at)
        end = min(next_start, at)
        clause = '' if tag == 'unknown' else ' AND account_tag=?'
        def stats(stop):
            args = [window['start'],stop] + ([] if tag == 'unknown' else [tag])
            return pricing.summarize(dict(r) for r in c.execute('SELECT * FROM events WHERE ts>=? AND ts<?'+clause,args))
        p = dict(window, end=end, closed=next_start<at, start_at=iso(window['start']), end_at=iso(end),
                 coverage_complete=scan_result['complete'] and window['start']>=at-31*86400,
                 first_85_at=iso(window['first85']) if window.get('first85') is not None else None,
                 full=stats(end), at85=stats(window['first85']) if window.get('first85') is not None else None)
        periods.append(p)
    config = read(data / 'github-sync-settings.json')
    last = c.execute('SELECT MAX(ts) FROM events').fetchone()[0]
    result = {'schema':1,'price_version':pricing.VERSION,'currency':'USD','method':'API Standard',
              'generated_at':iso(at),'device_id':device_identity(data),
              'label':''.join(ch for ch in str(config.get('label') or socket.gethostname()) if ch.isprintable())[:32],
              'coverage_start':iso(at-31*86400),'last_recorded_at':iso(last) if last else None,
              'complete':scan_result['complete'],'scan_files':scan_result['files'],
              'scan_errors':len(scan_result['errors']),'periods':periods}
    save(data / 'api-usage-status.json', result)
    return result


def validate_report(value, at=None):
    at = at or time.time()
    if (not isinstance(value,dict) or value.get('schema') != 1 or value.get('currency') != 'USD'
            or value.get('price_version') != pricing.VERSION or type(value.get('complete')) is not bool):
        raise ValueError('unsupported_api_report')
    if stamp(value['generated_at']) > at+300: raise ValueError('future_api_report')
    periods = value.get('periods')
    if not isinstance(periods,list) or len(periods)>MAX_PERIODS: raise ValueError('invalid_api_periods')
    for p in periods:
        if not _window_valid(p,at): raise ValueError('invalid_api_window')
        pricing.validate_summary(p['full'])
        if p.get('at85') is not None:
            if p.get('first85') is None: raise ValueError('missing_api_cutoff')
            pricing.validate_summary(p['at85'])
    return value


def combined(data, local, at):
    peers = read(data / 'github-sync-devices.json').get('devices') or []
    cfg=read(data/'github-sync-settings.json')
    cloud_has_local=any(p.get('device_id')==local['device_id'] for p in peers)
    include_local=not peers or cloud_has_local or cfg.get('enabled') and cfg.get('upload_local',True)
    devices = [{'device_id':local['device_id'],'label':local['label'],'uploaded_at':local['generated_at'], 'is_local':True,'api_usage':local}] if include_local else []
    seen = {local['device_id']}
    for peer in peers:
        if peer.get('device_id') in seen or peer.get('is_local'): continue
        seen.add(peer.get('device_id')); devices.append(peer)
    config = read(data / 'api-settings.json')
    budget = str(pricing.money(config.get('budget_usd','200')))
    expected = config.get('expected_devices',3)
    if type(expected) is not int or not 1<=expected<=32: expected=3
    result = {'schema':1,'generated_at':iso(at),'budget_usd':budget,'expected_devices':expected,'periods':[]}
    basis=local['periods'] if include_local else next(((p.get('api_usage') or {}).get('periods',[]) for p in peers if p.get('api_usage')),[])
    for period in reversed(basis):
        for mode in ('full','at85'):
            if mode=='at85' and period.get('first85') is None: continue
            rows=[]; amounts={}; ready=True
            for device in devices:
                api=device.get('api_usage')
                matching=None
                try:
                    validate_report(api,at)
                    matching=next((p for p in api['periods'] if p.get('account_tag')==period.get('account_tag') and abs(p['start']-period['start'])<=120),None)
                except (ValueError,KeyError,TypeError): pass
                status='ready'; stat=None
                if not api: status='upgrade_required'
                elif matching is None: status='waiting_for_period'
                elif matching['start']!=period['start'] or mode=='at85' and matching.get('first85')!=period.get('first85'): status='aligning_cutoff'
                elif mode=='full' and period.get('closed') and abs(matching.get('end',0)-period.get('end',0))>1:status='waiting_for_period'
                elif not api.get('complete') or matching.get('coverage_complete') is False: status='partial_history'
                else:
                    stat=matching.get(mode)
                    if stat is None or stat.get('usd') is None: status='unpriced_model'
                    elif matching.get('account_tag')=='unknown':status='account_scope_unknown'
                    elif at-min(stamp(device.get('uploaded_at') or api['generated_at']),stamp(api['generated_at']))>1800:
                        status='saved_snapshot' if mode=='at85' or period.get('closed') else 'stale'
                value=stat.get('usd') if stat else None
                ready=ready and status in ('ready','saved_snapshot')
                key=device['device_id'];amounts[key]=value
                rows.append({'device_id':key,'label':device.get('label','Device'),'is_local':device.get('is_local',False),
                             'state':status,'api_usd':value,'priced_usd':stat.get('priced_usd') if stat else None,
                             'tokens':(stat or {}).get('tokens',{}).get('total_tokens'),
                             'updated_at':device.get('uploaded_at'),'models':(stat or {}).get('models',{})})
            complete=ready and len(rows)>=expected
            allocation=pricing.allocate(amounts,budget,85 if mode=='at85' else period.get('percent',0)) if complete else None
            if allocation:
                for row in rows: row.update(allocation[row['device_id']])
            result['periods'].append({'start':period['start'],'start_at':period['start_at'],'mode':mode,
                                      'first_85_at':period.get('first_85_at'),'used_percent':85 if mode=='at85' else period.get('percent'),
                                      'closed':period.get('closed',False),'complete':complete,'devices':rows,'total_usd':str(sum(pricing.money(v) for v in amounts.values())) if all(v is not None for v in amounts.values()) else None})
    save(data / 'api-combined.json',result)
    return result


def run_once(data, home=None, backend=None, at=None):
    data.mkdir(parents=True,exist_ok=True);backend=pathlib.Path(backend) if backend else backend_root(data)
    config=read(data/'connection-settings.json') or read(backend/'connection-settings.json')
    home=pathlib.Path(home or config.get('codex_home') or os.environ.get('CODEX_HOME') or pathlib.Path.home()/'.codex').resolve()
    at=at or time.time()
    with contextlib.closing(open_ledger(data)) as c:
        scan_result=scan(c,data,home,at-31*86400)
        local=report(c,data,backend,scan_result,at)
    combined(data,local,at)
    return local


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--data',required=True);parser.add_argument('--codex-home');parser.add_argument('--backend');parser.add_argument('--loop',action='store_true')
    args=parser.parse_args();data=pathlib.Path(args.data).resolve();data.mkdir(parents=True,exist_ok=True)
    lock=(data/'api-usage.lock').open('a+b');lock.seek(0)
    if os.name=='nt':
        import msvcrt
        try:msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        except OSError:return 2
    save(data/'api-usage-process.json',{'pid':os.getpid(),'script':str(pathlib.Path(__file__).resolve())})
    try:
        while not (data/'api-usage-stop.request').exists():
            try:
                backend=pathlib.Path(args.backend) if args.backend else backend_root(data)
                paused=bool(read(backend/'ui-settings.json').get('RecorderPaused'))
                if paused and args.loop:
                    save(data/'api-usage-progress.json',{'state':'paused'})
                else:run_once(data,args.codex_home,args.backend)
            except Exception as e:
                save(data/'api-usage-progress.json',{'state':'error','code':type(e).__name__,'updated_at':iso(time.time())})
                if not args.loop: raise
            if not args.loop:break
            for _ in range(60):
                if (data/'api-usage-stop.request').exists():break
                if (data/'api-usage.request').exists():
                    (data/'api-usage.request').unlink(missing_ok=True);break
                time.sleep(1)
    finally:
        if read(data/'api-usage-process.json').get('pid')==os.getpid():(data/'api-usage-process.json').unlink(missing_ok=True)
        lock.close()
    return 0


if __name__=='__main__':raise SystemExit(main())
