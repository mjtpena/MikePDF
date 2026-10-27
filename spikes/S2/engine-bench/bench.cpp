// S2.1 native engine bench: PDFium page -> BGRA tile. Single-threaded (PDFium is single-threaded per process).
// Build: build.cmd. Usage:
//   bench.exe all   <fixtureDir> <outDir> [affinityMask]     full matrix + warm TTFT + progressive
//   bench.exe cold  <pdf> <tile> [affinityMask]              one process-cold open -> first tile, prints a CSV line (driven 30x by run-engine-bench.ps1)
//   bench.exe dump  <pdf> <page> <zoomPct> <tile> <tx> <ty> <out.bgra>    write one tile for visual checks
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>
#include <atomic>
#include <thread>
#include <random>
#include <numeric>
#include <cmath>
#include "fpdfview.h"
#include "fpdf_progressive.h"

static LARGE_INTEGER g_freq;
static inline int64_t Now() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static inline double Ms(int64_t t) { return 1000.0 * (double)t / (double)g_freq.QuadPart; }

// ------------------------------------------------------------------------------------------------ stats
struct Stats { size_t n = 0; double p50 = 0, p95 = 0, p99 = 0, max = 0, mean = 0, min = 0; };
static Stats Summarise(std::vector<double> v) {
  Stats s; if (v.empty()) return s;
  std::sort(v.begin(), v.end()); s.n = v.size();
  auto pct = [&](double p) { double idx = p * (v.size() - 1); size_t lo = (size_t)idx; size_t hi = std::min(lo + 1, v.size() - 1); return v[lo] + (v[hi] - v[lo]) * (idx - lo); };
  s.p50 = pct(0.5); s.p95 = pct(0.95); s.p99 = pct(0.99); s.max = v.back(); s.min = v.front();
  s.mean = std::accumulate(v.begin(), v.end(), 0.0) / v.size();
  return s;
}

