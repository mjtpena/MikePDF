#include "preview_handler.h"

#include <algorithm>
#include <cstring>

#include "bitmap.h"
#include "resource.h"
#include "validation.h"

namespace mikepdf::shellext {

namespace {

constexpr wchar_t kWindowClass[] = L"MikePDF.ShellPreview";
constexpr UINT WM_PREVIEW_OPENED = WM_APP + 1;
constexpr UINT WM_PREVIEW_RESULTS = WM_APP + 2;
constexpr UINT WM_PREVIEW_FAILED = WM_APP + 3;
constexpr UINT_PTR kTimerLoading = 1;
constexpr UINT_PTR kTimerLimit = 2;
constexpr UINT_PTR kTimerResize = 3;
// Sent to a child window when its parent's DPI changes; not in every SDK header.
constexpr UINT kWmDpiChangedAfterParent = 0x02E3;
constexpr std::uint64_t kPixelSectionBytes = kMaxPixelBytes;

int string_for_status(protocol::Status status) noexcept {
    switch (status) {
        case protocol::Status::PasswordRequired:
        case protocol::Status::PasswordIncorrect:
            return IDS_PREVIEW_PASSWORD;
        case protocol::Status::PermissionDenied:
            return IDS_PREVIEW_OFF;
        default:
            return IDS_PREVIEW_UNAVAILABLE;
    }
}

std::wstring replace_all(std::wstring text, const std::wstring& from, const std::wstring& to) {
    for (std::size_t at = text.find(from); at != std::wstring::npos; at = text.find(from, at + to.size())) {
        text.replace(at, from.size(), to);
    }
    return text;
}

}  // namespace

PreviewHandler::PreviewHandler(PreviewEnvironment environment)
    : environment_(std::move(environment)), background_(GetSysColor(COLOR_WINDOW)), text_color_(GetSysColor(COLOR_WINDOWTEXT)) {
    ++g_module_objects;
}

PreviewHandler::~PreviewHandler() {
    Unload();
    if (site_ != nullptr) {
        site_->Release();
    }
    if (font_ != nullptr) {
        DeleteObject(font_);
    }
    --g_module_objects;
}

STDMETHODIMP PreviewHandler::QueryInterface(REFIID iid, void** out) {
    return query_table(iid, out,
                       {{&IID_IInitializeWithStream, static_cast<IInitializeWithStream*>(this)},
                        {&IID_IPreviewHandler, static_cast<IPreviewHandler*>(this)},
                        {&IID_IOleWindow, static_cast<IOleWindow*>(this)},
                        {&IID_IObjectWithSite, static_cast<IObjectWithSite*>(this)},
                        {&IID_IPreviewHandlerVisuals, static_cast<IPreviewHandlerVisuals*>(this)}});
}

STDMETHODIMP_(ULONG) PreviewHandler::Release() {
    const long left = --references_;
    if (left == 0) {
        delete this;
    }
    return static_cast<ULONG>(left);
}

STDMETHODIMP PreviewHandler::Initialize(IStream* stream, DWORD) {
    if (stream == nullptr) {
        return E_POINTER;
    }
    if (stream_ != nullptr) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
    }
    stream_ = stream;
    stream_->AddRef();
    return S_OK;
}

STDMETHODIMP PreviewHandler::SetWindow(HWND parent, const RECT* rect) {
    if (parent == nullptr || rect == nullptr) {
        return E_INVALIDARG;
    }
    parent_ = parent;
    rect_ = *rect;
    if (window_ != nullptr) {
        ::SetParent(window_, parent);
        MoveWindow(window_, rect_.left, rect_.top, rect_.right - rect_.left, rect_.bottom - rect_.top, TRUE);
    }
    return S_OK;
}

STDMETHODIMP PreviewHandler::SetRect(const RECT* rect) {
    if (rect == nullptr) {
        return E_POINTER;
    }
    rect_ = *rect;
    if (window_ != nullptr) {
        MoveWindow(window_, rect_.left, rect_.top, rect_.right - rect_.left, rect_.bottom - rect_.top, TRUE);
    }
    return S_OK;
}

