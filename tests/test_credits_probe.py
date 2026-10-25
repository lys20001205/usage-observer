import datetime as dt
from contextlib import closing
import json
import pathlib
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]/'src'))
from credits_probe import analyze, read

NOW = dt.datetime(2026, 10, 25, 3, tzinfo=dt.timezone.utc)
DAY = NOW.replace(hour=0)


def sample(minutes, value, marker='a', **extra):
    return ((NOW+dt.timedelta(minutes=minutes)).isoformat(), json.dumps(dict(credit_balance=value, account_marker=marker, **extra)))


class CreditsTests(unittest.TestCase):
    def check_rows(self, rows):
        return analyze(rows, NOW, DAY)[0]

    def test_decrease_refill_and_decimal_precision(self):
        result = self.check_rows([sample(-3, '55635'), sample(-2, '55610.1399885000'), sample(-1, '55700'), sample(0, '55602.8756585000')])
        self.assertEqual(result['balance'], '55602.8756585000')
        self.assertEqual(result['today_decrease'], '121.9843530000')
        self.assertEqual(result['last_delta'], '-97.1243415000')
        self.assertFalse(result['stale'])

    def test_zero_is_valid_missing_is_not_zero(self):
        result = self.check_rows([sample(-1, '5'), sample(0, '0')])
        self.assertEqual(result['balance'], '0')
        self.assertFalse(result['stale'])
        result = self.check_rows([sample(-1, '5'), sample(0, None)])
        self.assertEqual(result['balance'], '5')
        self.assertTrue(result['stale'])

    def test_failed_sample_retains_value_and_breaks_interval(self):
        result = self.check_rows([sample(-2, '100'), sample(-1, None, error='timeout'), sample(0, '20')])
        self.assertEqual(result['today_decrease'], '0')
        self.assertIsNone(result['last_delta'])

    def test_account_switch_resets_totals_even_if_new_account_unavailable(self):
        result = self.check_rows([sample(-2, '100'), sample(-1, '80'), sample(0, '10', 'b')])
        self.assertEqual(result['today_decrease'], '0')
        self.assertIsNone(result['last_delta'])
        result = self.check_rows([sample(-1, '100'), sample(0, None, 'b')])
        self.assertIsNone(result['balance'])

    def test_gap_and_midnight_are_not_charged_to_today(self):
        result = self.check_rows([sample(-10, '100'), sample(0, '20')])
        self.assertEqual(result['today_decrease'], '0')
        rows = [((DAY-dt.timedelta(minutes=1)).isoformat(), json.dumps(dict(credit_balance='100'))), (DAY.isoformat(), json.dumps(dict(credit_balance='20')))]
        result = self.check_rows(rows)
        self.assertEqual(result['today_decrease'], '0')
        self.assertEqual(result['observed_decrease'], '80')
        self.assertFalse(result['account_verified'])

    def test_invalid_future_duplicates_and_staleness(self):
        result = self.check_rows([sample(-5, '100'), sample(-5, '1'), sample(2, '0')])
        self.assertEqual(result['balance'], '100')
        self.assertTrue(result['stale'])
        for invalid in ['NaN', 'Infinity', '-1', True, 'abc']:
            self.assertIsNone(self.check_rows([sample(0, invalid)])['balance'])

    def test_read_only_database_and_csv_contains_only_allowlisted_values(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            database = root/'records.sqlite'
            with closing(sqlite3.connect(database)) as connection:
                connection.execute('CREATE TABLE samples(id INTEGER PRIMARY KEY,time TEXT,quota_json TEXT)')
                connection.execute('INSERT INTO samples(time,quota_json) VALUES(?,?)', sample(0, '55635', secret='must-not-export'))
                connection.commit()
            original = database.read_bytes()
            result = read(database, root/'output')
            self.assertEqual(result['balance'], '55635')
            self.assertEqual(database.read_bytes(), original)
            csv = (root/'output'/'credits-history.csv').read_text(encoding='utf-8-sig')
            self.assertIn('55635', csv)
            self.assertNotIn('secret', csv)
            self.assertNotIn('must-not-export', csv)


if __name__ == '__main__':
    unittest.main()
