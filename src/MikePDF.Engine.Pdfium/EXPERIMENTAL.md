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
| `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFPage_GetAnnotIndex`, `FPDFPage_CloseAnnot` | `fpdf_annot.h` | Reading a page's annotations by position (`ListAnnotations`, `GetAnnotation`, `CountAnnotations`, `SetAnnotationHidden`, schema 7) | none |
| `FPDFAnnot_GetSubtype`, `FPDFAnnot_GetFlags`, `FPDFAnnot_SetFlags`, `FPDFAnnot_GetRect`, `FPDFAnnot_HasKey` | `fpdf_annot.h` | Subtype, flags, rectangle and key presence; `SetFlags` is the display mirror's Hidden flag (put back by `SaveDocument`) | none |
| `FPDFAnnot_GetColor`, `FPDFAnnot_GetNumberValue`, `FPDFAnnot_GetStringValue`, `FPDFAnnot_GetLinkedAnnot`, `FPDFAnnot_GetBorder` | `fpdf_annot.h` | Colour (only for an annotation with no appearance stream), `/CA`, `/Q`, `/MaxLen`, text entries, `/IRT`, `/Border` | none |
| `FPDFAnnot_HasAttachmentPoints`, `FPDFAnnot_CountAttachmentPoints`, `FPDFAnnot_GetAttachmentPoints`, `FPDFAnnot_GetVertices`, `FPDFAnnot_GetLine` | `fpdf_annot.h` | Quadrilaterals, polygon and polyline vertices, line end points in `GetAnnotation` | none |
| `FPDFAnnot_GetInkListCount`, `FPDFAnnot_GetInkListPath` | `fpdf_annot.h` | `GetAnnotation` (stroke and point counts) and `GetInkStroke` | none |
| `FPDFAnnot_GetObjectCount`, `FPDFAnnot_GetObject` | `fpdf_annot.h` | The first colour an appearance stream paints with, in `GetAnnotation` (`GetColor` fails for an annotation with a stream) | none |
| `FPDFAnnot_GetFormFieldType`, `FPDFAnnot_GetFormFieldFlags`, `FPDFAnnot_GetFormFieldName`, `FPDFAnnot_GetFormFieldAlternateName`, `FPDFAnnot_GetFormFieldValue`, `FPDFAnnot_GetFormFieldExportValue` | `fpdf_annot.h` | Reading a form widget's field (`GetFormFields`, `GetFormFieldDetail`) | none |
| `FPDFAnnot_GetOptionCount`, `FPDFAnnot_GetOptionLabel`, `FPDFAnnot_IsOptionSelected`, `FPDFAnnot_GetFontSize`, `FPDFAnnot_IsChecked`, `FPDFAnnot_GetFormControlCount`, `FPDFAnnot_GetFormFieldAtPoint`, `FPDFAnnot_GetFormAdditionalActionJavaScript` | `fpdf_annot.h` | Choice options and selection, check state, group size, the widget under a point, whether an action script exists (never run) | none |
| `FORM_SetFocusedAnnot`, `FORM_SelectAllText`, `FORM_SetIndexSelected`, `FORM_IsIndexSelected` | `fpdf_formfill.h` | `SetFormFieldValue` and `FormAction`: the only way PDFium lets a field value change is through these events | none |
| `FPDFText_GetMatrix`, `FPDFText_GetCharAngle`, `FPDFText_GetLooseCharBox` | `fpdf_text.h` | `GetTextQuads`: a character's direction and its loose box | none |