bool PreviewHandler::create_window() {
    static const ATOM atom = [] {
        WNDCLASSEXW cls{sizeof(cls)};
        cls.style = CS_DBLCLKS;
        cls.lpfnWndProc = &PreviewHandler::window_proc;
        cls.hInstance = g_module;
        cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        cls.lpszClassName = kWindowClass;
        return RegisterClassExW(&cls);
    }();
    if (atom == 0) {
        return false;
    }
    window_ = CreateWindowExW(0, kWindowClass, load_text(IDS_PREVIEW_NAME).c_str(), WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_VSCROLL, rect_.left, rect_.top,
                              rect_.right - rect_.left, rect_.bottom - rect_.top, parent_, nullptr, g_module, this);
    if (window_ == nullptr) {
        return false;
    }
    // A touch pan with one finger scrolls (spec 4).
    GESTURECONFIG pan{GID_PAN, GC_PAN | GC_PAN_WITH_INERTIA | GC_PAN_WITH_SINGLE_FINGER_VERTICALLY, 0};
    SetGestureConfig(window_, 0, 1, &pan, sizeof(pan));
    notify_window_ = window_;
    return true;
}

STDMETHODIMP PreviewHandler::DoPreview() {
    if (stream_ == nullptr || parent_ == nullptr) {
        return E_UNEXPECTED;
    }
    if (window_ != nullptr) {
        return S_OK;
    }
    if (!create_window()) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    state_ = State::Loading;
    started_at_ = GetTickCount();
    SetTimer(window_, kTimerLoading, kLoadingMs, nullptr);
    SetTimer(window_, kTimerLimit, kLimitMs, nullptr);
    update_automation();

    // The policy before anything else: a policy-off machine copies no byte and starts no ShellHost (SHELL-15). The pane says why.
    if (evaluate_policy(ShellFeature::Preview, environment_.policy_reader) == PolicyState::OffByPolicy) {
        fail(IDS_PREVIEW_OFF_POLICY);
        return S_OK;
    }
    // The stream belongs to the shell's apartment: it is copied here, on the calling thread. Only the pipe round trips run on the worker.
    Section source;
    if (FAILED(copy_stream_to_section(*stream_, kMaxStreamBytes, source))) {
        fail(IDS_PREVIEW_UNAVAILABLE);
        return S_OK;
    }
    stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    wake_event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!stop_event_ || !wake_event_) {
        fail(IDS_PREVIEW_UNAVAILABLE);
        return S_OK;
    }
    worker_ = std::thread([this, moved = std::make_shared<Section>(std::move(source))]() { worker_main(std::move(*moved)); });
    return S_OK;
}

void PreviewHandler::worker_main(Section source) {
    ShellClient client(environment_.config());
    HANDLE stop = stop_event_.get();
    const Deadline deadline = after(std::chrono::milliseconds(kLimitMs));
    const auto report_failure = [&] {
        {
            std::lock_guard lock(shared_);
            failure_string_ = string_for_status(client.last_status());
        }
        PostMessageW(notify_window_, WM_PREVIEW_FAILED, 0, 0);
    };

    if (FAILED(client.connect(deadline, stop))) {
        report_failure();
        return;
    }
    OpenedDocument document;
    if (FAILED(client.open(source, deadline, kLimitMs, document, stop))) {
        report_failure();
        return;
    }
    source = Section{};  // ShellHost has its own copy now
    {
        std::lock_guard lock(shared_);
        opened_ = {document.page_count, document.first_width, document.first_height};
    }
    PostMessageW(notify_window_, WM_PREVIEW_OPENED, 0, 0);

    Section pixels;
    if (FAILED(create_section(kPixelSectionBytes, pixels))) {
        report_failure();
        return;
    }
    HANDLE waits[2] = {stop, wake_event_.get()};
    for (;;) {
        if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0) {
            return;
        }
        for (;;) {
            if (WaitForSingleObject(stop, 0) == WAIT_OBJECT_0) {
                return;
            }
            Job job;
            {
                std::lock_guard lock(shared_);
                if (jobs_.empty()) {
                    break;
                }
                job = jobs_.front();
                jobs_.erase(jobs_.begin());
            }
            Result result{job.page, job.generation, job.width, 0, 0, nullptr};
            RenderedPage page;
            const HRESULT hr = client.render(job.page, job.width, PageLayout::render_height(), pixels, after(std::chrono::milliseconds(kLimitMs)), kLimitMs, page, stop);
            if (FAILED(hr) && !client.is_connected()) {
                if (WaitForSingleObject(stop, 0) != WAIT_OBJECT_0) {
                    report_failure();
                }
                return;
            }
            if (SUCCEEDED(hr)) {
                MappedView view;
                if (SUCCEEDED(view.map(pixels.handle.get(), page.bytes)) && SUCCEEDED(make_bitmap(view.data(), page.width, page.height, result.bitmap))) {
                    result.width = page.width;
                    result.height = page.height;
                }
            }
            {
                std::lock_guard lock(shared_);
                results_.push_back(result);
            }
            PostMessageW(notify_window_, WM_PREVIEW_RESULTS, 0, 0);
        }
    }
}

