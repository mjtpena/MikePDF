// S2.2 engine: trimmed from spikes/S3/engine-cpp/engine.cpp (same pipe framing and section/handle plumbing), extended for the render pipeline:
//   - RenderTile carries a generation number; CancelBefore(gen) is handled on the I/O thread and (a) drops queued requests, (b) aborts the in-flight progressive render
//   - optional page cache (LRU of FPDF_PAGE) so the content stream is parsed once per page, not once per tile
//   - GetPageSizes returns every page size in one message (layout for a 2,000-page document)
// Build: build.cmd. Runs inside the zero-capability AppContainer from the broker (Sandbox.cs).
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <string>
#include <vector>
#include <deque>
#include <list>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <algorithm>
#include "fpdfview.h"
#include "fpdf_progressive.h"
#include "fpdf_thumbnail.h"

namespace {
enum Msg : uint16_t { Hello = 1, Ping = 2, OpenDocument = 4, CloseDocument = 5, RenderTile = 6, Shutdown = 7, GetPageSizes = 20, CancelBefore = 21, SetOptions = 22 };
constexpr uint16_t kResponseBit = 0x8000;
constexpr size_t kHeader = 12;
constexpr uint32_t kMaxFrame = 8u << 20;
constexpr size_t kShmHeader = 64 * 1024, kSlotSize = 1u << 20;       // 1 MiB = one 512x512 BGRA tile
constexpr int kFlagNoCache = 0x100;                                    // RenderTile flag: do not keep the FPDF_PAGE (thumbnails)

int64_t Now() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }

struct Writer {
  std::vector<uint8_t> b;
  template <class T> Writer& raw(T v) { auto p = reinterpret_cast<const uint8_t*>(&v); b.insert(b.end(), p, p + sizeof(T)); return *this; }
  Writer& i32(int32_t v) { return raw(v); } Writer& u32(uint32_t v) { return raw(v); } Writer& i64(int64_t v) { return raw(v); } Writer& f32(float v) { return raw(v); }
  Writer& str(const std::string& s) { i32(static_cast<int32_t>(s.size())); b.insert(b.end(), s.begin(), s.end()); return *this; }
};
struct Reader {
  const uint8_t* p; size_t n; size_t pos = 0; bool ok = true;
  Reader(const uint8_t* d, size_t len) : p(d), n(len) {}
  template <class T> T raw() { T v{}; if (pos + sizeof(T) > n) { ok = false; return v; } std::memcpy(&v, p + pos, sizeof(T)); pos += sizeof(T); return v; }
  int32_t i32() { return raw<int32_t>(); } uint32_t u32() { return raw<uint32_t>(); } int64_t i64() { return raw<int64_t>(); } float f32() { return raw<float>(); }
};

HANDLE g_pipe = INVALID_HANDLE_VALUE, g_evRead = nullptr, g_evWrite = nullptr;
std::mutex g_writeLock;
uint8_t* g_shm = nullptr; size_t g_slotCount = 0;
HANDLE g_fileFromSpawn = nullptr;
std::atomic<uint32_t> g_minGen{0};
std::atomic<int64_t> g_pdfiumInitTicks{0};
int g_pageCacheCap = 8;
std::atomic<int> g_progressive{1};

void Log(const char* fmt, ...) {
  HANDLE h = GetStdHandle(STD_ERROR_HANDLE); if (h == nullptr || h == INVALID_HANDLE_VALUE) return;
  char buf[512]; va_list ap; va_start(ap, fmt); int n = vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
  if (n > 0) { DWORD w; WriteFile(h, buf, static_cast<DWORD>(std::min<int>(n, sizeof buf - 1)), &w, nullptr); }
}

