// S3b: minimal C++20 engine stub speaking the same wire protocol as the .NET stub (spikes/S3/common/Protocol.cs).
// Build: spikes/S3/engine-cpp/build.cmd   (cl /std:c++20 /guard:cf /sdl /W4 ... /link /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA)
// Two variants from this one source:
//   engine-static.exe  : pdfium.dll linked through the normal import library (loader resolves it before main)
//   engine-delay.exe   : pdfium.dll delay-loaded, so a failed load is reported precisely instead of killing the process (-DDELAYLOAD_BUILD)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <delayimp.h>
#define PSAPI_VERSION 2
#include <psapi.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <deque>
#include <memory>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <algorithm>

#include "fpdfview.h"
#include "fpdf_sysfontinfo.h"

#ifdef DELAYLOAD_BUILD
extern "C" HRESULT WINAPI __HrLoadAllImportsForDll(LPCSTR szDll);
#endif

namespace {

// ------------------------------------------------------------------------------------------------ protocol constants (mirror of Protocol.cs)
enum Msg : uint16_t { Hello = 1, Ping = 2, PingEngine = 3, OpenDocument = 4, CloseDocument = 5, RenderTile = 6, Shutdown = 7, RunNegative = 8, AllocUntilLimit = 9, GdiProbe = 10, Crash = 11, SetFontProvider = 12, GetCharBoxes = 13, ApplyRuntimeMitigations = 14, SetFonts = 15, LoadDll = 16 };
constexpr uint32_t kProtoVersion = 1;
constexpr uint16_t kResponseBit = 0x8000;
constexpr size_t kHeader = 12;
constexpr uint32_t kMaxFrame = 8u << 20;
constexpr size_t kShmHeader = 64 * 1024, kSlotSize = 4u << 20, kSlotCount = 8;
constexpr size_t kOffStage = 72;

int64_t Now() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
double Ms(int64_t t) { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return 1000.0 * static_cast<double>(t) / static_cast<double>(f.QuadPart); }

struct Writer {
  std::vector<uint8_t> b;
  template <class T> Writer& raw(T v) { auto p = reinterpret_cast<const uint8_t*>(&v); b.insert(b.end(), p, p + sizeof(T)); return *this; }
  Writer& u16(uint16_t v) { return raw(v); }
  Writer& i32(int32_t v) { return raw(v); }
  Writer& u32(uint32_t v) { return raw(v); }
  Writer& i64(int64_t v) { return raw(v); }
  Writer& f32(float v) { return raw(v); }
  Writer& str(const std::string& s) { i32(static_cast<int32_t>(s.size())); b.insert(b.end(), s.begin(), s.end()); return *this; }
};

struct Reader {
  const uint8_t* p; size_t n; size_t pos = 0; bool ok = true;
  Reader(const uint8_t* d, size_t len) : p(d), n(len) {}
  template <class T> T raw() { T v{}; if (pos + sizeof(T) > n) { ok = false; return v; } std::memcpy(&v, p + pos, sizeof(T)); pos += sizeof(T); return v; }
  int32_t i32() { return raw<int32_t>(); }
  uint32_t u32() { return raw<uint32_t>(); }
  int64_t i64() { return raw<int64_t>(); }
  float f32() { return raw<float>(); }
  std::string str(int32_t maxLen = 64 * 1024) {
    int32_t len = i32();
    if (!ok || len < 0 || len > maxLen || pos + static_cast<size_t>(len) > n) { ok = false; return {}; }
    std::string s(reinterpret_cast<const char*>(p + pos), static_cast<size_t>(len)); pos += static_cast<size_t>(len); return s;
  }
  const uint8_t* bytes(int32_t len) { if (!ok || len < 0 || pos + static_cast<size_t>(len) > n) { ok = false; return nullptr; } auto r = p + pos; pos += static_cast<size_t>(len); return r; }
};

// ------------------------------------------------------------------------------------------------ globals
HANDLE g_pipe = INVALID_HANDLE_VALUE;
HANDLE g_evRead = nullptr, g_evWrite = nullptr;
std::mutex g_writeLock;
uint8_t* g_shm = nullptr;
HANDLE g_fileFromSpawn = nullptr;
std::string g_fontMode = "gdi", g_args, g_loadMode = "hr";
int64_t g_tMain = 0, g_tPdfium = 0, g_tPipe = 0;
std::string g_pdfiumError, g_loadLog;
double g_pdfiumInitMs = 0;
std::atomic<bool> g_engineReady{false};

void Log(const char* fmt, ...) {
  HANDLE h = GetStdHandle(STD_ERROR_HANDLE);
  if (h == nullptr || h == INVALID_HANDLE_VALUE) return;
  char buf[512]; va_list ap; va_start(ap, fmt); int n = vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
  if (n > 0) { DWORD w; WriteFile(h, buf, static_cast<DWORD>(std::min<int>(n, sizeof buf - 1)), &w, nullptr); }
}

void Stage(uint64_t s) { if (g_shm) *reinterpret_cast<volatile uint64_t*>(g_shm + kOffStage) = s; }

// ------------------------------------------------------------------------------------------------ overlapped pipe I/O (a synchronous handle would deadlock reader vs writer)
bool ReadExact(void* dst, DWORD n) {
  auto* p = static_cast<uint8_t*>(dst);
  while (n) {
    OVERLAPPED ov{}; ov.hEvent = g_evRead; ResetEvent(g_evRead);
    DWORD got = 0;
    if (!ReadFile(g_pipe, p, n, nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
    if (!GetOverlappedResult(g_pipe, &ov, &got, TRUE) || got == 0) return false;
    p += got; n -= got;
  }
  return true;
}
bool WriteAll(const void* src, DWORD n) {
  auto* p = static_cast<const uint8_t*>(src);
  while (n) {
    OVERLAPPED ov{}; ov.hEvent = g_evWrite; ResetEvent(g_evWrite);
    DWORD got = 0;
    if (!WriteFile(g_pipe, p, n, nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
    if (!GetOverlappedResult(g_pipe, &ov, &got, TRUE)) return false;
    p += got; n -= got;
  }
  return true;
}
void Respond(uint16_t type, uint32_t reqId, const Writer& w) {
  std::vector<uint8_t> f(kHeader + w.b.size());
  uint32_t len = static_cast<uint32_t>(f.size() - 4); uint16_t t = static_cast<uint16_t>(type | kResponseBit), fl = 0;
  std::memcpy(f.data(), &len, 4); std::memcpy(f.data() + 4, &t, 2); std::memcpy(f.data() + 6, &fl, 2); std::memcpy(f.data() + 8, &reqId, 4);
  if (!w.b.empty()) std::memcpy(f.data() + kHeader, w.b.data(), w.b.size());
  std::lock_guard<std::mutex> g(g_writeLock);
  WriteAll(f.data(), static_cast<DWORD>(f.size()));
}

// ------------------------------------------------------------------------------------------------ document access through a HANDLE (never a path)
struct FileCtx { HANDLE h; };
int GetBlock(void* param, unsigned long pos, unsigned char* buf, unsigned long size) {
  HANDLE h = static_cast<FileCtx*>(param)->h;
  unsigned long total = 0;
  while (total < size) {
    OVERLAPPED ov{}; ov.Offset = pos + total;       // positional read on a synchronous handle; the file pointer is not shared
    DWORD got = 0;
    if (!ReadFile(h, buf + total, size - total, &got, &ov) || got == 0) return 0;
    total += got;
  }
  return 1;
}

FPDF_DOCUMENT g_doc = nullptr;
FPDF_FILEACCESS g_fa{};
FileCtx g_fctx{};
HANDLE g_docHandle = nullptr;

// ------------------------------------------------------------------------------------------------ broker-fed font provider
struct Font { std::string key; bool bold, italic; std::vector<uint8_t> data; std::string name; };
std::vector<std::unique_ptr<Font>> g_fonts;
std::mutex g_fontLock;
int g_mapCalls = 0, g_mapHits = 0;

std::string Norm(const char* face) { std::string s; for (const char* c = face ? face : ""; *c; ++c) { char ch = static_cast<char>(tolower(static_cast<unsigned char>(*c))); if (ch != ' ' && ch != '-' && ch != '_') s += ch; } return s; }

Font* ResolveFont(const char* faceRaw, int weight, bool italic, int charset = 0) {
  std::string f = Norm(faceRaw);
  bool bold = weight >= 600 || f.find("bold") != std::string::npos;
  italic = italic || f.find("italic") != std::string::npos || f.find("oblique") != std::string::npos;
  std::string key;
  if (charset == 128 || f.find("gothic") != std::string::npos || f.find("mincho") != std::string::npos) key = "gothic";
  else if (f.find("segoe") != std::string::npos) key = "segoe";
  else if (f.find("verdana") != std::string::npos) key = "verdana";
  else if (f.find("arial") != std::string::npos || f.find("helvetica") != std::string::npos) key = "arial";
  else if (f.find("times") != std::string::npos) key = "times";
  else if (f.find("courier") != std::string::npos) key = "courier";
  else if (f.find("georgia") != std::string::npos) key = "georgia";
  if (key.empty()) return nullptr;
  std::lock_guard<std::mutex> g(g_fontLock);
  Font* any = nullptr;
  for (auto& u : g_fonts) {
    if (u->key != key) continue;
    if (u->bold == bold && u->italic == italic) return u.get();
    if (!any) any = u.get();
  }
  return any;
}

uint32_t Be32(const uint8_t* p) { return (uint32_t{p[0]} << 24) | (uint32_t{p[1]} << 16) | (uint32_t{p[2]} << 8) | p[3]; }
uint16_t Be16(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }

void* MapFontCb(FPDF_SYSFONTINFO*, int weight, FPDF_BOOL italic, int charset, int, const char* face, FPDF_BOOL* exact) {
  ++g_mapCalls; if (exact) *exact = 0;
  Font* f = ResolveFont(face, weight, italic != 0, charset);
  if (f) ++g_mapHits;
  return f;
}
void* GetFontCb(FPDF_SYSFONTINFO*, const char* face) { return ResolveFont(face, 400, false); }
unsigned long GetFontDataCb(FPDF_SYSFONTINFO*, void* h, unsigned int table, unsigned char* buf, unsigned long size) {
  auto* f = static_cast<Font*>(h); if (!f) return 0;
  const uint8_t* d = f->data.data(); size_t total = f->data.size();
  uint32_t off = 0, len = static_cast<uint32_t>(total);
  const bool isTtc = total >= 4 && std::memcmp(d, "ttcf", 4) == 0;
  if (table == 0x74746366u && !isTtc) return 0;             // 'ttcf' on a plain .ttf: "no such table" (returning the whole file aborts PDFium)
  if (table != 0 && table != 0x74746366u) {
    size_t dir = isTtc ? Be32(d + 12) : 0;
    if (dir + 12 > total) return 0;
    uint16_t n = Be16(d + dir + 4); bool found = false;
    for (uint16_t i = 0; i < n && dir + 12 + 16u * (i + 1u) <= total; ++i) {
      const uint8_t* rec = d + dir + 12 + 16u * i; uint32_t tag = Be32(rec);
      uint32_t swapped = _byteswap_ulong(table);
      if (tag == table || tag == swapped) { off = Be32(rec + 8); len = Be32(rec + 12); found = true; break; }
    }
    if (!found || static_cast<size_t>(off) + len > total) return 0;
  }
  if (!buf || size < len) return len;
  std::memcpy(buf, d + off, len);
  return len;
}
unsigned long GetFaceNameCb(FPDF_SYSFONTINFO*, void* h, char* buf, unsigned long size) {
  auto* f = static_cast<Font*>(h); if (!f) return 0;
  unsigned long n = static_cast<unsigned long>(f->name.size() + 1);
  if (!buf || size < n) return n;
  std::memcpy(buf, f->name.c_str(), n); return n;
}
int GetCharsetCb(FPDF_SYSFONTINFO*, void*) { return 0; }
void DeleteFontCb(FPDF_SYSFONTINFO*, void*) {}
void ReleaseCb(FPDF_SYSFONTINFO*) {}

FPDF_SYSFONTINFO g_sysFontInfo{};
void InstallBrokerProvider() {
  g_sysFontInfo.version = 1;
  g_sysFontInfo.Release = ReleaseCb;
  g_sysFontInfo.MapFont = MapFontCb;
  g_sysFontInfo.GetFont = GetFontCb;
  g_sysFontInfo.GetFontData = GetFontDataCb;
  g_sysFontInfo.GetFaceName = GetFaceNameCb;
  g_sysFontInfo.GetFontCharset = GetCharsetCb;
  g_sysFontInfo.DeleteFont = DeleteFontCb;
  FPDF_SetSystemFontInfo(&g_sysFontInfo);
}

// ------------------------------------------------------------------------------------------------ diagnostics reported in Hello
const char* const kMitNames[] = { "DEP", "ASLR", "DynamicCode", "StrictHandleCheck", "SystemCallDisable(Win32k)", "MitigationOptionsMask", "ExtensionPointDisable",
                                  "ControlFlowGuard", "Signature", "FontDisable", "ImageLoad", "SystemCallFilter", "PayloadRestriction", "ChildProcess",
                                  "SideChannelIsolation", "UserShadowStack", "RedirectionTrust", "UserPointerAuth", "SEHOP" };

void Line(std::string& s, const std::string& k, const std::string& v) { s += k; s += '='; s += v; s += '\n'; }

std::string DescribeProcess() {
  std::string s;
  Line(s, "pid", std::to_string(GetCurrentProcessId()));
  Line(s, "runtime", "native C++ (MSVC /std:c++20, static CRT)");
  Line(s, "arch", "X64");
  Line(s, "args", g_args);
  Line(s, "pdfiumError", g_pdfiumError);
  Line(s, "pdfiumInitMs", std::to_string(g_pdfiumInitMs));
  Line(s, "loadLog", g_loadLog);
  Line(s, "fontMode", g_fontMode);
  HANDLE tok = nullptr;
  if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) {
    alignas(8) uint8_t buf[2048]; DWORD ret = 0; DWORD v = 0;
    if (GetTokenInformation(tok, static_cast<TOKEN_INFORMATION_CLASS>(29), &v, 4, &ret)) Line(s, "isAppContainer", std::to_string(v));
    if (GetTokenInformation(tok, TokenIntegrityLevel, buf, sizeof buf, &ret)) {
      auto* sid = static_cast<SID*>(reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buf)->Label.Sid);
      char hex[16]; snprintf(hex, sizeof hex, "0x%lX", sid->SubAuthority[sid->SubAuthorityCount - 1]); Line(s, "integrityRid", hex);
    }
    if (GetTokenInformation(tok, static_cast<TOKEN_INFORMATION_CLASS>(30), buf, sizeof buf, &ret)) Line(s, "capabilityCount", std::to_string(*reinterpret_cast<DWORD*>(buf)));
    if (GetTokenInformation(tok, TokenPrivileges, buf, sizeof buf, &ret)) Line(s, "privilegeCount", std::to_string(*reinterpret_cast<DWORD*>(buf)));
    CloseHandle(tok);
  }
  for (int p = 0; p < 19; ++p) {
    uint32_t b[8] = {};
    SIZE_T len = p == 0 ? 8 : p == 5 ? 24 : 4;
    char key[64]; snprintf(key, sizeof key, "mit.%s", kMitNames[p]);
    if (GetProcessMitigationPolicy(GetCurrentProcess(), static_cast<PROCESS_MITIGATION_POLICY>(p), b, len)) { char v[16]; snprintf(v, sizeof v, "0x%X", b[0]); Line(s, key, v); }
    else Line(s, key, "err" + std::to_string(GetLastError()));
  }
  if (char e[8] = {}; GetEnvironmentVariableA("S3_MODULES", e, sizeof e) && e[0] == '1') {
    HMODULE mods[256]; DWORD need = 0; std::string m;
    if (K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &need)) {
      for (DWORD i = 0; i < need / sizeof(HMODULE) && i < 256; ++i) { char nm[MAX_PATH]; if (K32GetModuleBaseNameA(GetCurrentProcess(), mods[i], nm, sizeof nm)) { for (char* c = nm; *c; ++c) *c = static_cast<char>(tolower(static_cast<unsigned char>(*c))); m += nm; m += ','; } }
    }
    Line(s, "modules", m);
  }
  return s;
}

// ------------------------------------------------------------------------------------------------ engine thread
struct Work { uint16_t type; uint32_t reqId; std::vector<uint8_t> payload; int64_t tRecv; };
std::mutex g_qLock; std::condition_variable g_qCv; std::deque<Work> g_queue; bool g_qDone = false;

const char* LoadStep(const wchar_t* dll) {
  static char buf[64];
  HMODULE m = LoadLibraryExW(dll, nullptr, 0);
  if (m) { snprintf(buf, sizeof buf, "ok"); return buf; }
  snprintf(buf, sizeof buf, "err%lu", GetLastError()); return buf;
}

void InitPdfium() {
  int64_t t0 = Now();
  Stage(1);
#ifdef DELAYLOAD_BUILD
  // Load the dependencies one by one so a failure under Win32k lockdown / signature policy is attributed to a specific DLL.
  Log("[cpp-engine] LoadLibrary(gdi32)...\n");
  g_loadLog += std::string("gdi32=") + LoadStep(L"gdi32.dll") + ";";
  Log("[cpp-engine] gdi32 returned\n");
  Log("[cpp-engine] LoadLibrary(user32)...\n");
  g_loadLog += std::string("user32=") + LoadStep(L"user32.dll") + ";";
  Log("[cpp-engine] user32 returned\n");
  SetLastError(0);
  if (g_loadMode == "probe") {   // map the image without resolving imports or running DllMain: separates image rejected from init failed"
    HMODULE m0 = LoadLibraryExW(L"pdfium.dll", nullptr, DONT_RESOLVE_DLL_REFERENCES);
    char pb[96]; snprintf(pb, sizeof pb, "pdfium-noresolve=%s(err %lu);", m0 ? "ok" : "FAIL", m0 ? 0ul : GetLastError()); g_loadLog += pb;
    Log("[cpp-engine] noresolve probe: %s\n", pb);
    if (m0) FreeLibrary(m0);
  }
  if (g_loadMode == "plain") {
    Log("[cpp-engine] LoadLibraryW(pdfium.dll)...\n");
    HMODULE m1 = LoadLibraryW(L"pdfium.dll");
    char pb[96]; snprintf(pb, sizeof pb, "pdfium-plain=%s(err %lu);", m1 ? "ok" : "FAIL", m1 ? 0ul : GetLastError()); g_loadLog += pb;
    Log("[cpp-engine] plain load: %s\n", pb);
  }
  Log("[cpp-engine] loading pdfium.dll (all imports)...\n");
  HRESULT hr = __HrLoadAllImportsForDll("pdfium.dll");
  Log("[cpp-engine] pdfium load returned 0x%08lX\n", static_cast<unsigned long>(hr));
  DWORD le = GetLastError();
  char b[96]; snprintf(b, sizeof b, "pdfium.dll=hr0x%08lX(lasterr %lu)", static_cast<unsigned long>(hr), le); g_loadLog += b;
  if (FAILED(hr)) { g_pdfiumError = std::string("pdfium.dll load failed: ") + b; Stage(90); return; }
#endif
  Stage(2);
  Log("[cpp-engine] FPDF_InitLibraryWithConfig...\n");
  FPDF_LIBRARY_CONFIG cfg{}; cfg.version = 2;
  FPDF_InitLibraryWithConfig(&cfg);
  Log("[cpp-engine] pdfium initialised\n");
  Stage(3);
  if (g_fontMode == "broker") InstallBrokerProvider();
  Stage(4);
  g_tPdfium = Now(); g_pdfiumInitMs = Ms(g_tPdfium - t0);
}

void DoOpen(Reader& r, Writer& o) {
  int64_t hv = r.i64();
  if (!g_pdfiumError.empty()) { o.i32(-4).i32(0).f32(0).f32(0).i64(Now()).i64(Now()).u32(0).str(g_pdfiumError); return; }
  if (g_doc) { FPDF_CloseDocument(g_doc); g_doc = nullptr; }
  HANDLE h = hv == 0 ? g_fileFromSpawn : reinterpret_cast<HANDLE>(hv);
  int64_t t0 = Now();
  LARGE_INTEGER sz{};
  if (!GetFileSizeEx(h, &sz) || sz.QuadPart > 0xFFFFFFFFll) { o.i32(-2).i32(0).f32(0).f32(0).i64(t0).i64(Now()).u32(0).str("size query failed or file >= 4 GiB"); return; }
  g_fctx.h = h; g_docHandle = h;
  g_fa.m_FileLen = static_cast<unsigned long>(sz.QuadPart); g_fa.m_GetBlock = GetBlock; g_fa.m_Param = &g_fctx;
  Stage(10);
  g_doc = FPDF_LoadCustomDocument(&g_fa, nullptr);
  unsigned long err = g_doc ? 0 : FPDF_GetLastError();
  int pages = 0; float pw = 0, ph = 0;
  if (g_doc) {
    pages = FPDF_GetPageCount(g_doc);
    if (FPDF_PAGE p0 = FPDF_LoadPage(g_doc, 0)) { pw = FPDF_GetPageWidthF(p0); ph = FPDF_GetPageHeightF(p0); FPDF_ClosePage(p0); }
  }
  Stage(11);
  o.i32(g_doc ? 0 : -3).i32(pages).f32(pw).f32(ph).i64(t0).i64(Now()).u32(static_cast<uint32_t>(err)).str("");
}

void DoRender(Reader& r, Writer& o, int64_t tRecv, int64_t tDeq) {
  int page = r.i32(); float scale = r.f32(); int ox = r.i32(), oy = r.i32(), w = r.i32(), h = r.i32(), slot = r.i32(), flags = r.i32();
  if (!r.ok || !g_doc) { o.i32(-10); return; }
  if (slot < 0 || slot >= static_cast<int>(kSlotCount) || w <= 0 || h <= 0 || w > 1024 || h > 1024 || !g_shm) { o.i32(-11); return; }
  int64_t t0 = Now();
  FPDF_PAGE pg = FPDF_LoadPage(g_doc, page);
  if (!pg) { o.i32(-12); return; }
  int64_t tLoaded = Now();
  float pw = FPDF_GetPageWidthF(pg), ph = FPDF_GetPageHeightF(pg);
  uint8_t* dst = g_shm + kShmHeader + static_cast<size_t>(slot) * kSlotSize;
  FPDF_BITMAP bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, dst, w * 4);   // renders straight into the shared slot
  FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFFu);
  FPDF_RenderPageBitmap(bmp, pg, -ox, -oy, static_cast<int>(pw * scale + 0.5f), static_cast<int>(ph * scale + 0.5f), 0, flags);
  FPDFBitmap_Destroy(bmp);
  FPDF_ClosePage(pg);
  int64_t t1 = Now();
  o.i32(0).i32(slot).i64(tRecv).i64(tDeq).i64(t0).i64(tLoaded).i64(t1).i64(Now()).f32(pw).f32(ph);
}

