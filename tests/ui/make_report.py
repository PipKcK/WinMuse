"""Builds the Markdown test report from the unit-test results (TRX) and the stress results (phases JSON + CSV).

    python make_report.py <results dir> <out.md> [--tier Featured]

manual-findings.json (optional, in the results dir): [[severity, title, detail, [kinds it explains]], ...]. A written
finding replaces the automatic issues of the kinds it lists (e.g. "memory", "cpu-idle"), so nothing is reported twice.
"""
import csv
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

res_dir, out_md = sys.argv[1], sys.argv[2]
tier = sys.argv[sys.argv.index('--tier') + 1] if '--tier' in sys.argv else 'Featured'
limits = json.load(open(os.path.join(os.path.dirname(__file__), 'limits.json'), encoding='utf-8'))
OUTPUT_BUFFER_MS = 200  # audio queued ahead of the speakers: a render-thread delay shorter than this is not heard


def unit_results():
    trx = [f for f in os.listdir(res_dir) if f.endswith('.trx')]
    if not trx:
        return None
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    root = ET.parse(os.path.join(res_dir, sorted(trx)[-1])).getroot()
    rows = []
    for r in root.iter('{%s}UnitTestResult' % ns['t']):
        msg = r.find('.//t:Message', ns)
        out = r.find('.//t:StdOut', ns)
        rows.append((r.get('testName'), r.get('outcome'), (msg.text if msg is not None else '') or '', (out.text if out is not None else '') or ''))
    return rows


def verdict(value, expected, upper, higher_is_bad=True):
    if value is None:
        return 'n/a'
    if higher_is_bad:
        return 'PASS' if value <= expected else ('WARN' if value <= upper else 'FAIL')
    return 'PASS' if value >= expected else ('WARN' if value >= upper else 'FAIL')


lines = [f'# Music (WinMuse): {tier} test report', '']
issues = []  # (severity, title, detail, kind)

# ---- Unit tests
units = unit_results()
if units:
    passed = sum(1 for u in units if u[1] == 'Passed')
    lines += ['## Internal tests', '', f'{passed} of {len(units)} passed.', '']
    for name, outcome, msg, out in units:
        if outcome != 'Passed':
            issues.append(('High', f'Internal test failed: {name}', msg.strip()[:400], 'unit'))
    notes = [o.strip() for _, _, _, o in units if o.strip()]
    if notes:
        lines += ['Measurements from the tests:', ''] + [f'- {n}' for n in notes] + ['']

