# Attribution for the corpus

No corpus file is stored in this repository or redistributed by it. `mikepdf-render corpus fetch` downloads each one, unmodified, from the source in `manifest.json` and checks its SHA-256. Where a licence asks for credit, this is the credit, and the manifest's `source` field links each file to its page.

| Source | Licence | Credit |
|---|---|---|
| veraPDF corpus (`verapdf/...`) | CC BY 4.0 | veraPDF Consortium and the PDF Association, https://github.com/veraPDF/veraPDF-corpus. Unmodified, a sample of the files |
| Wikimedia Commons (`commons/...`) | per file: public domain, CC0, or CC BY 2.0 to 4.0 | The author named on each file's Commons page (the manifest's `source` is that page). Files under CC BY are taken unmodified |
| arXiv (`arxiv/...`) | CC BY 4.0 or CC0, per paper | The authors of each paper, named on its abstract page (the manifest's `source`). Only papers whose authors chose CC BY or CC0 are included |
| US Government documents (`gov/...`) | public domain (17 U.S.C. 105) | GovInfo, the Federal Register, the Internal Revenue Service. No credit is required |
| PDFium (`pdfium/...`) and Chromium (`chromium/...`) | BSD-3-Clause | The PDFium Authors and The Chromium Authors |
| qpdf (`qpdf/...`) | Apache-2.0 | Jay Berkenbilt and the qpdf contributors, https://github.com/qpdf/qpdf |
| PDF Association (`pdf-differences/...`, `safedocs/...`) | Apache-2.0 | The PDF Association |
| Generated (`generated/...`) | Apache-2.0 | MikePDF itself: made by `Corpus/CorpusGenerators.cs` |

The sources that were looked at and left out, with the reason, are in the manifest's `excluded` list and summarised in [README.md](README.md).
