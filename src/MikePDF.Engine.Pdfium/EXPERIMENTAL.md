# PDFium Experimental APIs wrapped by MikePDF.Engine.Pdfium

ADR-0002 item 5: every function marked "Experimental" in the PDFium headers that this wrapper calls is listed here.
The weekly PDFium job diffs exports and header signatures against `functions.csv` and fails on any change to a function below.

| Function | Header | Used for | Stable alternative |
|---|---|---|---|
| `FPDF_GetPageWidthF`, `FPDF_GetPageHeightF` | `fpdfview.h` | Page size in points (rotation applied) | `FPDF_GetPageWidth`/`Height` (double, slated for deprecation) |
| `FPDF_GetXFAPacketCount` | `fpdfview.h` | `hasXfa` in `GetDocumentInfo` | none |
| `FPDF_GetSignatureCount` | `fpdf_signature.h` | `signatureCount` in `GetDocumentInfo` | none |
| `FPDFCatalog_IsTagged` | `fpdf_catalog.h` | `isTagged` in `GetDocumentInfo` | none |
| `FPDFDoc_GetJavaScriptActionCount` | `fpdf_javascript.h` | `hasJavaScript` in `GetDocumentInfo` | none |
