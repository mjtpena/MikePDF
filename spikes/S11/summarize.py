"""Prints, for every S11 case, a compact ok/BAD grid, and with a second argument the full text of every BAD field."""
import json
import sys

path = sys.argv[1] if len(sys.argv) > 1 else 'out/results/results.json'
rows = json.load(open(path, encoding='utf-8'))
cols = ['open', 'pages', 'order', 'outline', 'links', 'named', 'labels', 'forms', 'annots', 'structure',
        'optionalContent', 'render', 'geometry', 'qpdf', 'pdfwriterValidator', 'prefix']


def cell(v):
    if v is None:
        return '-'
    if v.startswith('ok'):
        return 'ok'
    if v.startswith('n/a'):
        return 'n/a'
    return 'BAD'


print('%-22s %-4s ' % ('case', 'mode') + ' '.join('%-7s' % c[:7] for c in cols))
for r in rows:
    f = r['fields']
    print('%-22s %-4s ' % (r['case'], r['mode']) + ' '.join('%-7s' % cell(f.get(c)) for c in cols))
if len(sys.argv) > 2:
    only = sys.argv[2:]
    for r in rows:
        if only != ['all'] and r['case'] not in only:
            continue
        f = r['fields']
        bad = {k: v for k, v in f.items() if cell(v) == 'BAD'}
        print('\n== %s (%s) %s %s' % (r['case'], r['mode'], r.get('notes', ''), r.get('catalog', '')))
        for k, v in bad.items():
            print('   %s: %s' % (k, v))