STDMETHODIMP PreviewHandler::Unload() {
    if (stop_event_) {
        SetEvent(stop_event_.get());  // aborts every wait in the worker, so the join is quick (SHELL-19: within 200 ms)
    }
    if (worker_.joinable()) {
        worker_.join();
    }
    if (window_ != nullptr) {
        KillTimer(window_, kTimerLoading);
        KillTimer(window_, kTimerLimit);
        KillTimer(window_, kTimerResize);
        UiaReturnRawElementProvider(window_, 0, 0, nullptr);  // lets go of the provider's window
        DestroyWindow(window_);
        window_ = nullptr;
        notify_window_ = nullptr;
    }
    if (provider_ != nullptr) {
        provider_->Release();
        provider_ = nullptr;
    }
    release_bitmaps();
    layout_.reset();
    stop_event_.reset();
    wake_event_.reset();
    jobs_.clear();
    state_ = State::Idle;
    scroll_ = 0;
    if (stream_ != nullptr) {
        stream_->Release();
        stream_ = nullptr;
    }
    return S_OK;
}

void PreviewHandler::release_bitmaps() {
    std::vector<HBITMAP> doomed;
    cache_.take_all(doomed);
    {
        std::lock_guard lock(shared_);
        for (const Result& result : results_) {
            doomed.push_back(result.bitmap);
        }
        results_.clear();
    }
    for (HBITMAP bitmap : doomed) {
        if (bitmap != nullptr) {
            DeleteObject(bitmap);
        }
    }
}

STDMETHODIMP PreviewHandler::SetFocus() {
    if (window_ != nullptr) {
        ::SetFocus(window_);
    }
    return S_OK;
}

