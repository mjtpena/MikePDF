"""pyHanko over a directory of Quire-signed corpus files (trust: root-rsa.cer in that directory).
  .venv/Scripts/python.exe -I crossvalidate/pyhanko_validate_dir.py out/corpus-signed
"""
import collections, glob, json, os, sys
from pyhanko.pdf_utils.reader import PdfFileReader
from pyhanko.sign.validation import validate_pdf_signature
from pyhanko_certvalidator import ValidationContext
from asn1crypto import x509

d = sys.argv[1]
root = x509.Certificate.load(open(os.path.join(d, 'root-rsa.cer'), 'rb').read())
res, counts = {}, collections.Counter()
for p in sorted(glob.glob(os.path.join(d, '*.pdf'))):
    n = os.path.basename(p)
    try:
        with open(p, 'rb') as f:
            r = PdfFileReader(f)
            sigs = list(r.embedded_signatures)
            if not sigs:
                res[n] = 'NO_SIGNATURE_FOUND'
            else:
                es = sigs[-1]  # the signature Quire added is the last revision
                st = validate_pdf_signature(es, signer_validation_context=ValidationContext(trust_roots=[root], allow_fetching=False, revocation_mode='soft-fail'))
                res[n] = st.summary()
    except Exception as e:
        res[n] = 'ERROR ' + type(e).__name__ + ': ' + str(e)[:120]
    counts[res[n].split(':')[0] if res[n].startswith('INTACT') else res[n][:60]] += 1
    counts['  full:' + res[n][:70]] += 0
json.dump(res, open(os.path.join(d, '..', 'corpus', 'pyhanko-corpus-signed.json'), 'w'), indent=1)
summ = collections.Counter(v if len(v) < 70 else v[:70] for v in res.values())
for k, v in summ.most_common(): print(v, k)