void DoSetFonts(Reader& r, Writer& o) {
  int n = r.i32(); int added = 0; size_t bytes = 0;
  for (int i = 0; i < n && r.ok && i < 64; ++i) {
    std::string key = r.str(); int style = r.i32(); int len = r.i32(); std::string face = r.str();
    const uint8_t* d = r.bytes(len);
    if (!r.ok || !d) break;
    auto f = std::make_unique<Font>(); f->key = key; f->bold = (style & 1) != 0; f->italic = (style & 2) != 0; f->name = face; f->data.assign(d, d + len);
    bytes += static_cast<size_t>(len);
    std::lock_guard<std::mutex> g(g_fontLock); g_fonts.push_back(std::move(f)); ++added;
  }
  o.i32(r.ok ? 0 : -1).i32(added).i32(static_cast<int32_t>(bytes));
}

void DoGdi(Writer& o) {
  std::vector<std::string> lines;
  char b[256];
  HMODULE g = LoadLibraryW(L"gdi32.dll");
  if (!g) { snprintf(b, sizeof b, "LoadLibrary(gdi32) => FAIL err=%lu", GetLastError()); lines.push_back(b); }
  else {
    using CreateDC_t = HDC(WINAPI*)(HDC); using CreateFontW_t = HFONT(WINAPI*)(int, int, int, int, int, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, LPCWSTR);
    using Select_t = HGDIOBJ(WINAPI*)(HDC, HGDIOBJ); using GetFace_t = int(WINAPI*)(HDC, int, LPWSTR); using GetData_t = DWORD(WINAPI*)(HDC, DWORD, DWORD, PVOID, DWORD);
    using DelDC_t = BOOL(WINAPI*)(HDC); using DelObj_t = BOOL(WINAPI*)(HGDIOBJ);
    auto cdc = reinterpret_cast<CreateDC_t>(GetProcAddress(g, "CreateCompatibleDC")); auto cf = reinterpret_cast<CreateFontW_t>(GetProcAddress(g, "CreateFontW"));
    auto sel = reinterpret_cast<Select_t>(GetProcAddress(g, "SelectObject")); auto gtf = reinterpret_cast<GetFace_t>(GetProcAddress(g, "GetTextFaceW"));
    auto gfd = reinterpret_cast<GetData_t>(GetProcAddress(g, "GetFontData")); auto ddc = reinterpret_cast<DelDC_t>(GetProcAddress(g, "DeleteDC")); auto dob = reinterpret_cast<DelObj_t>(GetProcAddress(g, "DeleteObject"));
    SetLastError(0);
    HDC dc = cdc(nullptr);
    snprintf(b, sizeof b, "CreateCompatibleDC => %s err=%lu", dc ? "ok" : "FAIL", dc ? 0ul : GetLastError()); lines.push_back(b);
    if (dc) {
      HFONT f = cf(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, L"Arial");
      snprintf(b, sizeof b, "CreateFont(Arial) => %s err=%lu", f ? "ok" : "FAIL", f ? 0ul : GetLastError()); lines.push_back(b);
      if (f) {
        HGDIOBJ old = sel(dc, f); wchar_t nm[64] = {}; int n = gtf(dc, 64, nm);
        snprintf(b, sizeof b, "GetTextFace => \"%ls\" err=%lu", n > 0 ? nm : L"", n > 0 ? 0ul : GetLastError()); lines.push_back(b);
        DWORD sz = gfd(dc, 0, 0, nullptr, 0);
        if (sz == GDI_ERROR) snprintf(b, sizeof b, "GetFontData(whole file) => GDI_ERROR err=%lu", GetLastError()); else snprintf(b, sizeof b, "GetFontData(whole file) => %lu bytes err=0", sz);
        lines.push_back(b); sel(dc, old); dob(f);
      }
      ddc(dc);
    }
  }
  snprintf(b, sizeof b, "fontProvider: MapFont calls=%d hits=%d", g_mapCalls, g_mapHits); lines.push_back(b);
  o.i32(0).i32(static_cast<int32_t>(lines.size())); for (auto& l : lines) o.str(l);
}

