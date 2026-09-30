"""
Independent reference implementation of OMEGA feature set features-v1.

Written separately from the C# code (plain loops, no shared helpers) to cross-check it.
Generates deterministic synthetic 5m candles and the expected feature values at every candle,
each computed only from its trailing window, as documented in docs/FEATURES.md.

Usage: python3 generate_reference.py   (writes candles.csv and expected_features.csv)
"""
import csv, math
from datetime import datetime, timezone

N_CANDLES = 400
START_MS = int(datetime(2026, 1, 1, tzinfo=timezone.utc).timestamp() * 1000)
STEP_MS = 300_000

def lcg(seed):
    state = seed
    while True:
        state = (1103515245 * state + 12345) % 2**31
        yield state / 2**31

def make_candles():
    rnd = lcg(20260101)
    candles, close = [], 64000.00
    for i in range(N_CANDLES):
        open_ = close
        drift = (next(rnd) - 0.5) * 0.004 + 0.0004 * math.sin(i / 25)   # up to about +/-0.24 %
        close = round(open_ * (1 + drift), 2)
        high = round(max(open_, close) * (1 + next(rnd) * 0.0015), 2)
        low = round(min(open_, close) * (1 - next(rnd) * 0.0015), 2)
        volume = round(5 + next(rnd) * 40 + (30 if i % 37 == 0 else 0), 4)
        trades = 500 + int(next(rnd) * 2000)
        open_ms = START_MS + i * STEP_MS
        candles.append(dict(open_ms=open_ms, open=open_, high=high, low=low, close=close,
                            volume=volume, quote=round(volume * close, 4), trades=trades))
    return candles

def ema_seeded(values, period, alpha):
    avg = sum(values[:period]) / period
    for v in values[period:]:
        avg = alpha * v + (1 - alpha) * avg
    return avg

def ema_series(values, period, alpha):
    out = [None] * len(values)
    avg = sum(values[:period]) / period
    out[period - 1] = avg
    for i in range(period, len(values)):
        avg = alpha * values[i] + (1 - alpha) * avg
        out[i] = avg
    return out

def sample_std(values):
    m = sum(values) / len(values)
    return math.sqrt(sum((v - m) ** 2 for v in values) / (len(values) - 1))

def true_range(w, i):
    return max(w[i]['high'] - w[i]['low'], abs(w[i]['high'] - w[i-1]['close']), abs(w[i]['low'] - w[i-1]['close']))

def wilder_lb(n):
    return 1 + n + 10 * n

FEATURES = {}
def feature(name, lookback):
    def register(fn):
        FEATURES[name] = (lookback, fn)
        return fn
    return register

for k in (1, 3, 12):
    feature(f'log_return_{k}', k + 1)(lambda w: math.log(w[-1]['close'] / w[0]['close']))

feature('sma_20', 20)(lambda w: sum(c['close'] for c in w) / len(w))
feature('ema_20', 100)(lambda w: ema_seeded([c['close'] for c in w], 20, 2 / 21))
feature('ema_50', 250)(lambda w: ema_seeded([c['close'] for c in w], 50, 2 / 51))
feature('dist_ema_20', 100)(lambda w: w[-1]['close'] / ema_seeded([c['close'] for c in w], 20, 2 / 21) - 1)

@feature('rsi_14', wilder_lb(14))
def rsi(w):
    changes = [w[i]['close'] - w[i-1]['close'] for i in range(1, len(w))]
    g = ema_seeded([max(c, 0) for c in changes], 14, 1 / 14)
    l = ema_seeded([max(-c, 0) for c in changes], 14, 1 / 14)
    if l == 0:
        return None if g == 0 else 100.0
    return 100 - 100 / (1 + g / l)

feature('atr_14', wilder_lb(14))(lambda w: ema_seeded([true_range(w, i) for i in range(1, len(w))], 14, 1 / 14))

def macd_parts(w):
    closes = [c['close'] for c in w]
    fast, slow = ema_series(closes, 12, 2 / 13), ema_series(closes, 26, 2 / 27)
    line = [fast[i] - slow[i] for i in range(25, len(closes))]
    signal = ema_seeded(line, 9, 2 / 10)
    return line[-1], signal

feature('macd_line', 175)(lambda w: macd_parts(w)[0])
feature('macd_signal', 175)(lambda w: macd_parts(w)[1])
feature('macd_histogram', 175)(lambda w: macd_parts(w)[0] - macd_parts(w)[1])
feature('volatility_20', 21)(lambda w: sample_std([math.log(w[i]['close'] / w[i-1]['close']) for i in range(1, len(w))]))
feature('volume', 1)(lambda w: w[-1]['volume'])

@feature('volume_zscore_20', 21)
def vz(w):
    base = [c['volume'] for c in w[:-1]]
    s = sample_std(base)
    return None if s == 0 else (w[-1]['volume'] - sum(base) / len(base)) / s

@feature('adx_14', wilder_lb(14) + 14)
def adx(w):
    pdm, mdm, tr = [], [], []
    for i in range(1, len(w)):
        up, down = w[i]['high'] - w[i-1]['high'], w[i-1]['low'] - w[i]['low']
        pdm.append(up if up > down and up > 0 else 0.0)
        mdm.append(down if down > up and down > 0 else 0.0)
        tr.append(true_range(w, i))
    sp, sm, st = ema_series(pdm, 14, 1 / 14), ema_series(mdm, 14, 1 / 14), ema_series(tr, 14, 1 / 14)
    dx = []
    for i in range(13, len(tr)):
        pdi = 0 if st[i] == 0 else 100 * sp[i] / st[i]
        mdi = 0 if st[i] == 0 else 100 * sm[i] / st[i]
        dx.append(0 if pdi + mdi == 0 else 100 * abs(pdi - mdi) / (pdi + mdi))
    return ema_seeded(dx, 14, 1 / 14)

def main():
    candles = make_candles()
    with open('candles.csv', 'w', newline='') as f:
        out = csv.writer(f)
        out.writerow(['open_time_ms', 'open', 'high', 'low', 'close', 'base_volume', 'quote_volume', 'trade_count'])
        for c in candles:
            out.writerow([c['open_ms'], f"{c['open']:.2f}", f"{c['high']:.2f}", f"{c['low']:.2f}", f"{c['close']:.2f}",
                          f"{c['volume']:.4f}", f"{c['quote']:.4f}", c['trades']])
    # Features are computed from the values exactly as written (2 / 4 decimals).
    with open('candles.csv') as f:
        rows = list(csv.DictReader(f))
    candles = [dict(open_ms=int(r['open_time_ms']), open=float(r['open']), high=float(r['high']), low=float(r['low']),
                    close=float(r['close']), volume=float(r['base_volume'])) for r in rows]
    names = list(FEATURES)
    with open('expected_features.csv', 'w', newline='') as f:
        out = csv.writer(f)
        out.writerow(['index'] + names)
        for t in range(len(candles)):
            values = []
            for name in names:
                lookback, fn = FEATURES[name]
                v = None if t + 1 < lookback else fn(candles[t + 1 - lookback:t + 1])
                values.append('' if v is None else repr(v))
            out.writerow([t] + values)
    print(f'{len(candles)} candles, {len(names)} features')

if __name__ == '__main__':
    main()
