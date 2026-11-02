"""S8.1 cross-check: what PDFium's signature API reports vs what the S8.2 parser sees, per fixture.

  .venv/Scripts/python.exe -I crossvalidate/enum_compare.py out/fixtures out/enum-fixtures
"""
import json, os, sys
d, e = sys.argv[1], sys.argv[2]
quire = {r['Id']: r for r in json.load(open(os.path.join(d, 'quire.json'))) if not r.get('Missing')}
enum = {os.path.splitext(os.path.basename(r['File']))[0]: r for r in json.load(open(os.path.join(e, 'enum.json')))}
rows = []
for fid, q in quire.items():
    p = enum.get(fid)
    if not p: continue
    qs = [s for s in q['Report']['Signatures'] if s['ReachableFromAcroForm']]
    ps = p['Signatures']
    row = {'Id': fid, 'PdfiumCount': p['SignatureCount'], 'QuireReachableCount': len(qs), 'QuireTotalFound': len(q['Report']['Signatures']), 'Diffs': []}
    if p['SignatureCount'] != len(qs):
        row['Diffs'].append(f"count differs (PDFium {p['SignatureCount']} vs {len(qs)})")
    for i, (a, b) in enumerate(zip(ps, sorted(qs, key=lambda s: s['SignedRevisionEnd']))):
        if b['ByteRange'] and a['ByteRange'] != b['ByteRange']:
            row['Diffs'].append(f"sig{i}: ByteRange {a['ByteRange']} vs {b['ByteRange']}")
        if (a['SubFilter'] or '') != (b['SubFilter'] or ''):
            row['Diffs'].append(f"sig{i}: SubFilter {a['SubFilter']} vs {b['SubFilter']}")
        if (a['DocMdp'] or None) != (b['DocMdpPermission'] or None):
            row['Diffs'].append(f"sig{i}: DocMDP {a['DocMdp']} vs {b['DocMdpPermission']}")
        # PDFium returns the decoded hex string including zero padding; Quire reports the CMS length separately
        if b['ContentsCapacityBytes'] != a['ContentsLength']:
            row['Diffs'].append(f"sig{i}: Contents length PDFium {a['ContentsLength']} vs token {b['ContentsCapacityBytes']}")
    rows.append(row)
json.dump(rows, open(os.path.join(d, 'enum-compare.json'), 'w'), indent=2)
same = sum(1 for r in rows if not r['Diffs'])
print(f"{len(rows)} fixtures compared; identical enumeration: {same}")
for r in rows:
    if r['Diffs']: print(r['Id'], r['Diffs'])