STDMETHODIMP PreviewHandler::QueryFocus(HWND* focus) {
    if (focus == nullptr) {
        return E_POINTER;
    }
    *focus = GetFocus();
    return *focus != nullptr ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

bool PreviewHandler::forward_to_frame(MSG* message) {
    if (site_ == nullptr) {
        return false;
    }
    IPreviewHandlerFrame* frame = nullptr;
    if (FAILED(site_->QueryInterface(IID_PPV_ARGS(&frame))) || frame == nullptr) {
        return false;
    }
    const HRESULT hr = frame->TranslateAccelerator(message);
    frame->Release();
    return hr == S_OK;
}

STDMETHODIMP PreviewHandler::TranslateAccelerator(MSG* message) {
    // Tab, and the keys we do not use, belong to Explorer (spec 3.4): the frame decides. The arrows, Page Up and Down, Home and End are ours and reach the window.
    if (message != nullptr && message->message == WM_KEYDOWN && message->wParam == VK_TAB) {
        return forward_to_frame(message) ? S_OK : S_FALSE;
    }
    return S_FALSE;
}

STDMETHODIMP PreviewHandler::GetWindow(HWND* window) {
    if (window == nullptr) {
        return E_POINTER;
    }
    *window = window_;
    return window_ != nullptr ? S_OK : E_FAIL;
}

STDMETHODIMP PreviewHandler::SetSite(IUnknown* site) {
    if (site_ != nullptr) {
        site_->Release();
    }
    site_ = site;
    if (site_ != nullptr) {
        site_->AddRef();
    }
    return S_OK;
}

STDMETHODIMP PreviewHandler::GetSite(REFIID iid, void** out) {
    if (out == nullptr) {
        return E_POINTER;
    }
    *out = nullptr;
    return site_ != nullptr ? site_->QueryInterface(iid, out) : E_FAIL;
}

STDMETHODIMP PreviewHandler::SetBackgroundColor(COLORREF color) {
    background_ = color;
    if (window_ != nullptr) {
        InvalidateRect(window_, nullptr, FALSE);
    }
    return S_OK;
}

STDMETHODIMP PreviewHandler::SetFont(const LOGFONTW* font) {
    if (font == nullptr) {
        return E_POINTER;
    }
    HFONT made = CreateFontIndirectW(font);
    if (made == nullptr) {
        return E_FAIL;
    }
    if (font_ != nullptr) {
        DeleteObject(font_);
    }
    font_ = made;
    return S_OK;
}

STDMETHODIMP PreviewHandler::SetTextColor(COLORREF color) {
    text_color_ = color;
    return S_OK;
}

std::wstring PreviewHandler::load_text(int id) const {
    wchar_t buffer[512]{};
    const int n = LoadStringW(g_module, static_cast<UINT>(id), buffer, static_cast<int>(std::size(buffer)));
    return std::wstring(buffer, n > 0 ? static_cast<std::size_t>(n) : 0);
}

int PreviewHandler::dpi() const {
    const UINT value = window_ != nullptr ? GetDpiForWindow(window_) : 96;
    return value == 0 ? 96 : static_cast<int>(value);
}

void PreviewHandler::fail(int string_id) {
    state_ = State::Failed;
    message_id_ = string_id;
    KillTimer(window_, kTimerLoading);
    KillTimer(window_, kTimerLimit);
    if (stop_event_) {
        SetEvent(stop_event_.get());  // nothing more will be asked of ShellHost
    }
    update_automation();
    InvalidateRect(window_, nullptr, FALSE);
}

// ---- window --------------------------------------------------------------------------------------------------------------------------------------------

LRESULT CALLBACK PreviewHandler::window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams));
    }
    auto* self = reinterpret_cast<PreviewHandler*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (self == nullptr || self->window_ != window) {
        // Before CreateWindowEx returns window_ is not set yet; the messages of that moment need nothing from us.
        return DefWindowProcW(window, message, wparam, lparam);
    }
    return self->handle_message(message, wparam, lparam);
}

