"""Builds the S8.5 classification table and the manual Acrobat checklist.

  .venv/Scripts/python.exe -I crossvalidate/classify.py out/fixtures

Inputs (all in <dir>): manifest.json, quire.json (S8.2 validator), pyhanko.json (independent validator).
Outputs: classification.json, classification.md, and ../manual-acrobat-checklist.md (one level above <dir>).
Acrobat is never automated (risk R-15); its column is always "not measured (manual pass pending)".
"""
import json, os, sys

d = sys.argv[1]
manifest = {m['Id']: m for m in json.load(open(os.path.join(d, 'manifest.json')))}
quire = {r['Id']: r for r in json.load(open(os.path.join(d, 'quire.json')))}
pyh = {r['Id']: r for r in json.load(open(os.path.join(d, 'pyhanko.json')))}
ACROBAT = 'not measured (manual pass pending)'
ACCEPT = {'Valid', 'ValidWithWarnings'}


def map_pyhanko(s):
    if 'exception' in s:
        return 'Invalid', 'exception: ' + s['exception'][:90]
    if not s.get('intact') or not s.get('valid'):
        return 'Invalid', s.get('summary', '')
    summ = s.get('summary', '')
    if 'ILLEGAL_MODIFICATIONS' in summ:
        return 'Invalid', summ
    if not s.get('trusted'):
        return 'Unknown', summ
    if 'UNTOUCHED' in summ:
        return 'Valid', summ
    if 'ACCEPTABLE_MODIFICATIONS' in summ:
        return 'ValidWithWarnings', summ
    return 'Other', summ


def agree(q, p):
    if q == p:
        return 'exact'
    if q in ACCEPT and p in ACCEPT:
        return 'both accept (warning level differs)'
    if q not in ACCEPT and p not in ACCEPT:
        return 'both reject (reason/level differs)'
    return 'DISAGREE'


rows = []
for fid, spec in manifest.items():
    q = quire.get(fid, {})
    p = pyh.get(fid, {})
    if q.get('Missing') or not q:
        rows.append({'Id': fid, 'Family': spec['Family'], 'Missing': True})
        continue
    qsigs = q['Report']['Signatures']
    psigs = p.get('Signatures', [])
    n = max(len(qsigs), len(spec['Expected']))
    for i in range(n):
        exp = spec['Expected'][i] if i < len(spec['Expected']) else '(none)'
        qs = qsigs[i]['Status'] if i < len(qsigs) else 'NoSignatureFound'
        qr = qsigs[i]['StatusReason'] if i < len(qsigs) else ''
        if i < len(psigs):
            ps, praw = map_pyhanko(psigs[i])
        elif p.get('Error'):
            ps, praw = 'Invalid', 'reader error: ' + p['Error'][:90]
        else:
            ps, praw = 'NoSignatureFound', 'pyHanko enumerated no signature'
        rows.append({
            'Id': fid, 'Family': spec['Family'], 'Sig': i, 'Expected': exp, 'Quire': qs, 'QuireReason': qr,
            'PyHanko': ps, 'PyHankoRaw': praw, 'Acrobat': ACROBAT,
            'QuireMatchesExpected': qs == exp,
            'Agreement': agree(qs, ps),
            'QuireFalseValid': qs == 'Valid' and exp != 'Valid',
            'QuireAcceptsWhenExpectedReject': qs in ACCEPT and exp not in ACCEPT,
            'PyHankoAcceptsWhenExpectedReject': ps in ACCEPT and exp not in ACCEPT,
            'PyHankoValidWhenExpectedNotValid': ps == 'Valid' and exp != 'Valid',
        })

json.dump(rows, open(os.path.join(d, 'classification.json'), 'w'), indent=2)
real = [r for r in rows if not r.get('Missing')]
tot = len(real)
stats = {
    'signatures_classified': tot,
    'fixtures': len({r['Id'] for r in real}),
    'quire_matches_expected': sum(r['QuireMatchesExpected'] for r in real),
    'quire_false_valid': sum(r['QuireFalseValid'] for r in real),
    'quire_accepts_when_expected_reject': sum(r['QuireAcceptsWhenExpectedReject'] for r in real),
    'pyhanko_accepts_when_expected_reject': sum(r['PyHankoAcceptsWhenExpectedReject'] for r in real),
    'agreement_exact': sum(r['Agreement'] == 'exact' for r in real),
    'agreement_both_accept': sum(r['Agreement'].startswith('both accept') for r in real),
    'agreement_both_reject': sum(r['Agreement'].startswith('both reject') for r in real),
    'disagree': sum(r['Agreement'] == 'DISAGREE' for r in real),
}
json.dump(stats, open(os.path.join(d, 'classification-stats.json'), 'w'), indent=2)