bool ReadExact(void* dst, DWORD n) {
  auto* p = static_cast<uint8_t*>(dst);
  while (n) { OVERLAPPED ov{}; ov.hEvent = g_evRead; ResetEvent(g_evRead); DWORD got = 0;
    if (!ReadFile(g_pipe, p, n, nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
    if (!GetOverlappedResult(g_pipe, &ov, &got, TRUE) || got == 0) return false; p += got; n -= got; }
  return true;
}
bool WriteAll(const void* src, DWORD n) {
  auto* p = static_cast<const uint8_t*>(src);
  while (n) { OVERLAPPED ov{}; ov.hEvent = g_evWrite; ResetEvent(g_evWrite); DWORD got = 0;
    if (!WriteFile(g_pipe, p, n, nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
    if (!GetOverlappedResult(g_pipe, &ov, &got, TRUE)) return false; p += got; n -= got; }
  return true;
}
void Respond(uint16_t type, uint32_t reqId, const Writer& w) {
  std::vector<uint8_t> f(kHeader + w.b.size());
  uint32_t len = static_cast<uint32_t>(f.size() - 4); uint16_t t = static_cast<uint16_t>(type | kResponseBit), fl = 0;
  std::memcpy(f.data(), &len, 4); std::memcpy(f.data() + 4, &t, 2); std::memcpy(f.data() + 6, &fl, 2); std::memcpy(f.data() + 8, &reqId, 4);
  if (!w.b.empty()) std::memcpy(f.data() + kHeader, w.b.data(), w.b.size());
  std::lock_guard<std::mutex> g(g_writeLock); WriteAll(f.data(), static_cast<DWORD>(f.size()));
}

// ---- document through a HANDLE
struct FileCtx { HANDLE h; };
int GetBlock(void* param, unsigned long pos, unsigned char* buf, unsigned long size) {
  HANDLE h = static_cast<FileCtx*>(param)->h; unsigned long total = 0;
  while (total < size) { OVERLAPPED ov{}; ov.Offset = pos + total; DWORD got = 0; if (!ReadFile(h, buf + total, size - total, &got, &ov) || got == 0) return 0; total += got; }
  return 1;
}
FPDF_DOCUMENT g_doc = nullptr; FPDF_FILEACCESS g_fa{}; FileCtx g_fctx{};

// ---- page cache (engine thread only)
struct CachedPage { int index; FPDF_PAGE page; };
std::list<CachedPage> g_pages;
void ClearPages() { for (auto& c : g_pages) FPDF_ClosePage(c.page); g_pages.clear(); }
FPDF_PAGE GetPage(int index, bool cache) {
  if (cache && g_pageCacheCap > 0) {
    for (auto it = g_pages.begin(); it != g_pages.end(); ++it) if (it->index == index) { g_pages.splice(g_pages.begin(), g_pages, it); return it->page; }
  }
  FPDF_PAGE p = FPDF_LoadPage(g_doc, index);
  if (p && cache && g_pageCacheCap > 0) { g_pages.push_front({ index, p }); while (static_cast<int>(g_pages.size()) > g_pageCacheCap) { FPDF_ClosePage(g_pages.back().page); g_pages.pop_back(); } }
  return p;
}

// ---- progressive render with cancellation
struct PauseState { IFSDK_PAUSE base; uint32_t gen; };
FPDF_BOOL NeedPause(IFSDK_PAUSE* p) { return reinterpret_cast<PauseState*>(p)->gen < g_minGen.load(std::memory_order_relaxed); }

struct Work { uint16_t type; uint32_t reqId; std::vector<uint8_t> payload; int64_t tRecv; };

void DoOpen(Reader& r, Writer& o) {
  int64_t hv = r.i64(); ClearPages();
  if (g_doc) { FPDF_CloseDocument(g_doc); g_doc = nullptr; }
  HANDLE h = hv == 0 ? g_fileFromSpawn : reinterpret_cast<HANDLE>(hv);
  int64_t t0 = Now(); LARGE_INTEGER sz{};
  if (!GetFileSizeEx(h, &sz) || sz.QuadPart > 0xFFFFFFFFll) { o.i32(-2).i32(0).i64(t0).i64(Now()); return; }
  g_fctx.h = h; g_fa.m_FileLen = static_cast<unsigned long>(sz.QuadPart); g_fa.m_GetBlock = GetBlock; g_fa.m_Param = &g_fctx;
  g_doc = FPDF_LoadCustomDocument(&g_fa, nullptr);
  int pages = g_doc ? FPDF_GetPageCount(g_doc) : 0;
  o.i32(g_doc ? 0 : -3).i32(pages).i64(t0).i64(Now());
}

void DoPageSizes(Writer& o) {
  int n = g_doc ? FPDF_GetPageCount(g_doc) : 0; o.i32(n);
  for (int i = 0; i < n; i++) { FS_SIZEF s{}; if (!FPDF_GetPageSizeByIndexF(g_doc, i, &s)) { s.width = 612; s.height = 792; } o.f32(s.width).f32(s.height); }
}

// payload: i32 page, f32 scale, i32 ox, i32 oy, i32 w, i32 h, i32 slot, i32 flags, u32 gen
// response: i32 status (0 ok, 1 dropped/aborted by CancelBefore, <0 error), i32 slot, i64 tRecv, tDeq, tLoaded, tRendered, tSend (QPC)
void DoRender(Reader& r, Writer& o, int64_t tRecv, int64_t tDeq) {
  int page = r.i32(); float scale = r.f32(); int ox = r.i32(), oy = r.i32(), w = r.i32(), h = r.i32(), slot = r.i32(), flags = r.i32(); uint32_t gen = r.u32();
  if (!r.ok || !g_doc) { o.i32(-10).i32(slot); return; }
  if (gen < g_minGen.load()) { o.i32(1).i32(slot).i64(tRecv).i64(tDeq).i64(tDeq).i64(tDeq).i64(Now()); return; }                  // obsolete before it started
  if (slot < 0 || static_cast<size_t>(slot) >= g_slotCount || w <= 0 || h <= 0 || w > 512 || h > 512 || !g_shm) { o.i32(-11).i32(slot); return; }
  bool cache = (flags & kFlagNoCache) == 0;
  FPDF_PAGE pg = GetPage(page, cache);
  if (!pg) { o.i32(-12).i32(slot); return; }
  int64_t tLoaded = Now();
  float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg);
  uint8_t* dst = g_shm + kShmHeader + static_cast<size_t>(slot) * kSlotSize;
  FPDF_BITMAP bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, dst, w * 4);       // renders straight into the shared slot
  FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFFu);
  int sx = static_cast<int>(std::ceil(pw * scale)), sy = static_cast<int>(std::ceil(ph * scale));
  int status = 0;
  if (g_progressive.load()) {
    PauseState ps; ps.base.version = 1; ps.base.NeedToPauseNow = NeedPause; ps.base.user = nullptr; ps.gen = gen;
    int st = FPDF_RenderPageBitmap_Start(bmp, pg, -ox, -oy, sx, sy, 0, 0, &ps.base);
    while (st == FPDF_RENDER_TOBECONTINUED) {
      if (gen < g_minGen.load()) { status = 1; break; }
      st = FPDF_RenderPage_Continue(pg, &ps.base);
    }
    FPDF_RenderPage_Close(pg);
    if (status == 0 && st == FPDF_RENDER_FAILED) status = -13;
  } else {
    FPDF_RenderPageBitmap(bmp, pg, -ox, -oy, sx, sy, 0, 0);
  }
  FPDFBitmap_Destroy(bmp);
  if (!cache) FPDF_ClosePage(pg);
  int64_t tRendered = Now();
  o.i32(status).i32(slot).i64(tRecv).i64(tDeq).i64(tLoaded).i64(tRendered).i64(Now());
}

std::mutex g_qLock; std::condition_variable g_qCv; std::deque<Work> g_queue; bool g_qDone = false;
std::atomic<bool> g_ready{false};

void EngineThread() {
  int64_t t0 = Now();
  FPDF_LIBRARY_CONFIG cfg{}; cfg.version = 2; FPDF_InitLibraryWithConfig(&cfg);
  g_pdfiumInitTicks = Now() - t0; g_ready = true;
  for (;;) {
    Work w;
    { std::unique_lock<std::mutex> lk(g_qLock); g_qCv.wait(lk, [] { return !g_queue.empty() || g_qDone; }); if (g_queue.empty()) return; w = std::move(g_queue.front()); g_queue.pop_front(); }
    int64_t tDeq = Now(); Reader r(w.payload.data(), w.payload.size()); Writer o;
    switch (w.type) {
      case OpenDocument: DoOpen(r, o); break;
      case CloseDocument: ClearPages(); if (g_doc) { FPDF_CloseDocument(g_doc); g_doc = nullptr; } o.i32(0); break;
      case GetPageSizes: DoPageSizes(o); break;
      case RenderTile: DoRender(r, o, w.tRecv, tDeq); break;
      case SetOptions: { int cap = r.i32(); int prog = r.i32(); ClearPages(); g_pageCacheCap = cap; g_progressive = prog; o.i32(0); break; }
      default: o.i32(-1); break;
    }
    Respond(w.type, w.reqId, o);
  }
}
}  // namespace