void DoRuntimeMitigations(Reader& r, Writer& o) {
  struct M { uint32_t bit; int policy; uint32_t flags; const char* name; };
  static const M map[] = { {1, 8, 1, "Signature.MicrosoftSignedOnly"}, {2, 13, 1, "ChildProcess.NoChildProcessCreation"}, {4, 6, 1, "ExtensionPointDisable"}, {8, 9, 1, "FontDisable.DisableNonSystemFonts"},
                           {16, 10, 7, "ImageLoad.NoRemote|NoLowLabel|PreferSystem32"}, {32, 2, 1, "DynamicCode.ProhibitDynamicCode"}, {64, 3, 3, "StrictHandleCheck.Raise|Permanent"}, {128, 4, 1, "SystemCallDisable.DisallowWin32k"} };
  uint32_t mask = r.u32(); std::vector<std::pair<std::string, int>> res;
  for (auto& m : map) { if (!(mask & m.bit)) continue; uint32_t f = m.flags; BOOL ok = SetProcessMitigationPolicy(static_cast<PROCESS_MITIGATION_POLICY>(m.policy), &f, 4); res.emplace_back(m.name, ok ? 0 : static_cast<int>(GetLastError())); }
  o.i32(0).i32(static_cast<int32_t>(res.size())); for (auto& [n, e] : res) o.str(n).i32(e);
}

