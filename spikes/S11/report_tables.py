"""Markdown tables for docs/spikes/S11-page-operations.md, generated from out/results/results.json (run `S11.Ops run` first)."""
import json
import re
import sys

rows = json.load(open(sys.argv[1] if len(sys.argv) > 1 else 'out/results/results.json', encoding='utf-8'))
by = {}
for r in rows:
    by.setdefault(r['case'], {})[r['mode']] = r


def short(field, v):
    if v is None:
        return '-'
    if v.startswith('ok'):
        return 'ok'
    if v.startswith('n/a'):
        return 'n/a'
    m = re.search(r'dangling (\d+)', v)
    if field == 'outline':
        if v.startswith('LOST'):
            return '**LOST**'
        if m and int(m.group(1)):
            return 'dead entries %s' % m.group(1)
        return 'BROKEN'
    if field == 'links':
        mm = re.search(r'missing (\d+), retargeted/extra (\d+), inert (\d+)', v)
        return ('missing %s, wrong %s, inert %s' % mm.groups()) if mm else 'BROKEN'
    if field == 'named':
        mm = re.search(r'expected (\d+), missing (\d+), wrong page (\d+)', v)
        return ('**LOST** (%s)' % mm.group(1)) if mm and int(mm.group(2)) == int(mm.group(1)) else (('missing %s, wrong page %s' % (mm.group(2), mm.group(3))) if mm else 'BROKEN')
    if field == 'labels':
        mm = re.search(r'WRONG on (\d+)/(\d+)', v)
        return ('shifted %s/%s' % mm.groups()) if mm else 'WRONG'
    if field == 'forms':
        if 'orphan fields' in v:
            return 'orphan fields'
        mm = re.search(r'AcroForm in catalog: (yes\(\d+\)|NO)', v)
        return ('BROKEN (AcroForm %s)' % mm.group(1)) if mm else 'BROKEN'
    if field == 'structure':
        return '**LOST**'
    if field == 'optionalContent':
        return 'BROKEN' + (' (layer visible)' if 'DIFFERENTLY' in v else '')
    if field == 'render':
        return v.replace('DIFFERENT on ', 'differs on ')
    if field == 'qpdf':
        return v.split(':')[0]
    if field == 'pdfwriterValidator':
        return 'FAIL' if v.startswith('FAIL') else v
    return v[:30]


cols = ['order', 'outline', 'links', 'named', 'labels', 'forms', 'annots', 'structure', 'optionalContent', 'render', 'geometry', 'qpdf', 'pdfwriterValidator', 'prefix']
head = ['order', 'outline', 'links', 'named dests', 'labels', 'forms', 'annots', 'tags', 'opt. content', 'render', 'geometry', 'qpdf', 'PdfWriter validator', 'prefix']
for title, pred in (('PDFium', lambda c: not c.startswith('asm-')), ('assembler prototype', lambda c: c.startswith('asm-'))):
    print('\n### %s\n' % title)
    print('| case | save | ' + ' | '.join(head) + ' |')
    print('|---|---|' + '---|' * len(cols))
    for case, modes in by.items():
        if not pred(case):
            continue
        for mode, r in modes.items():
            f = r['fields']
            print('| %s | %s | ' % (case, mode) + ' | '.join(short(c, f.get(c)) for c in cols) + ' |')
