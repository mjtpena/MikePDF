// S1.5 (NOT BUILT: BLOCKED, no clang-cl on this machine). Trivial libFuzzer target: open a PDF from memory,
// load page 0, render it. Planned build: clang-cl `-fsanitize=fuzzer,address` linked against the
// ASan build of our own PDFium (args/asan-fuzz.gn). See docs/spikes/S1.md.
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

#include "public/fpdfview.h"

namespace {
struct Mem { const uint8_t* data; size_t size; };
int GetBlock(void* param, unsigned long pos, unsigned char* buf, unsigned long size) {
  auto* m = static_cast<Mem*>(param);
  if (pos + size < pos || pos + size > m->size) return 0;
  std::memcpy(buf, m->data + pos, size);
  return 1;
}
}  // namespace

extern "C" int LLVMFuzzerInitialize(int*, char***) {
  FPDF_LIBRARY_CONFIG cfg{};
  cfg.version = 2;
  FPDF_InitLibraryWithConfig(&cfg);
  return 0;
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
  Mem mem{data, size};
  FPDF_FILEACCESS fa{};
  fa.m_FileLen = static_cast<unsigned long>(size);
  fa.m_GetBlock = GetBlock;
  fa.m_Param = &mem;
  FPDF_DOCUMENT doc = FPDF_LoadCustomDocument(&fa, nullptr);
  if (!doc) return 0;
  if (FPDF_GetPageCount(doc) > 0) {
    if (FPDF_PAGE page = FPDF_LoadPage(doc, 0)) {
      int w = 256, h = 256;
      std::vector<uint8_t> buf(static_cast<size_t>(w) * h * 4);
      if (FPDF_BITMAP bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, buf.data(), w * 4)) {
        FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, FPDF_ANNOT);
        FPDFBitmap_Destroy(bmp);
      }
      FPDF_ClosePage(page);
    }
  }
  FPDF_CloseDocument(doc);
  return 0;
}