# ---- Stress sessions
for f in sorted(os.listdir(res_dir)):
    if not f.endswith('-phases.json'):
        continue
    r = json.load(open(os.path.join(res_dir, f), encoding='utf-8-sig'))
    label = r['label']
    # Render-thread delays (Debug builds log any over 50 ms); only ones longer than the output buffer are audible.
    log = os.path.join(res_dir, f'{label}-log.txt')
    gaps = [int(m) for m in re.findall(r'Render gap (\d+)', open(log, encoding='utf-8', errors='replace').read())] if os.path.exists(log) else []
    audible = [g for g in gaps if g >= OUTPUT_BUFFER_MS]
    gap_text = f"{len(gaps)} over 50 ms" + (f" (longest {max(gaps)} ms), {len(audible)} long enough to hear (≥ {OUTPUT_BUFFER_MS} ms)" if gaps else '')
    lines += [f'## Stress session: {label}', '',
              f"- Build: `{r['exe']}`", f"- Songs: {r.get('tracks')}, scan {r.get('scanSeconds')} s, automatic fetch {'on' if r.get('fetch') else 'off'}"
              + (f", songs fetched: {r.get('fetched')}" if r.get('fetch') else ''),
              f"- Finished normally: {r.get('ok')}" + (f" ({r.get('error')})" if r.get('error') else ''),
              f"- Unhandled errors in the log: {r.get('unhandled', 0)}; audio-thread delays: {gap_text}; decode-ahead ran dry: {r.get('decodeDry', 'n/a')}"]
    # Failed scripted UI actions are harness timing, not app faults: listed here, not as issues.
    for e in r.get('uiErrors') or []:
        lines.append(f'- Scripted UI action failed: {e}')
    lines.append('')
    if not r.get('ok'):
        issues.append(('High', f'{label}: session did not finish', r.get('error', ''), 'session'))
    if r.get('unhandled', 0):
        issues.append(('High', f'{label}: {r["unhandled"]} unhandled errors in the app log', 'See the session log.', 'unhandled'))
    if audible:
        issues.append(('High', f'{label}: {len(audible)} audible audio gaps', f'Longest {max(audible)} ms.', 'audio'))
    elif gaps:
        issues.append(('Low', f'{label}: {len(gaps)} audio-thread delays (not audible)', f'Longest {max(gaps)} ms; the {OUTPUT_BUFFER_MS} ms output buffer covered them.', 'audio'))
    if r.get('tracks') and r.get('scanSeconds'):
        rate = r['tracks'] / max(1, r['scanSeconds'])
        lim = limits['scanSongsPerSecond']
        v = verdict(rate, lim['expected'], lim['lower'], higher_is_bad=False)
        lines += [f"Scan speed: {rate:.0f} songs/s ({v}; expected ≥ {lim['expected']}, lower limit {lim['lower']})", '']
        if v == 'FAIL':
            issues.append(('Medium', f'{label}: library scan slow ({rate:.0f} songs/s)', '', 'scan'))
    lines += ['| Phase | Seconds | CPU avg % | CPU p95 % | CPU max % | Memory (private, max MB) | Threads max | Handles max | Verdict |',
              '|---|---|---|---|---|---|---|---|---|']
    for p in r['phases']:
        lim = limits['cpu'].get(p['phase'])
        v = verdict(p['cpuAvg'], lim['expected'], lim['upper']) if lim else '—'
        if lim and 'p95Upper' in lim and p['cpuP95'] > lim['p95Upper']:
            v = 'FAIL'
        lines.append(f"| {p['phase']} | {p['seconds']} | {p['cpuAvg']} | {p['cpuP95']} | {p['cpuMax']} | {p['privMax']} | {p['threadsMax']} | {p['handlesMax']} | {v} |")
        if v == 'FAIL':
            issues.append(('Medium', f"{label}: CPU too high in '{p['phase']}' (avg {p['cpuAvg']}%, p95 {p['cpuP95']}%)", f"Limit: avg ≤ {lim['upper']}%", 'cpu-' + p['phase']))
        elif v == 'WARN':
            issues.append(('Low', f"{label}: CPU above the expected level in '{p['phase']}' (avg {p['cpuAvg']}%)", f"Expected ≤ {lim['expected']}%, limit {lim['upper']}%", 'cpu-' + p['phase']))
    lines.append('')
    phases = {p['phase']: p for p in r['phases']}
    mem_max = max(p['privMax'] for p in r['phases'])
    mv = verdict(mem_max, limits['memoryPrivateMb']['expected'], limits['memoryPrivateMb']['upper'])
    lines.append(f"Peak memory: {mem_max} MB private ({mv}; expected ≤ {limits['memoryPrivateMb']['expected']}, limit {limits['memoryPrivateMb']['upper']})")
    if mv != 'PASS':
        issues.append(('Medium' if mv == 'FAIL' else 'Low', f'{label}: peak memory {mem_max} MB', '', 'memory'))
    if 'idle' in phases and 'settle' in phases:
        growth = phases['settle']['privMax'] - phases['idle']['privMax']
        gv = verdict(growth, limits['memoryGrowthMb']['expected'], limits['memoryGrowthMb']['upper'])
        lines.append(f"Memory growth over the session (settle minus idle): {growth:+} MB ({gv})")
        if gv != 'PASS':
            issues.append(('Medium' if gv == 'FAIL' else 'Low', f'{label}: memory grew {growth} MB during the session', 'Possible leak; needs a longer soak to confirm.', 'memory'))
    lines.append('')

extra = os.path.join(res_dir, 'manual-findings.json')
if os.path.exists(extra):
    for item in json.load(open(extra, encoding='utf-8')):
        sev, title, detail = item[:3]
        explains = item[3] if len(item) > 3 else []
        issues = [i for i in issues if i[3] not in explains]
        issues.append((sev, title, detail, 'manual'))

order = {'High': 0, 'Medium': 1, 'Low': 2}
lines += ['## Issues found (not fixed)', '']
if not issues:
    lines.append('None.')
for sev, title, detail, _ in sorted(issues, key=lambda i: order.get(i[0], 3)):
    lines.append(f'- **{sev}**: {title}' + (f'. {detail}' if detail else ''))
open(out_md, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print(out_md)