// ------------------------------------------------------------------------------------------------ document via HANDLE (same access pattern as the S3 engine)
struct FileCtx { HANDLE h; };
static int GetBlock(void* param, unsigned long pos, unsigned char* buf, unsigned long size) {
  HANDLE h = static_cast<FileCtx*>(param)->h; unsigned long total = 0;
  while (total < size) { OVERLAPPED ov{}; ov.Offset = pos + total; DWORD got = 0; if (!ReadFile(h, buf + total, size - total, &got, &ov) || got == 0) return 0; total += got; }
  return 1;
}
struct Doc {
  HANDLE h = INVALID_HANDLE_VALUE; FileCtx ctx{}; FPDF_FILEACCESS fa{}; FPDF_DOCUMENT doc = nullptr; int pages = 0;
  bool Open(const std::string& path) {
    h = CreateFileA(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz; GetFileSizeEx(h, &sz); ctx.h = h; fa.m_FileLen = (unsigned long)sz.QuadPart; fa.m_GetBlock = GetBlock; fa.m_Param = &ctx;
    doc = FPDF_LoadCustomDocument(&fa, nullptr); if (!doc) return false; pages = FPDF_GetPageCount(doc); return true;
  }
  void Close() { if (doc) FPDF_CloseDocument(doc); doc = nullptr; if (h != INVALID_HANDLE_VALUE) CloseHandle(h); h = INVALID_HANDLE_VALUE; }
};

// ------------------------------------------------------------------------------------------------ tiles
struct Tile { int T; std::vector<uint8_t> buf; FPDF_BITMAP bmp = nullptr;
  explicit Tile(int t) : T(t), buf((size_t)t * t * 4) { bmp = FPDFBitmap_CreateEx(t, t, FPDFBitmap_BGRA, buf.data(), t * 4); }
  ~Tile() { FPDFBitmap_Destroy(bmp); } };

constexpr double kPxPerPtAt100 = 96.0 / 72.0;     // zoom 100% = 96 dpi CSS pixels, device scale factor 1.0
constexpr int kFlags = 0;                         // no FPDF_ANNOT (annotations are a separate layer, spec 7.1.7), no LCD text

static double RenderTile(Tile& t, FPDF_PAGE pg, double scale, float pw, float ph, int tx, int ty) {
  FPDFBitmap_FillRect(t.bmp, 0, 0, t.T, t.T, 0xFFFFFFFFu);
  int64_t a = Now();
  FPDF_RenderPageBitmap(t.bmp, pg, -tx * t.T, -ty * t.T, (int)std::ceil(pw * scale), (int)std::ceil(ph * scale), 0, kFlags);
  return Ms(Now() - a);
}
static size_t NonWhite(const Tile& t) { size_t c = 0; const uint32_t* p = (const uint32_t*)t.buf.data(); for (size_t i = 0; i < (size_t)t.T * t.T; i += 7) c += (p[i] & 0xFFFFFF) != 0xFFFFFF; return c; }

struct Fixture { std::string name, path; };
static int TypicalPage(int n) { return n <= 1 ? 0 : std::min(n - 1, std::max(1, n / 4)); }   // an interior page: page 1 of a real book is often blank

static std::vector<int> SamplePages(int n, int count) {
  std::vector<int> v;
  if (n <= count) { for (int i = 0; i < n; i++) v.push_back(i); return v; }
  for (int i = 0; i < count; i++) v.push_back((int)((long long)(n - 1) * (i + 1) / (count + 1)));      // spread across the document, skipping the very first page
  return v;
}
// tiles in the central region of the page (12%..88%), at most `maxTiles`, evenly strided
static std::vector<std::pair<int,int>> PickTiles(int W, int H, int T, int maxTiles) {
  int cols = (W + T - 1) / T, rows = (H + T - 1) / T;
  int c0 = (int)(cols * 0.12), c1 = std::max(c0 + 1, (int)std::ceil(cols * 0.88)), r0 = (int)(rows * 0.12), r1 = std::max(r0 + 1, (int)std::ceil(rows * 0.88));
  std::vector<std::pair<int,int>> all; for (int y = r0; y < r1 && y < rows; y++) for (int x = c0; x < c1 && x < cols; x++) all.push_back({x, y});
  if ((int)all.size() <= maxTiles) return all;
  std::vector<std::pair<int,int>> out; for (int i = 0; i < maxTiles; i++) out.push_back(all[(size_t)((long long)(all.size() - 1) * i / (maxTiles - 1))]);
  return out;
}

// ------------------------------------------------------------------------------------------------ progressive
struct PauseState { IFSDK_PAUSE base; int64_t deadline = 0; std::atomic<bool> cancel{false}; long calls = 0; };
static FPDF_BOOL NeedPause(IFSDK_PAUSE* p) { auto* s = (PauseState*)p; ++s->calls; return s->cancel.load(std::memory_order_relaxed) || Now() >= s->deadline; }

struct ProgResult { double totalMs; long continues; double maxOvershootMs; long pauseCalls; int status; };
static ProgResult RenderProgressive(Tile& t, FPDF_PAGE pg, double scale, float pw, float ph, int tx, int ty, double sliceMs) {
  FPDFBitmap_FillRect(t.bmp, 0, 0, t.T, t.T, 0xFFFFFFFFu);
  PauseState ps; ps.base.version = 1; ps.base.NeedToPauseNow = NeedPause; ps.base.user = nullptr;
  int64_t sliceTicks = (int64_t)(sliceMs * g_freq.QuadPart / 1000.0);
  int64_t t0 = Now(); ps.deadline = t0 + sliceTicks;
  int st = FPDF_RenderPageBitmap_Start(t.bmp, pg, -tx * t.T, -ty * t.T, (int)std::ceil(pw * scale), (int)std::ceil(ph * scale), 0, kFlags, &ps.base);
  long conts = 1; double maxOver = 0;
  { double over = Ms(Now() - ps.deadline); if (st == FPDF_RENDER_TOBECONTINUED) maxOver = std::max(maxOver, over); }
  while (st == FPDF_RENDER_TOBECONTINUED) {
    ps.deadline = Now() + sliceTicks;
    st = FPDF_RenderPage_Continue(pg, &ps.base); ++conts;
    if (st == FPDF_RENDER_TOBECONTINUED) maxOver = std::max(maxOver, Ms(Now() - ps.deadline));
  }
  FPDF_RenderPage_Close(pg);
  return { Ms(Now() - t0), conts, maxOver, ps.calls, st };
}

// cancel test: a controller thread sets the cancel flag `delayMs` after the render starts; measure flag -> engine ready for the next job
struct CancelResult { bool cancelled; double latencyMs; double totalIfCompleteMs; };
static CancelResult RenderCancel(Tile& t, FPDF_PAGE pg, double scale, float pw, float ph, int tx, int ty, double sliceMs, double delayMs) {
  FPDFBitmap_FillRect(t.bmp, 0, 0, t.T, t.T, 0xFFFFFFFFu);
  PauseState ps; ps.base.version = 1; ps.base.NeedToPauseNow = NeedPause; ps.base.user = nullptr;
  int64_t sliceTicks = (int64_t)(sliceMs * g_freq.QuadPart / 1000.0);
  std::atomic<int64_t> tCancel{0}; std::atomic<bool> go{false}; std::atomic<bool> quit{false};
  int64_t t0 = Now(); int64_t fire = t0 + (int64_t)(delayMs * g_freq.QuadPart / 1000.0);
  std::thread ctl([&] {
    while (!quit.load() && Now() < fire) { if (Ms(fire - Now()) > 2.5) Sleep(1); else YieldProcessor(); }
    if (quit.load()) return;
    tCancel.store(Now()); ps.cancel.store(true);
  });
  ps.deadline = t0 + sliceTicks;
  int st = FPDF_RenderPageBitmap_Start(t.bmp, pg, -tx * t.T, -ty * t.T, (int)std::ceil(pw * scale), (int)std::ceil(ph * scale), 0, kFlags, &ps.base);
  while (st == FPDF_RENDER_TOBECONTINUED) {
    if (ps.cancel.load()) break;
    ps.deadline = Now() + sliceTicks;
    st = FPDF_RenderPage_Continue(pg, &ps.base);
  }
  FPDF_RenderPage_Close(pg);
  int64_t tBack = Now(); quit.store(true); ctl.join();
  int64_t tc = tCancel.load();
  CancelResult r{}; r.totalIfCompleteMs = Ms(tBack - t0);
  r.cancelled = tc != 0 && st == FPDF_RENDER_TOBECONTINUED;
  r.latencyMs = tc ? Ms(tBack - tc) : 0;
  return r;
}

// ------------------------------------------------------------------------------------------------ main benches
static FILE* g_tiles = nullptr; static FILE* g_summary = nullptr; static FILE* g_ttft = nullptr; static FILE* g_prog = nullptr;

static void SummaryRow(const char* fx, int tile, int zoom, const char* metric, const Stats& s) {
  double tps = s.mean > 0 ? 1000.0 / s.mean : 0;
  fprintf(g_summary, "%s,%d,%d,%s,%zu,%.3f,%.3f,%.3f,%.3f,%.3f,%.1f\n", fx, tile, zoom, metric, s.n, s.p50, s.p95, s.p99, s.max, s.mean, tps);
  fflush(g_summary);
}

static void BenchThroughput(const Fixture& fx) {
  Doc d; if (!d.Open(fx.path)) { printf("  open failed %s\n", fx.path.c_str()); return; }
  for (int T : {256, 512}) for (int zoom : {100, 200, 400}) {
    double scale = kPxPerPtAt100 * zoom / 100.0;
    Tile tile(T);
    std::vector<double> first, subsequent, loadMs, nonblankSub; std::vector<double> allRender;
    auto pages = SamplePages(d.pages, 8);
    int collected = 0;
    for (int pi : pages) {
      if (collected >= 48) break;
      int64_t a = Now(); FPDF_PAGE pg = FPDF_LoadPage(d.doc, pi); double lp = Ms(Now() - a);
      if (!pg) continue;
      float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg);
      int W = (int)std::ceil(pw * scale), H = (int)std::ceil(ph * scale);
      auto picks = PickTiles(W, H, T, 24);
      loadMs.push_back(lp);
      bool firstOfPage = true;
      for (auto [tx, ty] : picks) {                         // pass 1: first render includes content-stream parse for this page object
        double ms = RenderTile(tile, pg, scale, pw, ph, tx, ty);
        fprintf(g_tiles, "%s,%d,%d,%d,%d,%d,1,%d,%.3f,%.3f,%zu\n", fx.name.c_str(), T, zoom, pi, tx, ty, firstOfPage ? 1 : 0, lp, ms, NonWhite(tile));
        if (firstOfPage) first.push_back(ms); firstOfPage = false;
      }
      for (auto [tx, ty] : picks) {                         // pass 2: page object stays loaded (what a page cache in the engine gives)
        double ms = RenderTile(tile, pg, scale, pw, ph, tx, ty);
        size_t nw = NonWhite(tile);
        fprintf(g_tiles, "%s,%d,%d,%d,%d,%d,2,0,%.3f,%.3f,%zu\n", fx.name.c_str(), T, zoom, pi, tx, ty, lp, ms, nw);
        subsequent.push_back(ms); if (nw > 0) nonblankSub.push_back(ms); ++collected;
      }
      FPDF_ClosePage(pg);
    }
    SummaryRow(fx.name.c_str(), T, zoom, "render_loaded_page_ms", Summarise(subsequent));
    SummaryRow(fx.name.c_str(), T, zoom, "render_loaded_page_nonblank_ms", Summarise(nonblankSub));
    SummaryRow(fx.name.c_str(), T, zoom, "render_first_tile_of_page_ms", Summarise(first));
    SummaryRow(fx.name.c_str(), T, zoom, "load_page_ms", Summarise(loadMs));
    auto s = Summarise(subsequent);
    printf("  %-16s tile %3d zoom %3d%%  loaded-page render p50 %8.2f p95 %8.2f max %8.2f ms  (%5.1f tiles/s mean)  first-tile-of-page p50 %8.2f  n=%zu\n", fx.name.c_str(), T, zoom, s.p50, s.p95, s.max, s.mean > 0 ? 1000 / s.mean : 0, Summarise(first).p50, s.n);
    fflush(stdout);
  }
  d.Close();
}