LRESULT PreviewHandler::handle_message(UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
        case WM_PREVIEW_OPENED:
            on_opened();
            return 0;
        case WM_PREVIEW_RESULTS:
            on_results();
            return 0;
        case WM_PREVIEW_FAILED: {
            int id = 0;
            {
                std::lock_guard lock(shared_);
                id = failure_string_;
            }
            if (state_ != State::Failed) {
                fail(id != 0 ? id : IDS_PREVIEW_UNAVAILABLE);
            }
            return 0;
        }
        case WM_TIMER:
            if (wparam == kTimerLoading) {
                KillTimer(window_, kTimerLoading);
                InvalidateRect(window_, nullptr, FALSE);
            } else if (wparam == kTimerLimit) {
                KillTimer(window_, kTimerLimit);
                if (state_ == State::Loading) {
                    fail(IDS_PREVIEW_UNAVAILABLE);  // the hard limit of spec 3.4
                }
            } else if (wparam == kTimerResize) {
                KillTimer(window_, kTimerResize);
                on_resize_settled();
            }
            return 0;
        case WM_SIZE:
        case WM_DPICHANGED:
        case kWmDpiChangedAfterParent:
            if (layout_) {
                apply_layout();
                SetTimer(window_, kTimerResize, kResizeQuietMs, nullptr);  // pages are re-rendered after 100 ms of quiet; until then the old ones are drawn scaled
            }
            return 0;
        case WM_ERASEBKGND:
            return 1;
        case WM_PAINT:
            paint(nullptr);
            return 0;
        case WM_PRINTCLIENT:
            paint(reinterpret_cast<HDC>(wparam));
            return 0;
        case WM_GETDLGCODE:
            return DLGC_WANTARROWS | DLGC_WANTCHARS;
        case WM_LBUTTONDOWN:
            ::SetFocus(window_);
            return 0;
        case WM_VSCROLL: {
            if (!layout_) {
                return 0;
            }
            const int line = MulDiv(40, dpi(), 96);
            const int page = std::max(1, layout_->pane_height() * 9 / 10);
            switch (LOWORD(wparam)) {
                case SB_LINEUP: scroll_to(scroll_ - line); break;
                case SB_LINEDOWN: scroll_to(scroll_ + line); break;
                case SB_PAGEUP: scroll_to(scroll_ - page); break;
                case SB_PAGEDOWN: scroll_to(scroll_ + page); break;
                case SB_TOP: scroll_to(0); break;
                case SB_BOTTOM: scroll_to(layout_->total_height()); break;
                case SB_THUMBTRACK:
                case SB_THUMBPOSITION: {
                    SCROLLINFO info{sizeof(info), SIF_TRACKPOS};
                    GetScrollInfo(window_, SB_VERT, &info);
                    scroll_to(info.nTrackPos);
                    break;
                }
                default: break;
            }
            return 0;
        }
        case WM_MOUSEWHEEL: {
            if (!layout_) {
                return 0;
            }
            UINT lines = 3;
            SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
            const int delta = GET_WHEEL_DELTA_WPARAM(wparam);
            const std::int64_t step = lines == WHEEL_PAGESCROLL ? std::max(1, layout_->pane_height() * 9 / 10) : static_cast<std::int64_t>(lines) * MulDiv(40, dpi(), 96);
            scroll_to(scroll_ - delta * step / WHEEL_DELTA);
            return 0;
        }
        case WM_KEYDOWN: {
            if (wparam == VK_TAB) {
                MSG keyed{window_, WM_KEYDOWN, wparam, lparam, static_cast<DWORD>(GetMessageTime()), {}};
                if (forward_to_frame(&keyed)) {
                    return 0;
                }
                break;
            }
            if (!layout_) {
                break;
            }
            const int line = MulDiv(40, dpi(), 96);
            const int page = std::max(1, layout_->pane_height() * 9 / 10);
            switch (wparam) {
                case VK_UP: scroll_to(scroll_ - line); return 0;
                case VK_DOWN: scroll_to(scroll_ + line); return 0;
                case VK_PRIOR: scroll_to(scroll_ - page); return 0;
                case VK_NEXT: scroll_to(scroll_ + page); return 0;
                case VK_HOME: scroll_to(0); return 0;
                case VK_END: scroll_to(layout_->total_height()); return 0;
                case VK_SPACE: scroll_to((GetKeyState(VK_SHIFT) & 0x8000) ? scroll_ - page : scroll_ + page); return 0;
                default: break;
            }
            break;
        }
        case WM_GESTURE: {
            GESTUREINFO info{sizeof(info)};
            if (layout_ && GetGestureInfo(reinterpret_cast<HGESTUREINFO>(lparam), &info) && info.dwID == GID_PAN) {
                if (info.dwFlags & GF_BEGIN) {
                    gesture_last_y_ = info.ptsLocation.y;
                } else {
                    scroll_to(scroll_ - (info.ptsLocation.y - gesture_last_y_));
                    gesture_last_y_ = info.ptsLocation.y;
                }
                CloseGestureInfoHandle(reinterpret_cast<HGESTUREINFO>(lparam));
                return 0;
            }
            break;
        }
        case WM_GETOBJECT:
            if (static_cast<LONG>(lparam) == static_cast<LONG>(UiaRootObjectId)) {
                if (provider_ == nullptr) {
                    provider_ = new PreviewProvider(window_);
                    update_automation();
                }
                return UiaReturnRawElementProvider(window_, wparam, lparam, provider_);
            }
            break;
        default:
            break;
    }
    return DefWindowProcW(window_, message, wparam, lparam);
}

// ---- session events ------------------------------------------------------------------------------------------------------------------------------------

void PreviewHandler::on_opened() {
    if (state_ != State::Loading) {
        return;
    }
    Opened opened;
    {
        std::lock_guard lock(shared_);
        opened = opened_;
    }
    layout_ = std::make_unique<PageLayout>(opened.page_count, static_cast<double>(opened.first_height) / static_cast<double>(opened.first_width));
    state_ = State::Ready;
    KillTimer(window_, kTimerLimit);
    KillTimer(window_, kTimerLoading);
    apply_layout();
    request_pages();
    update_automation();
    InvalidateRect(window_, nullptr, FALSE);
}