void EngineThread() {
  InitPdfium();
  g_engineReady = true;
  for (;;) {
    Work w;
    { std::unique_lock<std::mutex> lk(g_qLock); g_qCv.wait(lk, [] { return !g_queue.empty() || g_qDone; }); if (g_queue.empty()) return; w = std::move(g_queue.front()); g_queue.pop_front(); }
    int64_t tDeq = Now();
    Reader r(w.payload.data(), w.payload.size()); Writer o;
    switch (w.type) {
      case PingEngine: o.i64(w.tRecv).i64(tDeq).i64(Now()); break;
      case OpenDocument: DoOpen(r, o); break;
      case CloseDocument: if (g_doc) { FPDF_CloseDocument(g_doc); g_doc = nullptr; } o.i32(0); break;
      case RenderTile: DoRender(r, o, w.tRecv, tDeq); break;
      case GdiProbe: DoGdi(o); break;
      case SetFonts: DoSetFonts(r, o); break;
      case LoadDll: { std::string p = r.str(); std::wstring wp(p.begin(), p.end()); SetLastError(0); HMODULE m = LoadLibraryExW(wp.c_str(), nullptr, 0); o.i32(m ? 0 : static_cast<int32_t>(GetLastError())); if (m) FreeLibrary(m); break; }
      case ApplyRuntimeMitigations: DoRuntimeMitigations(r, o); break;
      default: o.i32(-1).str("unknown message"); break;
    }
    Respond(w.type, w.reqId, o);
  }
}

}  // namespace