// Warm TTFT: repeated open -> first tile in one process (PDFium font/glyph caches warm, OS file cache warm).
static void BenchTtftWarm(const Fixture& fx, int iterations) {
  for (int T : {256, 512}) {
    std::vector<double> vOpen, vPage, vFirst, vTtft, vViewport;
    double scale = kPxPerPtAt100;
    Tile tile(T);
    for (int it = 0; it < iterations + 3; it++) {
      Doc d; int64_t t0 = Now();
      if (!d.Open(fx.path)) return;
      int64_t t1 = Now();
      int pidx = TypicalPage(d.pages);                           // a typical interior page; page 0 is often a title page
      FPDF_PAGE pg = FPDF_LoadPage(d.doc, pidx); int64_t t2 = Now();
      float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg);
      int W = (int)std::ceil(pw * scale), H = (int)std::ceil(ph * scale);
      int tx = std::min((W / T) / 2, (W - 1) / T), ty = std::min((int)(H * 0.3 / T), (H - 1) / T);       // first tile: text body, not the margin
      RenderTile(tile, pg, scale, pw, ph, tx, ty); int64_t t3 = Now();
      // rest of a 1200x1000 viewport anchored at the page top, centred horizontally
      int vx0 = std::max(0, (W - 1200) / 2), vx1 = std::min(W, vx0 + 1200), vy1 = std::min(H, 1000);
      for (int y = 0; y * T < vy1; y++) for (int x = vx0 / T; x * T < vx1; x++) { if (x == tx && y == ty) continue; RenderTile(tile, pg, scale, pw, ph, x, y); }
      int64_t t4 = Now();
      FPDF_ClosePage(pg); d.Close();
      if (it < 3) continue;                                     // 3 untimed warm-up opens
      vOpen.push_back(Ms(t1 - t0)); vPage.push_back(Ms(t2 - t1)); vFirst.push_back(Ms(t3 - t2)); vTtft.push_back(Ms(t3 - t0)); vViewport.push_back(Ms(t4 - t0));
    }
    auto so = Summarise(vOpen), sp = Summarise(vPage), sf = Summarise(vFirst), st = Summarise(vTtft), sv = Summarise(vViewport);
    fprintf(g_ttft, "%s,%d,warm,%zu,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f\n", fx.name.c_str(), T, st.n, so.p50, so.p95, sp.p50, sp.p95, sf.p50, sf.p95, st.p50, st.p95, st.max, sv.p50, sv.p95, sv.max, st.mean);
    fflush(g_ttft);
    printf("  %-16s TTFT warm tile %3d: open %.2f  page %.2f  firstTile %.2f  =>  TTFT p50 %.2f p95 %.2f max %.2f ms | viewport-complete p50 %.2f p95 %.2f\n", fx.name.c_str(), T, so.p50, sp.p50, sf.p50, st.p50, st.p95, st.max, sv.p50, sv.p95);
    fflush(stdout);
  }
}