void PreviewHandler::on_results() {
    std::vector<Result> arrived;
    {
        std::lock_guard lock(shared_);
        arrived.swap(results_);
    }
    if (!layout_) {
        for (const Result& result : arrived) {
            if (result.bitmap != nullptr) {
                DeleteObject(result.bitmap);
            }
        }
        return;
    }
    std::vector<HBITMAP> evicted;
    for (const Result& result : arrived) {
        if (result.generation != generation_) {
            if (result.bitmap != nullptr) {
                DeleteObject(result.bitmap);  // asked for before the pane changed size
            }
            continue;
        }
        if (result.bitmap != nullptr) {
            scroll_ += layout_->learn(result.page, result.width, result.height, scroll_);
        }
        cache_.put(result.page, result.bitmap, result.render_width, layout_->current_page(scroll_), evicted);
    }
    for (HBITMAP bitmap : evicted) {
        if (bitmap != nullptr) {
            DeleteObject(bitmap);
        }
    }
    scroll_ = layout_->clamp_scroll(scroll_);
    update_scroll_bar();
    request_pages();
    update_automation();
    InvalidateRect(window_, nullptr, FALSE);
}

void PreviewHandler::on_resize_settled() {
    if (!layout_) {
        return;
    }
    ++generation_;
    {
        std::lock_guard lock(shared_);
        jobs_.clear();
    }
    request_pages();
    InvalidateRect(window_, nullptr, FALSE);
}

void PreviewHandler::apply_layout() {
    RECT client{};
    GetClientRect(window_, &client);
    const int scale = dpi();
    layout_->set_viewport(client.right - client.left, client.bottom - client.top, MulDiv(8, scale, 96), MulDiv(8, scale, 96));
    scroll_ = layout_->clamp_scroll(scroll_);
    update_scroll_bar();
    InvalidateRect(window_, nullptr, FALSE);
}

void PreviewHandler::scroll_to(std::int64_t position) {
    if (!layout_) {
        return;
    }
    const std::int64_t clamped = layout_->clamp_scroll(position);
    if (clamped == scroll_) {
        return;
    }
    scroll_ = clamped;
    update_scroll_bar();
    request_pages();
    update_automation();
    InvalidateRect(window_, nullptr, FALSE);
}

void PreviewHandler::update_scroll_bar() {
    if (!layout_) {
        return;
    }
    SCROLLINFO info{sizeof(info)};
    info.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
    info.nMin = 0;
    info.nMax = static_cast<int>(std::max<std::int64_t>(layout_->total_height() - 1, 0));
    info.nPage = static_cast<UINT>(layout_->pane_height());
    info.nPos = static_cast<int>(scroll_);
    SetScrollInfo(window_, SB_VERT, &info, TRUE);
}

void PreviewHandler::request_pages() {
    if (!layout_ || !wake_event_) {
        return;
    }
    const PageLayout::Range range = layout_->visible(scroll_);
    const std::uint32_t center = layout_->current_page(scroll_);
    // At most as many pages as the cache holds, the ones nearest the middle of the view first, then the next page and the one before (SHELL-19: four pages in memory).
    std::vector<std::uint32_t> wanted;
    for (std::uint32_t page = range.first; page <= range.last && wanted.size() < 64; ++page) {
        wanted.push_back(page);
    }
    std::sort(wanted.begin(), wanted.end(), [center](std::uint32_t a, std::uint32_t b) {
        const std::uint32_t da = a > center ? a - center : center - a;
        const std::uint32_t db = b > center ? b - center : center - b;
        return da != db ? da < db : a < b;
    });
    if (center + 1 < layout_->page_count() && std::find(wanted.begin(), wanted.end(), center + 1) == wanted.end()) {
        wanted.push_back(center + 1);
    }
    if (wanted.size() > kMaxCachedPages) {
        wanted.resize(kMaxCachedPages);
    }
    std::vector<Job> jobs;
    for (const std::uint32_t page : wanted) {
        const auto* entry = cache_.find(page);
        if (entry == nullptr || entry->render_width != layout_->render_width()) {
            jobs.push_back({page, layout_->render_width(), generation_});
        }
    }
    {
        std::lock_guard lock(shared_);
        jobs_ = std::move(jobs);
    }
    SetEvent(wake_event_.get());
}

