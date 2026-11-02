
### PDFium

| case | save | order | outline | links | named dests | labels | forms | annots | tags | opt. content | render | geometry | qpdf | PdfWriter validator | prefix |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rotate | inc | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| rotate | full | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | ok | - |
| delete | inc | ok | dead entries 2 | ok | ok | shifted 6/8 | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| delete | full | ok | dead entries 2 | ok | ok | shifted 6/8 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| delete-first | inc | ok | dead entries 1 | ok | ok | shifted 9/9 | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| delete-first | full | ok | dead entries 1 | ok | ok | shifted 9/9 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| undo-delete-by-reimport | inc | ok | dead entries 1 | missing 5, wrong 0, inert 5 | missing 0, wrong page 1 | ok | ok | ok | ok | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | ok |
| undo-delete-by-reimport | full | ok | dead entries 1 | missing 5, wrong 0, inert 5 | missing 0, wrong page 1 | ok | ok | ok | ok | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| delete-form-pages | inc | ok | dead entries 3 | ok | ok | shifted 6/7 | orphan fields | ok | ok | ok | ok | ok | ok | ok | ok |
| delete-form-pages | full | ok | dead entries 3 | ok | ok | shifted 6/7 | orphan fields | ok | ok | ok | ok | ok | ok | ok | - |
| insert-blank | inc | ok | ok | ok | ok | shifted 7/11 | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| insert-blank | full | ok | ok | ok | ok | shifted 7/11 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| move | inc | ok | ok | ok | ok | shifted 8/10 | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| move | full | ok | ok | ok | ok | shifted 8/10 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| reorder-3-moves | inc | ok | ok | ok | ok | shifted 7/10 | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| reorder-3-moves | full | ok | ok | ok | ok | shifted 7/10 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| duplicate | inc | ok | ok | missing 2, wrong 0, inert 2 | ok | shifted 9/11 | ok | ok | ok | ok | ok | ok | WARN (exit 3) | ok | ok |
| duplicate | full | ok | ok | missing 2, wrong 0, inert 2 | ok | shifted 9/11 | ok | ok | ok | ok | ok | ok | WARN (exit 3) | ok | - |
| insert-from-file | inc | ok | BROKEN | missing 2, wrong 2, inert 2 | ok | shifted 9/12 | BROKEN (AcroForm yes(4)) | ok | ok | ok | ok | ok | WARN (exit 3) | ok | ok |
| insert-from-file | full | ok | BROKEN | missing 2, wrong 2, inert 2 | ok | shifted 9/12 | BROKEN (AcroForm yes(4)) | ok | ok | ok | ok | ok | WARN (exit 3) | ok | - |
| xs-delete-move-rotate | inc | ok | dead entries 1 | ok | ok | shifted 9/9 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | WARN (exit 3) | FAIL | ok |
| xs-delete-move-rotate | full | ok | dead entries 1 | ok | ok | shifted 9/9 | ok | ok | ok | ok | ok | ok | ok | ok | - |
| extract-4-6 | full | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-4-6 | inc | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-4-6-range-string | full | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-4-6-range-string | inc | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-forms | full | ok | **LOST** | ok | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-forms | inc | ok | **LOST** | ok | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-1-3 | full | ok | **LOST** | missing 3, wrong 0, inert 11 | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| extract-1-3 | inc | ok | **LOST** | missing 3, wrong 0, inert 11 | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| extract-3-ocg | full | ok | **LOST** | ok | **LOST** (1) | shifted 1/1 | n/a | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| extract-3-ocg | inc | ok | **LOST** | ok | **LOST** (1) | shifted 1/1 | n/a | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| extract-1-note | full | ok | **LOST** | ok | **LOST** (1) | shifted 1/1 | n/a | ok | **LOST** | ok | ok | ok | ok | ok | - |
| extract-1-note | inc | ok | **LOST** | ok | **LOST** (1) | shifted 1/1 | n/a | ok | **LOST** | ok | ok | ok | ok | ok | - |
| split-chapter-1 | full | ok | **LOST** | missing 3, wrong 0, inert 11 | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| split-chapter-1 | inc | ok | **LOST** | missing 3, wrong 0, inert 11 | **LOST** (4) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| split-chapter-2 | full | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| split-chapter-2 | inc | ok | **LOST** | missing 3, wrong 0, inert 9 | **LOST** (3) | shifted 3/3 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| split-chapter-3 | full | ok | **LOST** | missing 5, wrong 0, inert 12 | **LOST** (5) | shifted 4/4 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| split-chapter-3 | inc | ok | **LOST** | missing 5, wrong 0, inert 12 | **LOST** (5) | shifted 4/4 | BROKEN (AcroForm NO) | ok | **LOST** | ok | ok | ok | ok | ok | - |
| merge-A-B | full | ok | **LOST** | missing 36, wrong 0, inert 36 | **LOST** (12) | shifted 15/15 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| merge-A-B | inc | ok | **LOST** | missing 36, wrong 0, inert 36 | **LOST** (12) | shifted 15/15 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| merge-B-A | full | ok | **LOST** | missing 36, wrong 0, inert 36 | **LOST** (12) | shifted 15/15 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| merge-B-A | inc | ok | **LOST** | missing 36, wrong 0, inert 36 | **LOST** (12) | shifted 15/15 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 1: A-03 | ok | ok | ok | - |
| merge-A-A | full | ok | **LOST** | missing 54, wrong 0, inert 54 | **LOST** (12) | shifted 20/20 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 2: A-03,A-03 | ok | ok | ok | - |
| merge-A-A | inc | ok | **LOST** | missing 54, wrong 0, inert 54 | **LOST** (12) | shifted 20/20 | BROKEN (AcroForm NO) | ok | **LOST** | BROKEN (layer visible) | differs on 2: A-03,A-03 | ok | ok | ok | - |
| extract-C-all | full | ok | n/a | n/a | n/a | n/a | n/a | ok | n/a | n/a | ok | ok | ok | ok | - |
| extract-C-all | inc | ok | n/a | n/a | n/a | n/a | n/a | ok | n/a | n/a | ok | ok | ok | ok | - |
| C-move-delete | inc | ok | n/a | n/a | n/a | n/a | n/a | ok | n/a | n/a | ok | ok | ok | ok | ok |
| C-move-delete | full | ok | n/a | n/a | n/a | n/a | n/a | ok | n/a | n/a | ok | ok | ok | ok | - |

### assembler prototype

| case | save | order | outline | links | named dests | labels | forms | annots | tags | opt. content | render | geometry | qpdf | PdfWriter validator | prefix |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| asm-delete | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-reorder | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-extract-4-6 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-extract-forms | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-extract-1-3 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-extract-3-ocg | asm | ok | ok | ok | ok | ok | n/a | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-extract-1-note | asm | ok | ok | ok | ok | ok | n/a | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-split-chapter-1 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-split-chapter-2 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-split-chapter-3 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-insert-from-file | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-merge-A-B | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
| asm-merge-B-A | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | differs on 1: A-04 | ok | ok | ok | - |
| asm-extract-C-all | asm | ok | n/a | n/a | n/a | n/a | n/a | ok | n/a | n/a | ok | ok | ok | ok | - |
| asm-xs-extract-4-6 | asm | ok | ok | ok | ok | ok | ok | ok | **LOST** | ok | ok | ok | ok | ok | - |
