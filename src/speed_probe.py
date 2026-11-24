"""Read-only request throughput grouped by chat, model and configured speed tier.

Use recorded request-phase boundaries, never infer time from output-item cadence.
The interval includes first-token waiting and transport; explicit tool intervals
are subtracted. The local rollout does not supply a pure decoder clock.
Only timestamps, token counts and setting metadata are cached.
"""
import argparse
import datetime as dt
import hashlib
import json
import math
import os
import pathlib
import re
import sqlite3
import statistics
import tempfile
from contextlib import contextmanager

FIELDS = ('input_tokens', 'cached_input_tokens', 'output_tokens', 'reasoning_output_tokens', 'total_tokens')
CACHE_VERSION = 6
WINDOW_SECONDS = 300
HISTORY_SECONDS = 1800
ITEM_TYPES = ('reasoning', 'message', 'custom_tool_call', 'function_call', 'local_shell_call',
              'apply_patch_call', 'web_search_call', 'mcp_tool_call')
CALL_TYPES = ITEM_TYPES[2:]
OUTPUT_TYPES = ('custom_tool_call_output', 'function_call_output', 'local_shell_call_output',
                'apply_patch_call_output', 'web_search_call_output', 'mcp_tool_call_output')
KEEP_WORDS = tuple(('"' + word + '"').encode() for word in (
    'token_usage_record', 'turn_context', 'thread_settings_applied', 'task_started',
    'task_complete', 'turn_aborted', 'item_completed', *ITEM_TYPES, *OUTPUT_TYPES))
METHOD = '请求输出速度：按聊天、模型和设定档位分组；含思考、首字等待与传输；扣除显式工具执行'