static void BenchProgressive(const Fixture& fx, int zoom, int T) {
  Doc d; if (!d.Open(fx.path)) return;
  double scale = kPxPerPtAt100 * zoom / 100.0; Tile tile(T);
  int pi = TypicalPage(d.pages);
  FPDF_PAGE pg = FPDF_LoadPage(d.doc, pi); float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg);
  int W = (int)std::ceil(pw * scale), H = (int)std::ceil(ph * scale);
  auto picks = PickTiles(W, H, T, 24);
  // heaviest tile of this page = highest loaded-page time
  std::pair<int,int> heavy = picks.empty() ? std::pair<int,int>{0,0} : picks[0]; double hv = 0;
  for (auto p : picks) { RenderTile(tile, pg, scale, pw, ph, p.first, p.second); double ms = RenderTile(tile, pg, scale, pw, ph, p.first, p.second); if (ms > hv) { hv = ms; heavy = p; } }
  printf("  progressive: %s page %d zoom %d%% tile %d, heaviest tile (%d,%d) = %.2f ms non-progressive\n", fx.name.c_str(), pi, zoom, T, heavy.first, heavy.second, hv);
  std::vector<double> plain;
  for (int i = 0; i < 30; i++) plain.push_back(RenderTile(tile, pg, scale, pw, ph, heavy.first, heavy.second));
  auto sp = Summarise(plain);
  fprintf(g_prog, "%s,%d,%d,plain,0,%zu,%.3f,%.3f,%.3f,0,0\n", fx.name.c_str(), zoom, T, sp.n, sp.p50, sp.p95, sp.max);
  for (double slice : {0.5, 1.0, 2.0, 4.0}) {
    std::vector<double> tot, over; std::vector<double> conts;
    for (int i = 0; i < 30; i++) { auto r = RenderProgressive(tile, pg, scale, pw, ph, heavy.first, heavy.second, slice); tot.push_back(r.totalMs); over.push_back(r.maxOvershootMs); conts.push_back((double)r.continues); }
    auto st = Summarise(tot), so = Summarise(over), sc = Summarise(conts);
    fprintf(g_prog, "%s,%d,%d,slice%.1f,%.1f,%zu,%.3f,%.3f,%.3f,%.3f,%.1f\n", fx.name.c_str(), zoom, T, slice, slice, st.n, st.p50, st.p95, st.max, so.max, sc.p50);
    printf("    slice %.1f ms: total p50 %.2f p95 %.2f max %.2f (plain p50 %.2f)  continues p50 %.0f  max pause overshoot %.2f ms\n", slice, st.p50, st.p95, st.max, sp.p50, sc.p50, so.max);
  }
  // cancel latency: slice 2 ms; cancel fires at a random point inside the render
  std::mt19937 rng(7); std::vector<double> lat; int notCancelled = 0;
  for (int i = 0; i < 60; i++) {
    double delay = std::max(0.2, (0.1 + 0.8 * std::uniform_real_distribution<double>(0, 1)(rng)) * sp.p50);
    auto r = RenderCancel(tile, pg, scale, pw, ph, heavy.first, heavy.second, 2.0, delay);
    if (r.cancelled) lat.push_back(r.latencyMs); else ++notCancelled;
  }
  auto sl = Summarise(lat);
  fprintf(g_prog, "%s,%d,%d,cancel,2.0,%zu,%.3f,%.3f,%.3f,%d,0\n", fx.name.c_str(), zoom, T, sl.n, sl.p50, sl.p95, sl.max, notCancelled);
  printf("    cancel latency (flag -> engine free, slice 2 ms): n=%zu p50 %.3f p95 %.3f max %.3f ms (render finished before cancel in %d of 60); blocking render would take p50 %.2f max %.2f ms\n", sl.n, sl.p50, sl.p95, sl.max, notCancelled, sp.p50, sp.max);
  fflush(stdout); fflush(g_prog);
  FPDF_ClosePage(pg); d.Close();
}


