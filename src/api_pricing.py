"""Deterministic API Standard token valuation, with a versioned official rate card."""
from decimal import Decimal, InvalidOperation, ROUND_FLOOR, ROUND_HALF_UP

VERSION = '2026-11-07-api-standard-v1'
SOURCE = 'https://developers.openai.com/api/docs/pricing'
FIELDS = ('input_tokens', 'cached_input_tokens', 'cache_write_input_tokens',
          'output_tokens', 'reasoning_output_tokens', 'total_tokens')
RATES = {
    'gpt-6-astra': ('10', '1', '12.5', '50'),
    'gpt-6.1-sol': ('2', '.10', '2.5', '10'),
    'gpt-6.1-terra': ('1', '.05', '1.25', '5'),
    'gpt-6-luna': ('.10', '.01', '.125', '.50'),
}
MILLION = Decimal(1000000)


def tokens(value):
    if not isinstance(value, dict):
        raise ValueError('invalid_tokens')
    result = {}
    for key in FIELDS:
        number = value.get(key, 0)
        if type(number) is not int or not 0 <= number <= 9007199254740991:
            raise ValueError('invalid_tokens')
        result[key] = number
    if (result['total_tokens'] != result['input_tokens'] + result['output_tokens']
            or result['cached_input_tokens'] + result['cache_write_input_tokens'] > result['input_tokens']
            or result['reasoning_output_tokens'] > result['output_tokens']):
        raise ValueError('inconsistent_tokens')
    return result


def money(value):
    try:
        result = Decimal(str(value))
    except (InvalidOperation, ValueError):
        raise ValueError('invalid_amount') from None
    if not result.is_finite() or not 0 <= result <= Decimal('1000000000000'):
        raise ValueError('invalid_amount')
    return result


def price(model, usage, long_context=False):
    usage = tokens(usage)
    if model not in RATES:
        return None
    inp, cached, write, out = map(Decimal, RATES[model])
    im = Decimal(2 if long_context else 1)
    om = Decimal('1.5') if long_context else Decimal(1)
    uncached = usage['input_tokens'] - usage['cached_input_tokens'] - usage['cache_write_input_tokens']
    return ((uncached * inp + usage['cached_input_tokens'] * cached
             + usage['cache_write_input_tokens'] * write) * im
            + usage['output_tokens'] * out * om) / MILLION


def summarize(events):
    models = {}
    for event in events:
        model = event.get('model') or 'unknown'
        entry = models.setdefault(model, {'short': dict.fromkeys(FIELDS, 0),
                                         'long': dict.fromkeys(FIELDS, 0), 'requests': 0})
        usage = tokens(event)
        bucket = entry['long' if usage['input_tokens'] > 272000 else 'short']
        for field in FIELDS:
            bucket[field] += usage[field]
        entry['requests'] += 1
    total = dict.fromkeys(FIELDS, 0)
    amount = Decimal(0)
    unpriced = 0
    for model, entry in models.items():
        subtotal = Decimal(0)
        for tier in ('short', 'long'):
            usage = entry[tier]
            for field in FIELDS:
                total[field] += usage[field]
            cost = price(model, usage, tier == 'long')
            if cost is None:
                unpriced += usage['total_tokens']
            else:
                subtotal += cost
        entry['usd'] = str(subtotal) if model in RATES else None
        amount += subtotal
    return {'tokens': total, 'models': models, 'priced_usd': str(amount),
            'usd': str(amount) if unpriced == 0 else None,
            'unpriced_tokens': unpriced, 'requests': sum(e['requests'] for e in models.values())}


def validate_summary(summary):
    if not isinstance(summary, dict) or not isinstance(summary.get('models'), dict) or len(summary['models']) > 80:
        raise ValueError('invalid_api_summary')
    events = []
    request_count = 0
    for model, entry in summary['models'].items():
        if not isinstance(model, str) or len(model) > 80 or not isinstance(entry, dict):
            raise ValueError('invalid_api_model')
        count = entry.get('requests')
        if type(count) is not int or not 0 <= count <= 1000000000:
            raise ValueError('invalid_api_requests')
        request_count += count
        subtotal = Decimal(0)
        for tier in ('short', 'long'):
            usage = tokens(entry.get(tier))
            cost = price(model, usage, tier == 'long')
            if cost is not None:
                subtotal += cost
        if (model in RATES and money(entry.get('usd')) != subtotal
                or model not in RATES and entry.get('usd') is not None):
            raise ValueError('api_price_mismatch')
    total = dict.fromkeys(FIELDS, 0)
    amount = Decimal(0)
    unpriced = 0
    for model, entry in summary['models'].items():
        for tier in ('short', 'long'):
            for field in FIELDS:
                total[field] += entry[tier].get(field, 0)
            cost = price(model, entry[tier], tier == 'long')
            if cost is None:
                unpriced += entry[tier]['total_tokens']
            else:
                amount += cost
    if (tokens(summary.get('tokens')) != total or money(summary.get('priced_usd')) != amount
            or summary.get('unpriced_tokens') != unpriced or summary.get('requests') != request_count
            or (summary.get('usd') is None) != (unpriced > 0)
            or summary.get('usd') is not None and money(summary['usd']) != amount):
        raise ValueError('api_summary_mismatch')
    return summary


def allocate(amounts, budget, used_percent):
    budget = money(budget)
    used = money(used_percent)
    if used > 100:
        raise ValueError('invalid_used_percent')
    if any(value is None for value in amounts.values()):
        return None
    values = {key: money(value) for key, value in amounts.items()}
    total = sum(values.values())
    if not total:
        return None
    def split(units):
        units=int(Decimal(units).quantize(Decimal(1),rounding=ROUND_HALF_UP))
        exact={key:value/total*units for key,value in values.items()}
        floors={key:int(value.to_integral_value(rounding=ROUND_FLOOR)) for key,value in exact.items()}
        order=sorted(values,key=lambda key:(-(exact[key]-floors[key]),key))
        for key in order[:units-sum(floors.values())]:floors[key]+=1
        return {key:str(Decimal(value)/100) for key,value in floors.items()}
    share={key:str((value/total*100).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)) for key,value in values.items()}
    percent={key:str((value/total*used).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)) for key,value in values.items()}
    amount=split(budget*used)
    return {key:{'share_percent':share[key],'budget_percent':percent[key],
                 'budget_usd':amount[key]} for key in values}