md = ['| Fixture | Family | Sig | Expected | Quire | pyHanko 0.38.0 (mapped) | pyHanko raw | Acrobat | Quire = expected | Quire vs pyHanko |', '|---|---|---|---|---|---|---|---|---|---|']
for r in real:
    md.append(f"| {r['Id']} | {r['Family']} | {r['Sig']} | {r['Expected']} | **{r['Quire']}** | {r['PyHanko']} | {r['PyHankoRaw'][:70]} | {r['Acrobat']} | {'yes' if r['QuireMatchesExpected'] else '**NO**'} | {r['Agreement']} |")
md.append('')
md.append('Stats: ' + json.dumps(stats))
open(os.path.join(d, 'classification.md'), 'w', encoding='utf-8').write('\n'.join(md) + '\n')

# ---- manual Acrobat checklist
ACRO_EXPECT = {
    'Valid': 'Signature valid (integrity). Signer identity shows UNKNOWN/untrusted unless the matching root-*.cer is added as a trusted certificate in Acrobat first.',
    'ValidWithWarnings': 'Signature integrity valid; Acrobat lists later changes ("document has been modified since this signature was applied"). Record the exact wording and whether it is shown as valid or as a warning.',
    'Invalid': 'Signature invalid / "document has been altered or corrupted since the signature was applied" (or the signature panel shows a red cross).',
    'Unknown': 'Signature validity UNKNOWN (identity not trusted); integrity should read as intact.',
}
out = ['# S8 manual Acrobat checklist', '',
       'Acrobat was **not automated** (risk R-15, EULA open). Nothing below was measured. The "expected" column is the S8 author\'s prediction from',
       'Acrobat behaviour documented publicly; it exists only so the manual pass has something to compare against.', '',
       'How to run the pass (user action, not done by the spike):',
       '1. Open each file in Acrobat Reader / Acrobat DC on the VM-V machine; open the Signatures panel; expand each signature.',
       '2. First pass: do nothing to Acrobat\'s trust settings (expect identity UNKNOWN for every fixture).',
       '3. Second pass (optional): add `certs/root-rsa.cer`, `root-ecdsa.cer`, `root-rsa-expired.cer` as trusted certificates in a throw-away Acrobat profile / VM only, then repeat.',
       '4. Record: verdict icon, the one-line summary, "Signature Properties > Validity Summary", and whether "View signed version" differs.',
       '5. Fill the last column and flag any fixture where Acrobat shows a valid/green state for something Quire calls Invalid (critical) or the reverse.', '',
       '| File | Sig | Quire verdict | Expected Acrobat verdict (prediction) | Acrobat actual |', '|---|---|---|---|---|']
for r in real:
    f = manifest[r['Id']]['File']
    exp = ACRO_EXPECT.get(r['Expected'], '')
    if r['Family'] == 'attack-shadow' and r['Id'] in ('shadow-annot-overlay', 'sigfield-overlay'):
        exp = 'Valid with a "modified since signing" notice (annotation / signature-field additions are permitted changes). This is the residual overlay risk: record whether Acrobat visibly warns.'
    out.append(f"| `fixtures/{f}` | {r['Sig']} | {r['Quire']} | {exp} | not measured (manual pass pending) |")
out.append('')
out.append('Not in this table: `_base-unsigned*.pdf` (inputs), `cng-*.pdf` (CNG probe outputs, trust only their own throw-away certs).')
open(os.path.join(os.path.dirname(os.path.abspath(d)), 'manual-acrobat-checklist.md'), 'w', encoding='utf-8').write('\n'.join(out) + '\n')
print(json.dumps(stats, indent=2))
for r in real:
    flag = '' if r['QuireMatchesExpected'] else '   <== QUIRE != EXPECTED'
    flag += '   <== PYHANKO ACCEPTS' if r['PyHankoAcceptsWhenExpectedReject'] else ''
    flag += '   <== DISAGREE' if r['Agreement'] == 'DISAGREE' else ''
    print(f"{r['Id']:34} s{r['Sig']} exp={r['Expected']:18} quire={r['Quire']:18} pyhanko={r['PyHanko']:18} {r['Agreement']}{flag}")