int main(int argc, char** argv) {
  g_tMain = Now();
  std::string pipeName; HANDLE section = nullptr;
  for (int i = 1; i < argc; ++i) {
    std::string a = argv[i]; g_args += (i > 1 ? " " : "") + a;
    if (a == "--pipe" && i + 1 < argc) pipeName = argv[++i];
    else if (a == "--file" && i + 1 < argc) g_fileFromSpawn = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_strtoui64(argv[++i], nullptr, 10)));
    else if (a == "--section" && i + 1 < argc) section = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_strtoui64(argv[++i], nullptr, 10)));
    else if (a == "--fontmode" && i + 1 < argc) g_fontMode = argv[++i];
    else if (a == "--loadmode" && i + 1 < argc) g_loadMode = argv[++i];
  }
  Log("[cpp-engine] main entered\n");
  if (pipeName.empty()) { Log("missing --pipe\n"); return 2; }
  if (section) {
    g_shm = static_cast<uint8_t*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    if (!g_shm) { Log("MapViewOfFile failed %lu\n", GetLastError()); return 3; }
  }
  g_evRead = CreateEventW(nullptr, TRUE, FALSE, nullptr); g_evWrite = CreateEventW(nullptr, TRUE, FALSE, nullptr);

  std::thread engine(EngineThread);

  std::wstring wname = L"\\\\.\\pipe\\" + std::wstring(pipeName.begin(), pipeName.end());
  for (int attempt = 0; attempt < 100; ++attempt) {
    g_pipe = CreateFileW(wname.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (g_pipe != INVALID_HANDLE_VALUE) break;
    DWORD e = GetLastError();
    if (e == ERROR_PIPE_BUSY) { WaitNamedPipeW(wname.c_str(), 1000); continue; }
    Log("pipe connect failed: %lu\n", e); return 4;
  }
  if (g_pipe == INVALID_HANDLE_VALUE) return 4;
  g_tPipe = Now();

  uint8_t hdr[kHeader];
  for (;;) {
    if (!ReadExact(hdr, kHeader)) break;
    uint32_t len; uint16_t type; uint32_t reqId;
    std::memcpy(&len, hdr, 4); std::memcpy(&type, hdr + 4, 2); std::memcpy(&reqId, hdr + 8, 4);
    if (len < kHeader - 4 || len > kMaxFrame) { Log("bad frame length %u\n", len); return 5; }
    std::vector<uint8_t> payload(len - (kHeader - 4));
    if (!payload.empty() && !ReadExact(payload.data(), static_cast<DWORD>(payload.size()))) break;
    int64_t tRecv = Now();
    switch (type) {
      case Hello: {
        while (!g_engineReady) Sleep(0);
        Writer w; std::string info = DescribeProcess();
        w.u32(kProtoVersion).i64(g_tMain).i64(g_tPdfium).i64(g_tPipe).i64(Now()).str(info);
        Respond(Hello, reqId, w); break;
      }
      case Ping: { Writer w; w.i64(tRecv).i64(Now()); Respond(Ping, reqId, w); break; }
      case Shutdown: { Writer w; Respond(Shutdown, reqId, w); { std::lock_guard<std::mutex> lk(g_qLock); g_qDone = true; } g_qCv.notify_all(); engine.detach(); return 0; }
      case Crash: *static_cast<volatile int*>(nullptr) = 1; break;
      default: { std::lock_guard<std::mutex> lk(g_qLock); g_queue.push_back({ type, reqId, std::move(payload), tRecv }); } g_qCv.notify_one(); break;
    }
  }
  { std::lock_guard<std::mutex> lk(g_qLock); g_qDone = true; } g_qCv.notify_all(); engine.detach();
  return 0;
}