void PreviewHandler::update_automation() {
    if (provider_ == nullptr) {
        return;
    }
    std::wstring value;
    if (state_ == State::Ready && layout_) {
        value = replace_all(replace_all(load_text(IDS_PREVIEW_VALUE), L"{0}", std::to_wstring(layout_->current_page(scroll_) + 1)), L"{1}", std::to_wstring(layout_->page_count()));
    } else if (state_ == State::Failed) {
        value = load_text(message_id_);
    } else {
        value = load_text(IDS_PREVIEW_LOADING);
    }
    provider_->update(load_text(IDS_PREVIEW_NAME), value);
}

// ---- painting ------------------------------------------------------------------------------------------------------------------------------------------

void PreviewHandler::draw_message(HDC dc, const RECT& area, const std::wstring& text) {
    HGDIOBJ previous = SelectObject(dc, font_ != nullptr ? static_cast<HGDIOBJ>(font_) : GetStockObject(DEFAULT_GUI_FONT));
    ::SetTextColor(dc, text_color_);
    SetBkMode(dc, TRANSPARENT);
    RECT inset = area;
    InflateRect(&inset, -MulDiv(16, dpi(), 96), -MulDiv(16, dpi(), 96));
    RECT measured = inset;
    DrawTextW(dc, text.c_str(), -1, &measured, DT_CENTER | DT_WORDBREAK | DT_CALCRECT | DT_NOPREFIX);
    const int height = measured.bottom - measured.top;
    inset.top += std::max<LONG>(0, (inset.bottom - inset.top - height) / 2);
    DrawTextW(dc, text.c_str(), -1, &inset, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX);
    SelectObject(dc, previous);
}

void PreviewHandler::paint(HDC print_dc) {
    PAINTSTRUCT paint_struct{};
    HDC screen = print_dc != nullptr ? print_dc : BeginPaint(window_, &paint_struct);
    RECT client{};
    GetClientRect(window_, &client);
    const int width = client.right - client.left;
    const int height = client.bottom - client.top;
    HDC dc = CreateCompatibleDC(screen);
    HBITMAP buffer = CreateCompatibleBitmap(screen, std::max(width, 1), std::max(height, 1));
    HGDIOBJ previous = SelectObject(dc, buffer);

    HBRUSH background = CreateSolidBrush(background_);
    FillRect(dc, &client, background);
    DeleteObject(background);

    if (state_ == State::Ready && layout_) {
        HBRUSH white = static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH));
        const PageLayout::Range range = layout_->visible(scroll_);
        for (std::uint32_t page = range.first; page <= range.last; ++page) {
            const int page_w = layout_->page_width(page);
            const int page_h = layout_->page_height(page);
            const int left = (width - page_w) / 2;
            const int top = static_cast<int>(layout_->page_top(page) - scroll_);
            const RECT slot{left, top, left + page_w, top + page_h};
            const auto* entry = cache_.find(page);
            if (entry != nullptr && entry->value != nullptr) {
                HDC source = CreateCompatibleDC(dc);
                HGDIOBJ old = SelectObject(source, entry->value);
                BITMAP info{};
                GetObjectW(entry->value, sizeof(info), &info);
                if (info.bmWidth == page_w && info.bmHeight == page_h) {
                    BitBlt(dc, left, top, page_w, page_h, source, 0, 0, SRCCOPY);
                } else {
                    // Rendered at another width (the pane changed size and the new render is not here yet): scaled, so there is never a blank page.
                    SetStretchBltMode(dc, HALFTONE);
                    StretchBlt(dc, left, top, page_w, page_h, source, 0, 0, info.bmWidth, info.bmHeight, SRCCOPY);
                }
                SelectObject(source, old);
                DeleteDC(source);
            } else {
                FillRect(dc, &slot, white);
            }
        }
    } else if (state_ == State::Failed) {
        draw_message(dc, client, load_text(message_id_));
    } else if (state_ == State::Loading && GetTickCount() - started_at_ >= kLoadingMs) {
        draw_message(dc, client, load_text(IDS_PREVIEW_LOADING));
    }

    BitBlt(screen, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    SelectObject(dc, previous);
    DeleteObject(buffer);
    DeleteDC(dc);
    if (print_dc == nullptr) {
        EndPaint(window_, &paint_struct);
    }
}

}  // namespace mikepdf::shellext
