import datetime as dt
import pathlib
import sys
import unittest
import os
import subprocess

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'src'))
from machine_time import local_day_start_utc, format_local, machine_local


class MachineTimeTests(unittest.TestCase):
    def test_tokyo_day_changes_before_china_day(self):
        instant = dt.datetime(2026, 10, 22, 15, 30, tzinfo=dt.timezone.utc)
        tokyo = dt.timezone(dt.timedelta(hours=9))
        china = dt.timezone(dt.timedelta(hours=8))
        self.assertEqual(local_day_start_utc(now=instant, zone=tokyo).isoformat(), '2026-10-22T15:00:00+00:00')
        self.assertEqual(local_day_start_utc(now=instant, zone=china).isoformat(), '2026-10-21T16:00:00+00:00')

    def test_fractional_zone_and_calendar_lookback(self):
        instant = dt.datetime(2026, 1, 1, 0, 0, tzinfo=dt.timezone.utc)
        zone = dt.timezone(dt.timedelta(hours=5, minutes=30))
        self.assertEqual(local_day_start_utc(6, instant, zone).isoformat(), '2025-12-25T18:30:00+00:00')

    def test_os_selected_zone_and_explicit_display_offset(self):
        instant = dt.datetime(2026, 10, 22, 17, 0, tzinfo=dt.timezone.utc)
        shown = format_local(instant.isoformat())
        self.assertTrue(shown.startswith(machine_local(instant).strftime('%Y-%m-%d %H:%M:%S')))
        self.assertRegex(shown, r' UTC[+-]\d{2}:\d{2}$')
        boundary = local_day_start_utc(now=instant)
        self.assertEqual(machine_local(boundary).date(), machine_local(instant).date())
        self.assertEqual(machine_local(boundary).hour, 0)

    @unittest.skipUnless(os.name == 'nt', 'Windows machine zone is independent of CRT TZ')
    def test_inherited_tz_cannot_override_windows_zone(self):
        source = str(pathlib.Path(__file__).resolve().parents[1] / 'src')
        code = 'import sys;sys.path.insert(0,'+repr(source)+');from machine_time import format_local,local_day_start_utc;import datetime as dt;t=dt.datetime(2026,10,22,17,tzinfo=dt.timezone.utc);print(format_local(t.isoformat()));print(local_day_start_utc(now=t).isoformat())'
        expected = subprocess.check_output([sys.executable, '-c', code], text=True)
        environment = dict(os.environ, TZ='CET-1')
        actual = subprocess.check_output([sys.executable, '-c', code], env=environment, text=True)
        self.assertEqual(actual, expected)


if __name__ == '__main__':
    unittest.main()
