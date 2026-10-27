"""Interop fixtures made by pyHanko (independent implementation).

  pyhanko-signed.pdf       pyHanko signs the unsigned base document with the RSA test leaf (PAdES / ETSI.CAdES.detached)
  pyhanko-after-quire.pdf  pyHanko adds a second signature (ECDSA leaf) on top of valid-rsa-cades.pdf (made by Quire's writer)

Run isolated:  .venv/Scripts/python.exe -I crossvalidate/pyhanko_sign.py out/fixtures
Test keys only (leaf-*-TESTONLY.pfx, passphrase 'test'); nothing is installed into any certificate store.
"""
import sys, os
from pyhanko.sign import signers
from pyhanko.sign.fields import SigSeedSubFilter
from pyhanko.sign.signers import PdfSignatureMetadata
from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter

d = sys.argv[1]
rsa = signers.SimpleSigner.load_pkcs12(pfx_file=os.path.join(d, 'certs', 'leaf-rsa-TESTONLY.pfx'), passphrase=b'test')
ec = signers.SimpleSigner.load_pkcs12(pfx_file=os.path.join(d, 'certs', 'leaf-ecdsa-TESTONLY.pfx'), passphrase=b'test')

def sign(src, dst, signer, field):
    with open(os.path.join(d, src), 'rb') as f:
        w = IncrementalPdfFileWriter(f)
        meta = PdfSignatureMetadata(field_name=field, subfilter=SigSeedSubFilter.PADES, md_algorithm='sha256')
        with open(os.path.join(d, dst), 'wb') as o:
            signers.sign_pdf(w, meta, signer=signer, output=o)
    print('wrote', dst, os.path.getsize(os.path.join(d, dst)), 'bytes')

sign('_base-unsigned.pdf', 'pyhanko-signed.pdf', rsa, 'PySig1')
sign('valid-rsa-cades.pdf', 'pyhanko-after-quire.pdf', ec, 'PySig2')