int main(int argc, char** argv) {
  std::string pipeName; HANDLE section = nullptr; size_t sectionBytes = 0;
  for (int i = 1; i < argc; ++i) {
    std::string a = argv[i];
    if (a == "--pipe" && i + 1 < argc) pipeName = argv[++i];
    else if (a == "--file" && i + 1 < argc) g_fileFromSpawn = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_strtoui64(argv[++i], nullptr, 10)));
    else if (a == "--section" && i + 1 < argc) section = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_strtoui64(argv[++i], nullptr, 10)));
    else if (a == "--slots" && i + 1 < argc) g_slotCount = static_cast<size_t>(atoi(argv[++i]));
    else if (a == "--pagecache" && i + 1 < argc) g_pageCacheCap = atoi(argv[++i]);
  }
  if (pipeName.empty()) return 2;
  if (section) { g_shm = static_cast<uint8_t*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0)); if (!g_shm) return 3; }
  (void)sectionBytes;
  g_evRead = CreateEventW(nullptr, TRUE, FALSE, nullptr); g_evWrite = CreateEventW(nullptr, TRUE, FALSE, nullptr);
  std::thread engine(EngineThread);
  std::wstring wname = L"\\\\.\\pipe\\" + std::wstring(pipeName.begin(), pipeName.end());
  for (int attempt = 0; attempt < 100; ++attempt) {
    g_pipe = CreateFileW(wname.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (g_pipe != INVALID_HANDLE_VALUE) break;
    if (GetLastError() == ERROR_PIPE_BUSY) { WaitNamedPipeW(wname.c_str(), 1000); continue; }
    return 4;
  }
  if (g_pipe == INVALID_HANDLE_VALUE) return 4;
  uint8_t hdr[kHeader];
  for (;;) {
    if (!ReadExact(hdr, kHeader)) break;
    uint32_t len; uint16_t type; uint32_t reqId;
    std::memcpy(&len, hdr, 4); std::memcpy(&type, hdr + 4, 2); std::memcpy(&reqId, hdr + 8, 4);
    if (len < kHeader - 4 || len > kMaxFrame) return 5;
    std::vector<uint8_t> payload(len - (kHeader - 4));
    if (!payload.empty() && !ReadExact(payload.data(), static_cast<DWORD>(payload.size()))) break;
    int64_t tRecv = Now();
    switch (type) {
      case Hello: { while (!g_ready) Sleep(0); Writer w; w.u32(1).i64(GetCurrentProcessId()).i64(g_pdfiumInitTicks.load()).str("s2-engine native C++20"); Respond(Hello, reqId, w); break; }
      case Ping: { Writer w; w.i64(tRecv).i64(Now()); Respond(Ping, reqId, w); break; }
      case CancelBefore: { Reader r(payload.data(), payload.size()); uint32_t g = r.u32(); uint32_t cur = g_minGen.load(); while (g > cur && !g_minGen.compare_exchange_weak(cur, g)) {} Writer w; w.i64(tRecv).i64(Now()); Respond(CancelBefore, reqId, w); break; }
      case Shutdown: { Writer w; Respond(Shutdown, reqId, w); { std::lock_guard<std::mutex> lk(g_qLock); g_qDone = true; } g_qCv.notify_all(); engine.detach(); return 0; }
      default: { std::lock_guard<std::mutex> lk(g_qLock); g_queue.push_back({ type, reqId, std::move(payload), tRecv }); } g_qCv.notify_one(); break;
    }
  }
  { std::lock_guard<std::mutex> lk(g_qLock); g_qDone = true; } g_qCv.notify_all(); engine.detach();
  return 0;
}