def stamp(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


def iso(value):
    return dt.datetime.fromtimestamp(value, dt.timezone.utc).isoformat()


def tier(value):
    value = str(value or '').lower()
    return {'default': 'standard', 'auto': 'unknown', 'fast': 'fast',
            'priority': 'fast', 'standard': 'standard', 'ultrafast': 'ultrafast'}.get(value, 'unknown')


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
    handle, temporary = tempfile.mkstemp(prefix='speed-cache-', dir=str(path.parent))
    try:
        with os.fdopen(handle, 'w', encoding='utf-8') as stream:
            json.dump(cache, stream, ensure_ascii=True, separators=(',', ':'))
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def boundary(cur, when, source):
    cur.update(anchor=when, anchor_source=source, first_item=None, request_settings=None, tool_intervals=[])


def elapsed_tools(intervals, start, end):
    spans = sorted((max(start, a), min(end, b)) for a, b in intervals if b > start and a < end)
    total = 0.0
    previous = start
    for a, b in spans:
        a = max(a, previous)
        if b > a:
            total += b - a
            previous = b
    return total


def parse_file(path, previous, begin):
    stat = path.stat()
    with path.open('rb') as source:
        prefix = hashlib.sha256(source.read(512)).hexdigest()
    cur = previous if isinstance(previous, dict) else {}
    if (stat.st_size < cur.get('offset', 0) or stat.st_mtime_ns < cur.get('mtime', 0)
            or cur.get('prefix', prefix) != prefix):
        cur = {}
    cur.setdefault('offset', 0)
    cur.setdefault('responses', {})
    cur.setdefault('settings', {})
    cur.setdefault('open_calls', {})
    cur.setdefault('tool_intervals', [])
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
                if sub == 'thread_settings_applied':
                    settings = payload.get('thread_settings') or {}
                    for key in ('model', 'service_tier'):
                        if key in settings:
                            cur['settings'][key] = settings[key]
                    effort = settings.get('reasoning_effort', settings.get('effort'))
                    if effort is not None:
                        cur['settings']['effort'] = effort
                elif kind == 'turn_context':
                    # Settings changed during a running turn only apply to the next
                    # turn. A repeated context from compaction is not a new turn.
                    if cur.get('turn_active') and not cur.get('context_seen'):
                        for key in ('model', 'service_tier'):
                            if key in payload:
                                cur['active_settings'][key] = payload[key]
                        if payload.get('effort') is not None:
                            cur['active_settings']['effort'] = payload['effort']
                        cur['context_seen'] = True
                elif kind == 'event_msg' and sub == 'task_started':
                    boundary(cur, when, 'task_start')
                    cur['open_calls'] = {}
                    cur['turn_active'] = True
                    cur['turn_id'] = payload.get('turn_id') or ('at:' + str(when))
                    cur['active_settings'] = dict(cur['settings'])
                    cur['context_seen'] = False
                elif kind == 'event_msg' and sub in ('task_complete', 'turn_aborted'):
                    boundary(cur, None, 'closed')
                    cur['open_calls'] = {}
                    cur['turn_active'] = False
                elif kind == 'event_msg' and sub == 'item_completed':
                    item = payload.get('item') or {}
                    if item.get('type') in ('CommandExecution', 'McpToolCall', 'WebSearch', 'DynamicToolCall'):
                        a, b = payload.get('started_at_ms'), payload.get('completed_at_ms')
                        if isinstance(a, (int, float)) and isinstance(b, (int, float)) and b > a:
                            cur['tool_intervals'].append([a / 1000, b / 1000])
                elif kind == 'response_item' and sub in OUTPUT_TYPES:
                    call = cur['open_calls'].pop(payload.get('call_id'), None)
                    anchor = cur.get('anchor')
                    if call is not None and anchor is not None and call >= anchor and cur.get('first_item') is not None:
                        cur['tool_intervals'].append([call, when])
                    else:
                        boundary(cur, when, 'tool_result')
                elif kind == 'response_item' and sub in ITEM_TYPES:
                    if cur.get('first_item') is None and cur.get('anchor') is not None:
                        # A trailing item belongs to the response just completed.
                        if cur.get('closed_at') is None or when - cur['closed_at'] > 2:
                            cur['first_item'] = when
                            cur['request_settings'] = dict(cur.get('active_settings', {}))
                    if sub in CALL_TYPES and payload.get('call_id'):
                        cur['open_calls'][payload['call_id']] = when
                elif kind == 'token_usage_record':
                    response_id = payload.get('response_id')
                    if not response_id or response_id in cur['responses']:
                        continue
                    anchor = cur.get('anchor')
                    if when >= begin and anchor is not None and when > anchor:
                        usage = payload.get('usage') or {}
                        settings = cur.get('request_settings') or cur.get('active_settings', {})
                        tool_seconds = elapsed_tools(cur['tool_intervals'], anchor, when)
                        cur['responses'][response_id] = dict(
                            start=anchor, end=when, seconds=when - anchor - tool_seconds,
                            tool_seconds=tool_seconds, timing_source=cur.get('anchor_source'),
                            model=settings.get('model'), speed=tier(settings.get('service_tier')),
                            effort=settings.get('effort'), turn_id=cur.get('turn_id'),
                            tier_source='response' if payload.get('service_tier') else 'turn_start',
                            usage={key: usage.get(key, 0) for key in FIELDS})
                        if payload.get('service_tier'):
                            cur['responses'][response_id]['speed'] = tier(payload['service_tier'])
                    boundary(cur, when, 'previous_response')
                    cur['closed_at'] = when
            except (ValueError, KeyError, TypeError, AttributeError, OverflowError):
                boundary(cur, None, 'invalid_boundary')
                cur['parse_errors'] = cur.get('parse_errors', 0) + 1
        cur['offset'] = stream.tell()
    cur.update(mtime=stat.st_mtime_ns, prefix=prefix)
    cur['responses'] = {key: value for key, value in cur['responses'].items() if value['end'] >= begin - 900}
    return cur


def statistics_for(rows):
    seconds = sum(row['seconds'] for row in rows)
    output = sum(row['output'] for row in rows)
    last = max(rows, key=lambda row: row['end']) if rows else None
    return dict(effective_tps=round(output / seconds, 3) if seconds else None,
                median_tps=round(statistics.median(row['output'] / row['seconds'] for row in rows), 3) if rows else None,
                last_tps=round(last['output'] / last['seconds'], 3) if last else None,
                samples=len(rows), output_tokens=output, seconds=round(seconds, 6),
                tool_seconds=round(sum(row['tool_seconds'] for row in rows), 6),
                last_response_at=iso(last['end']) if last else None)


def runtime_tiers(home, session_ids, begin, now):
    """Prefer the active turn's execution config when the local log records it."""
    path = home / 'logs_2.sqlite'
    if not path.is_file() or not session_ids:
        return {}
    result = {}
    try:
        with readonly(path) as connection:
            marks = ','.join('?' for _ in session_ids)
            rows = connection.execute(
                "SELECT thread_id,feedback_log_body FROM logs WHERE ts>=? AND ts<=? "
                "AND target='feedback_tags' AND thread_id IN (" + marks + ") "
                "AND feedback_log_body LIKE '%service_tier%' ORDER BY id DESC LIMIT 2000",
                (int(begin), int(now) + 1, *session_ids))
            for row in rows:
                body = row['feedback_log_body'] or ''
                turn = re.search(r'\bturn[._]id="?([0-9a-f-]{36})', body)
                value = re.search(r'"service_tier"\s*:\s*"([^"]+)"', body)
                if turn and value and tier(value.group(1)) != 'unknown':
                    result.setdefault((row['thread_id'], turn.group(1)), tier(value.group(1)))
    except (sqlite3.Error, OSError):
        pass
    return result


def probe(database, home, cache_path, now):
    begin = now - HISTORY_SECONDS
    identity = str(database.resolve()) + '|' + str(home.resolve())
    cache = load_cache(cache_path, identity)
    empty = dict(sampled_at=iso(now), window_seconds=WINDOW_SECONDS, history_window_seconds=HISTORY_SECONDS,
                 method=METHOD, effective_tps=None, median_tps=None, visible_equivalent_tps=None,
                 samples=0, excluded_responses=0, recorded_responses=0, matched_responses=0,
                 mismatched_responses=0, last_response_at=None, groups=[], selected_key=None,
                 coverage='unavailable', error=None)
    if not database.is_file():
        return dict(empty, error='no_database')
    with readonly(database) as connection:
        rows = connection.execute(
            "SELECT response_id,time,session_id,model,output_tokens,reasoning_output_tokens FROM events "
            "WHERE host='A' AND time>=? ORDER BY time DESC LIMIT 2001", (iso(begin),)).fetchall()
    if len(rows) > 2000:
        return dict(empty, error='response_limit_exceeded')
    rows = [row for row in rows if begin <= stamp(row['time']) <= now + 1]
    if not rows:
        return dict(empty, coverage='no_samples')
    ids = {row['session_id'] for row in rows}
    executed_tiers = runtime_tiers(home, ids, begin, now)
    paths, titles = {}, {}
    state = home / 'state_5.sqlite'
    if state.is_file():
        with readonly(state) as connection:
            columns = {row['name'] for row in connection.execute('PRAGMA table_info(threads)')}
            title_column = ("COALESCE(NULLIF(name,''),title,'') AS title" if 'name' in columns and 'title' in columns
                            else 'title' if 'title' in columns else "'' AS title")
            marks = ','.join('?' for _ in ids)
            for row in connection.execute('SELECT id,rollout_path,' + title_column + ' FROM threads WHERE id IN (' + marks + ')', tuple(ids)):
                path = pathlib.Path(row['rollout_path'])
                if path.is_file():
                    paths[row['id']] = path
                titles[row['id']] = ' '.join(str(row['title'] or row['id']).split())[:160]
    missing = ids - paths.keys()
    if missing:
        for folder in ('sessions', 'archived_sessions'):
            for path in (home / folder).rglob('*.jsonl'):
                sid = path.stem[-36:]
                if sid in missing:
                    paths[sid] = path
                    missing.remove(sid)
    data, active, sessions = {}, {}, {}
    for sid, path in paths.items():
        key = str(path)
        try:
            cur = parse_file(path, cache['files'].get(key), begin)
        except OSError:
            continue
        active[key] = cur
        sessions[sid] = cur
        data.update(cur['responses'])
    cache.update(files=active, sampled_at=now)
    write_cache(cache_path, cache)
    valid, reasons = [], {}
    matched = mismatched = 0
    for row in rows:
        record = data.get(row['response_id'])
        if not record:
            continue
        usage = record['usage']
        if usage['output_tokens'] != row['output_tokens'] or usage['reasoning_output_tokens'] != row['reasoning_output_tokens']:
            mismatched += 1
            continue
        matched += 1
        seconds, output, reasoning = record['seconds'], row['output_tokens'], row['reasoning_output_tokens']
        reason = ('invalid_duration' if not math.isfinite(seconds) or seconds <= 0 else
                  'empty_output' if output <= 0 else
                  'invalid_usage' if not 0 <= reasoning <= output else None)
        if reason:
            reasons[reason] = reasons.get(reason, 0) + 1
            continue
        actual_tier = executed_tiers.get((row['session_id'], record.get('turn_id')))
        selected_tier = record['speed'] if record['tier_source'] == 'response' else actual_tier or record['speed']
        valid.append(dict(seconds=seconds, output=output, reasoning=reasoning, end=record['end'],
                          tool_seconds=record['tool_seconds'], session_id=row['session_id'],
                          model=record.get('model') or row['model'] or 'unknown', speed=selected_tier,
                          effort=record.get('effort'), tier_source='runtime' if actual_tier else record['tier_source'], turn_id=record.get('turn_id')))
    grouped = {}
    for row in valid:
        key = '|'.join((row['session_id'], row['model'], row['speed']))
        grouped.setdefault(key, []).append(row)
    groups = []
    for key, members in grouped.items():
        first = members[0]
        session = sessions.get(first['session_id'], {})
        recent = [row for row in members if row['end'] >= now - WINDOW_SECONDS]
        groups.append(dict(key=key, session_id=first['session_id'],
                           title=titles.get(first['session_id'], first['session_id']),
                           model=first['model'], speed=first['speed'],
                           configured_speed=tier(session.get('settings', {}).get('service_tier')),
                           active_turn=bool(session.get('turn_active') and any(row.get('turn_id') == session.get('turn_id') for row in members)),
                           efforts=sorted({row['effort'] for row in members if row['effort']}),
                           recent=statistics_for(recent), history=statistics_for(members)))
    groups.sort(key=lambda group: group['history']['last_response_at'] or '', reverse=True)
    selected = next((group for group in groups if group['recent']['samples']), None)
    selected_rows = [row for row in valid if selected and '|'.join((row['session_id'], row['model'], row['speed'])) == selected['key']
                     and row['end'] >= now - WINDOW_SECONDS]
    stats = statistics_for(selected_rows)
    seconds = sum(row['seconds'] for row in selected_rows)
    return dict(empty, **{key: stats[key] for key in ('effective_tps', 'median_tps', 'samples', 'last_response_at')},
                visible_equivalent_tps=round(sum(row['output'] - row['reasoning'] for row in selected_rows) / seconds, 3) if seconds else None,
                groups=groups, selected_key=selected['key'] if selected else None,
                excluded_responses=sum(reasons.values()), excluded_reasons=reasons,
                recorded_responses=len(rows), matched_responses=matched, mismatched_responses=mismatched,
                coverage='complete' if matched == len(rows) else 'partial',
                error='usage_mismatch' if mismatched else None)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--database', type=pathlib.Path, required=True)
    parser.add_argument('--codex-home', type=pathlib.Path, required=True)
    parser.add_argument('--cache', type=pathlib.Path, required=True)
    args = parser.parse_args()
    try:
        result = probe(args.database, args.codex_home, args.cache, dt.datetime.now(dt.timezone.utc).timestamp())
    except (OSError, sqlite3.Error, ValueError, TypeError, KeyError) as error:
        result = dict(effective_tps=None, samples=0, coverage='unavailable', error=type(error).__name__, groups=[])
    print(json.dumps(result, ensure_ascii=True, separators=(',', ':'), allow_nan=False))
