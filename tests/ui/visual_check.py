"""Checks the screenshots from Visual-App.ps1 and builds contact sheets for review.

    python visual_check.py <shots dir> [--baseline <dir>] [--save-baseline]

Automatic checks: a capture that is blank or nearly one flat color (a page that failed to draw), and, when a baseline
exists, how much each view changed since then. Album art and the playing song differ between runs, so the baseline
difference is a pointer for review, not a pass mark. Writes checks.json and one contact sheet per theme and size.
"""
import json
import os
import shutil
import sys
from PIL import Image, ImageChops, ImageDraw, ImageStat

shots = sys.argv[1]
baseline = sys.argv[sys.argv.index('--baseline') + 1] if '--baseline' in sys.argv else None
files = sorted(f for f in os.listdir(shots) if f.endswith('.png') and not f.startswith('sheet-'))
results = []
for f in files:
    im = Image.open(os.path.join(shots, f)).convert('RGB')
    stat = ImageStat.Stat(im.convert('L'))
    # Share of the image that is one exact color: near 1.0 means nothing was drawn.
    small = im.resize((160, 100))
    top = max(small.getcolors(160 * 100), key=lambda c: c[0])[0] / (160 * 100)
    r = {'file': f, 'size': im.size, 'stddev': round(stat.stddev[0], 1), 'flat': round(top, 3)}
    r['blank'] = stat.stddev[0] < 4 or top > 0.97
    if baseline and os.path.exists(os.path.join(baseline, f)):
        b = Image.open(os.path.join(baseline, f)).convert('RGB')
        if b.size == im.size:
            d = ImageChops.difference(im, b).convert('L').point(lambda v: 255 if v > 40 else 0)
            r['changedPct'] = round(100 * ImageStat.Stat(d).mean[0] / 255, 1)
        else:
            r['changedPct'] = None
            r['note'] = f'size changed from {b.size}'
    results.append(r)

# Contact sheets: one per theme and size, each capture scaled to 480 px wide with its view name.
groups = {}
for f in files:
    theme, size = f.split('-')[:2]
    groups.setdefault(f'{theme}-{size}', []).append(f)
for key, group in groups.items():
    thumbs = []
    for f in group:
        im = Image.open(os.path.join(shots, f)).convert('RGB')
        im.thumbnail((480, 480))
        thumbs.append((f.split('-', 2)[2][:-4], im))
    cols = 3
    cw = 480; ch = max(t.size[1] for _, t in thumbs) + 22
    sheet = Image.new('RGB', (cols * cw, ((len(thumbs) + cols - 1) // cols) * ch), (128, 128, 128))
    dr = ImageDraw.Draw(sheet)
    for i, (name, t) in enumerate(thumbs):
        x, y = (i % cols) * cw, (i // cols) * ch
        dr.text((x + 4, y + 4), name, fill=(255, 255, 255))
        sheet.paste(t, (x, y + 20))
    sheet.save(os.path.join(shots, f'sheet-{key}.png'))

json.dump(results, open(os.path.join(shots, 'checks.json'), 'w'), indent=1)
blank = [r['file'] for r in results if r['blank']]
print(f'{len(results)} captures, {len(blank)} blank' + (': ' + ', '.join(blank) if blank else ''))
for r in results:
    if r.get('changedPct') is not None:
        print(f"  {r['file']}: {r['changedPct']}% changed since baseline")
if '--save-baseline' in sys.argv and baseline:
    os.makedirs(baseline, exist_ok=True)
    for f in files:
        shutil.copy(os.path.join(shots, f), os.path.join(baseline, f))
    print('baseline saved to', baseline)
