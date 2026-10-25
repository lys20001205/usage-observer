"""Read existing quota samples only; never call an account or model endpoint."""
import argparse
import csv
from contextlib import closing
import datetime as dt
from decimal import Decimal, InvalidOperation
import json
import pathlib
import sqlite3

from machine_time import local_day_start_utc


def number(value):
    if isinstance(value, bool) or value is None:
        return None
    try:
        result = Decimal(str(value))
        return result if result.is_finite() and result >= 0 else None
    except InvalidOperation:
        return None


def timestamp(value):
    try:
        result = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
        return result.astimezone(dt.timezone.utc) if result.tzinfo else None
    except (ValueError, TypeError, AttributeError):
        return None


def analyze(rows, now=None, day_start=None):
    now = now or dt.datetime.now(dt.timezone.utc)
    day_start = day_start or local_day_start_utc(now=now)
    latest = None
    previous = None
    history = []
    today = Decimal(0)
    observed = Decimal(0)
    last_delta = None
    latest_valid = False
    marker = None
    segment = 0
    for stamp, payload in rows:
        instant = timestamp(stamp)
        if instant is None or instant > now + dt.timedelta(seconds=60):
            continue
        if latest is not None and instant <= latest:
            continue
        latest = instant
        try:
            quota = json.loads(payload)
            if not isinstance(quota, dict):
                raise ValueError('Invalid sample')
        except (ValueError, TypeError):
            latest_valid = False
            previous = None
            continue
        current_marker = quota.get('account_marker') or ''
        if marker is not None and current_marker != marker:
            previous = None
            today = observed = Decimal(0)
            last_delta = None
            segment += 1
        marker = current_marker
        balance = number(quota.get('credit_balance'))
        latest_valid = not quota.get('error') and balance is not None
        if not latest_valid:
            # Keep last value for the same known account, but never bridge failures.
            previous = None
            last_delta = None
            if history and history[-1]['segment'] != segment:
                history.append(dict(time=stamp, balance=None, delta=None, segment=segment))
            continue
        delta = None
        if previous is not None and (instant - previous[0]).total_seconds() <= 180:
            delta = balance - previous[1]
            decrease = max(-delta, Decimal(0))
            observed += decrease
            # Never attribute an interval crossing midnight entirely to today.
            if previous[0] >= day_start:
                today += decrease
        last_delta = delta
        history.append(dict(time=stamp, balance=str(balance), delta=str(delta) if delta is not None else None, segment=segment))
        previous = (instant, balance)
    active = [row for row in history if row['segment'] == segment and row['balance'] is not None]
    last = active[-1] if active else {}
    sample = timestamp(last.get('time'))
    return dict(balance=last.get('balance'), sampled_at=last.get('time'),
                stale=not latest_valid or sample is None or (now-sample).total_seconds() > 180,
                last_delta=str(last_delta) if last_delta is not None else None,
                today_decrease=str(today) if active else None,
                observed_decrease=str(observed) if active else None,
                started_at=active[0]['time'] if active else None,
                account_verified=bool(marker), samples=len(active),
                coverage='Observed decreases between valid samples at most 3 minutes apart; not a bill. Legacy samples cannot identify login switches.'), history


def read(database, output):
    with closing(sqlite3.connect(pathlib.Path(database).resolve().as_uri()+'?mode=ro', uri=True, timeout=3)) as connection:
        result, history = analyze(connection.execute('SELECT time,quota_json FROM samples ORDER BY id'))
    output = pathlib.Path(output)
    output.mkdir(parents=True, exist_ok=True)
    temporary = output/'credits-history.csv.tmp'
    with temporary.open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=['time', 'balance', 'delta', 'segment'])
        writer.writeheader()
        writer.writerows(history)
    temporary.replace(output/'credits-history.csv')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--database', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(read(args.database, args.output)))
    except Exception as error:
        print(json.dumps(dict(error=type(error).__name__, stale=True)))