int main(int argc, char** argv) {
  QueryPerformanceFrequency(&g_freq);
  if (argc < 2) { puts("usage: bench all|cold|dump ..."); return 2; }
  std::string mode = argv[1];
  auto setAffinity = [&](int idx) { if (argc > idx) { DWORD_PTR m = (DWORD_PTR)strtoull(argv[idx], nullptr, 0); if (m) SetThreadAffinityMask(GetCurrentThread(), m); } };
  if (mode == "cpuinfo") {
    ULONG len = 0; GetSystemCpuSetInformation(nullptr, 0, &len, GetCurrentProcess(), 0);
    std::vector<uint8_t> b(len); if (!GetSystemCpuSetInformation((PSYSTEM_CPU_SET_INFORMATION)b.data(), len, &len, GetCurrentProcess(), 0)) return 3;
    for (ULONG o = 0; o < len;) { auto* e = (PSYSTEM_CPU_SET_INFORMATION)(b.data() + o); if (e->Type == CpuSetInformation) printf("logical %u core %u efficiencyClass %u\n", e->CpuSet.LogicalProcessorIndex, e->CpuSet.CoreIndex, e->CpuSet.EfficiencyClass); o += e->Size; }
    return 0;
  }
  if (mode == "cold") {
    if (argc < 4) return 2; setAffinity(4);
    int T = atoi(argv[3]); double scale = kPxPerPtAt100;
    int64_t a = Now(); FPDF_LIBRARY_CONFIG cfg{}; cfg.version = 2; FPDF_InitLibraryWithConfig(&cfg); int64_t b = Now();
    Doc d; if (!d.Open(argv[2])) return 3; int64_t c = Now();
    int pidx = TypicalPage(d.pages); FPDF_PAGE pg = FPDF_LoadPage(d.doc, pidx); int64_t e = Now();
    float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg); int W = (int)std::ceil(pw * scale), H = (int)std::ceil(ph * scale);
    Tile tile(T); int tx = std::min((W / T) / 2, (W - 1) / T), ty = std::min((int)(H * 0.3 / T), (H - 1) / T);
    RenderTile(tile, pg, scale, pw, ph, tx, ty); int64_t f = Now();
    printf("%s,%d,cold,%.3f,%.3f,%.3f,%.3f,%.3f\n", argv[2], T, Ms(b - a), Ms(c - b), Ms(e - c), Ms(f - e), Ms(f - b));   // init, open, page, firstTile, ttft(excl init)
    return 0;
  }
  if (mode == "dump") {
    if (argc < 9) return 2;
    FPDF_InitLibrary(); Doc d; if (!d.Open(argv[2])) return 3;
    int pi = atoi(argv[3]); int zoom = atoi(argv[4]); int T = atoi(argv[5]); int tx = atoi(argv[6]), ty = atoi(argv[7]);
    double scale = kPxPerPtAt100 * zoom / 100.0; FPDF_PAGE pg = FPDF_LoadPage(d.doc, pi); Tile tile(T);
    double ms = RenderTile(tile, pg, scale, FPDF_GetPageWidthF(pg), FPDF_GetPageHeightF(pg), tx, ty);
    FILE* f = fopen(argv[8], "wb"); fwrite(tile.buf.data(), 1, tile.buf.size(), f); fclose(f);
    printf("rendered %.2f ms, nonwhite(1/7 sample) %zu\n", ms, NonWhite(tile)); return 0;
  }
  if (mode == "all") {
    if (argc < 4) return 2; setAffinity(4);
    std::string dir = argv[2], out = argv[3];
    CreateDirectoryA(out.c_str(), nullptr);
    FPDF_LIBRARY_CONFIG cfg{}; cfg.version = 2; FPDF_InitLibraryWithConfig(&cfg);
    g_tiles = fopen((out + "\\tiles.csv").c_str(), "w"); g_summary = fopen((out + "\\summary.csv").c_str(), "w");
    g_ttft = fopen((out + "\\ttft_warm.csv").c_str(), "w"); g_prog = fopen((out + "\\progressive.csv").c_str(), "w");
    fprintf(g_tiles, "fixture,tile,zoom,page,tx,ty,pass,firstOfPage,loadPageMs,renderMs,nonWhiteSampled\n");
    fprintf(g_summary, "fixture,tile,zoom,metric,n,p50,p95,p99,max,mean,tilesPerSec\n");
    fprintf(g_ttft, "fixture,tile,state,n,open_p50,open_p95,page_p50,page_p95,firstTile_p50,firstTile_p95,ttft_p50,ttft_p95,ttft_max,viewport_p50,viewport_p95,viewport_max,ttft_mean\n");
    fprintf(g_prog, "fixture,zoom,tile,mode,sliceMs,n,p50,p95,max,extra1,extra2\n");
    std::vector<Fixture> fxs; WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA((dir + "\\*.pdf").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) do { std::string n = fd.cFileName; fxs.push_back({ n.substr(0, n.size() - 4), dir + "\\" + n }); } while (FindNextFileA(h, &fd));
    FindClose(h);
    std::sort(fxs.begin(), fxs.end(), [](auto& a, auto& b) { return a.name < b.name; });
    const char* only = getenv("S2_ONLY");
    for (auto& fx : fxs) {
      if (only && *only && std::string(only).find(fx.name) == std::string::npos) continue;
      printf("== %s\n", fx.name.c_str());
      BenchThroughput(fx);
      BenchTtftWarm(fx, 40);
    }
    // progressive: the heaviest fixtures (picked by the earlier matrix), tile 512 at 100% and 400%
    const char* heavy[] = { "vector-map", "jpx-photo", "huge-page", "transparency", "usgs-topo", "text-book" };
    for (auto* hn : heavy) for (auto& fx : fxs) if (fx.name.find(hn) != std::string::npos && (!only || !*only || std::string(only).find(fx.name) != std::string::npos)) {
      printf("== progressive %s\n", fx.name.c_str());
      BenchProgressive(fx, 100, 512); BenchProgressive(fx, 400, 512);
    }
    fclose(g_tiles); fclose(g_summary); fclose(g_ttft); fclose(g_prog);
    return 0;
  }
  return 2;
}
