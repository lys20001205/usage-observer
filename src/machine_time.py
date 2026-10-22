"""OS-local presentation and calendar boundaries; persisted instants remain UTC."""
import datetime as dt
import os

if os.name == 'nt':
    import ctypes
    from ctypes import wintypes

    class _SystemTime(ctypes.Structure):
        _fields_ = [(name, wintypes.WORD) for name in
                    ('year', 'month', 'weekday', 'day', 'hour', 'minute', 'second', 'millisecond')]

    _kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    for _name in ('SystemTimeToTzSpecificLocalTimeEx', 'TzSpecificLocalTimeToSystemTimeEx'):
        _function = getattr(_kernel, _name)
        _function.argtypes = (ctypes.c_void_p, ctypes.POINTER(_SystemTime), ctypes.POINTER(_SystemTime))
        _function.restype = wintypes.BOOL

    def _windows_convert(value, to_utc=False):
        source = _SystemTime(value.year, value.month, (value.weekday()+1)%7,
                             value.day, value.hour, value.minute, value.second, value.microsecond//1000)
        target = _SystemTime()
        function = _kernel.TzSpecificLocalTimeToSystemTimeEx if to_utc else _kernel.SystemTimeToTzSpecificLocalTimeEx
        # NULL selects the actual active Windows zone, including dynamic DST.
        # Inherited TZ/CRT settings must not replace the machine's time zone.
        if not function(None, ctypes.byref(source), ctypes.byref(target)):
            raise ctypes.WinError(ctypes.get_last_error())
        return dt.datetime(target.year, target.month, target.day, target.hour,
                           target.minute, target.second, target.millisecond*1000+value.microsecond%1000)


def machine_local(instant):
    if os.name != 'nt':
        return instant.astimezone()
    utc = instant.astimezone(dt.timezone.utc)
    local = _windows_convert(utc)
    offset = local - utc.replace(tzinfo=None)
    return local.replace(tzinfo=dt.timezone(offset))


def local_day_start_utc(days_ago=0, now=None, zone=None):
    instant = now if now is not None else dt.datetime.now(dt.timezone.utc)
    local = machine_local(instant) if zone is None else instant.astimezone(zone)
    day = local.date() - dt.timedelta(days=days_ago)
    midnight = dt.datetime.combine(day, dt.time.min, tzinfo=zone)
    if zone is None and os.name == 'nt':
        return _windows_convert(midnight, to_utc=True).replace(tzinfo=dt.timezone.utc)
    # Resolve midnight using the rule for that date, not today's fixed offset.
    return midnight.astimezone(dt.timezone.utc)


def format_local(value):
    local = machine_local(dt.datetime.fromisoformat(value))
    offset = local.strftime('%z')
    return local.strftime('%Y-%m-%d %H:%M:%S') + ' UTC' + offset[:3] + ':' + offset[3:]
