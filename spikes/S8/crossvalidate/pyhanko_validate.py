"""Independent cross-validation of every fixture with pyHanko (MIT).

  .venv/Scripts/python.exe -I crossvalidate/pyhanko_validate.py out/fixtures

Trust: only the test roots named in manifest.json for the fixture (custom trust, no network: allow_fetching=False).
"""
import json, os, sys, traceback
from pyhanko.pdf_utils.reader import PdfFileReader
from pyhanko.sign.validation import validate_pdf_signature
from pyhanko_certvalidator import ValidationContext
from asn1crypto import pem, x509

d = sys.argv[1]
manifest = json.load(open(os.path.join(d, 'manifest.json')))

def load_root(name):
    data = open(os.path.join(d, 'certs', f'root-{name}.cer'), 'rb').read()
    if pem.detect(data):
        _, _, data = pem.unarmor(data)
    return x509.Certificate.load(data)

out = []
for spec in manifest:
    path = os.path.join(d, spec['File'])
    row = {'Id': spec['Id'], 'File': spec['File'], 'Signatures': [], 'Error': None}
    if not os.path.exists(path):
        row['Missing'] = True
        out.append(row)
        continue
    try:
        roots = [load_root(t) for t in spec['Trust']]
        with open(path, 'rb') as f:
            r = PdfFileReader(f)
            sigs = list(r.embedded_signatures)
            for i, es in enumerate(sigs):
                s = {'Index': i, 'FieldName': es.field_name}
                try:
                    vc = ValidationContext(trust_roots=roots, allow_fetching=False, revocation_mode='soft-fail')
                    st = validate_pdf_signature(es, signer_validation_context=vc)
                    s.update({
                        'intact': st.intact, 'valid': st.valid, 'trusted': st.trusted, 'revoked': st.revoked,
                        'bottom_line': st.bottom_line, 'coverage': st.coverage.name if st.coverage else None,
                        'modification_level': st.modification_level.name if st.modification_level else None,
                        'docmdp_ok': st.docmdp_ok, 'summary': st.summary(),
                    })
                except Exception as e:  # keep going: an exception is itself a (non-Valid) verdict
                    s.update({'exception': f'{type(e).__name__}: {e}'[:400]})
                row['Signatures'].append(s)
    except Exception as e:
        row['Error'] = f'{type(e).__name__}: {e}'[:400]
    out.append(row)
    sums = [s.get('summary') or s.get('exception') for s in row['Signatures']]
    print(f"{spec['Id']:34} {row['Error'] or sums}")

json.dump(out, open(os.path.join(d, 'pyhanko.json'), 'w'), indent=2)
