"""Read existing samples without importing recorder code or changing its database."""
import argparse,datetime as dt,json,pathlib,sqlite3,math

def read_history(database,limit=30000):
    path=pathlib.Path(database)
    if not path.is_file():
        return dict(points=[],total=0,truncated=False,source='samples',error='no_history')
    with sqlite3.connect(path.resolve().as_uri()+'?mode=ro',uri=True,timeout=2) as connection:
        connection.execute('PRAGMA query_only=ON')
        connection.row_factory=sqlite3.Row
        if not connection.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='samples'").fetchone():
            return dict(points=[],total=0,truncated=False,source='samples',error='no_samples')
        total=connection.execute('SELECT COUNT(*) FROM samples').fetchone()[0]
        rows=connection.execute('SELECT id,time,quota_json,health_json FROM samples ORDER BY id DESC LIMIT ?', (limit,)).fetchall()
    points=[]
    for row in reversed(rows):
        try:
            quota=json.loads(row['quota_json']);health=json.loads(row['health_json'])
            if not isinstance(quota,dict) or not isinstance(health,dict):raise ValueError()
            primary=quota.get('primary') or {};secondary=quota.get('secondary') or {}
            def number(value):
                return value if type(value) in (float,int) and math.isfinite(value) else None
            def window(value):
                return dict(used=number(value.get('usedPercent')),reset=number(value.get('resetsAt')),minutes=number(value.get('windowDurationMins')))
            hosts=[health.get(name) or {} for name in ('A','B')]
            covered=all(host.get('status')=='ok' for host in hosts)
            tokens=sum(max(0,number(host.get('added_tokens')) or 0) for host in hosts)
            marker=quota.get('account_marker');marker=marker if isinstance(marker,str) else None
            points.append(dict(id=row['id'],time=row['time'],primary=window(primary),secondary=window(secondary),account=marker,error=bool(quota.get('error')),error_category=(quota.get('error_details') or {}).get('category','unknown'),tokens=tokens,token_coverage=covered))
        except (ValueError,TypeError,AttributeError):
            points.append(dict(id=row['id'],time=row['time'],primary={},secondary={},account=None,error=True,error_category='invalid_record',tokens=0,token_coverage=False))
    return dict(points=points,total=total,truncated=total>len(points),source='samples',error=None)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--database',required=True);args=parser.parse_args()
    try:print(json.dumps(read_history(args.database),ensure_ascii=True,separators=(',',':'),allow_nan=False))
    except (sqlite3.Error,OSError):print(json.dumps(dict(points=[],total=0,truncated=False,source='samples',error='read_failed')))
