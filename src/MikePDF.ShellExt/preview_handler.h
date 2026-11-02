// IPreviewHandler over IInitializeWithStream (spec 3.4): a scrollable, read-only column of pages in a child window of the preview pane. Pages are rendered on demand
// through a ShellHost session that stays open for as long as the preview lives. No PDF byte is parsed here.
#pragma once

#include <Windows.h>
#include <shobjidl.h>

#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "com_support.h"
#include "page_layout.h"
#include "policy_gate.h"
#include "preview_uia.h"
#include "section.h"
#include "shell_client.h"

namespace mikepdf::shellext {

struct PreviewEnvironment {
    PolicyReader policy_reader = read_policy_from_registry;
    std::function<ClientConfig()> config = [] { return production_config(g_module, ShellFeature::Preview); };
};

class PreviewHandler final : public IInitializeWithStream, public IPreviewHandler, public IOleWindow, public IObjectWithSite, public IPreviewHandlerVisuals {
public:
    explicit PreviewHandler(PreviewEnvironment environment);
    PreviewHandler(const PreviewHandler&) = delete;
    PreviewHandler& operator=(const PreviewHandler&) = delete;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    STDMETHODIMP_(ULONG) Release() override;

    // IInitializeWithStream
    STDMETHODIMP Initialize(IStream* stream, DWORD mode) override;

    // IPreviewHandler
    STDMETHODIMP SetWindow(HWND parent, const RECT* rect) override;
    STDMETHODIMP SetRect(const RECT* rect) override;
    STDMETHODIMP DoPreview() override;
    STDMETHODIMP Unload() override;
    STDMETHODIMP SetFocus() override;
    STDMETHODIMP QueryFocus(HWND* focus) override;
    STDMETHODIMP TranslateAccelerator(MSG* message) override;

    // IOleWindow
    STDMETHODIMP GetWindow(HWND* window) override;
    STDMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }

    // IObjectWithSite
    STDMETHODIMP SetSite(IUnknown* site) override;
    STDMETHODIMP GetSite(REFIID iid, void** out) override;

    // IPreviewHandlerVisuals
    STDMETHODIMP SetBackgroundColor(COLORREF color) override;
    STDMETHODIMP SetFont(const LOGFONTW* font) override;
    STDMETHODIMP SetTextColor(COLORREF color) override;

    /// The time the preview shows nothing before it says "Loading...", the limit after which it gives up, and how long the pane must stay still before pages are
    /// re-rendered at its new width (spec 3.4).
    static constexpr UINT kLoadingMs = 300;
    static constexpr UINT kLimitMs = 5000;
    static constexpr UINT kResizeQuietMs = 100;

private:
    ~PreviewHandler();

    enum class State { Idle, Loading, Ready, Failed };

    struct Opened {
        std::uint32_t page_count{0};
        float first_width{0};
        float first_height{0};
    };
    struct Result {
        std::uint32_t page{0};
        std::uint32_t generation{0};
        std::uint32_t render_width{0};
        std::uint32_t width{0};
        std::uint32_t height{0};
        HBITMAP bitmap{nullptr};  // null: this page could not be rendered
    };
    struct Job {
        std::uint32_t page{0};
        std::uint32_t width{0};
        std::uint32_t generation{0};
    };

    static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam);
    LRESULT handle_message(UINT message, WPARAM wparam, LPARAM lparam);
    bool create_window();
    void worker_main(Section source);
    void fail(int string_id);
    void on_opened();
    void on_results();
    void on_resize_settled();
    void apply_layout();
    void scroll_to(std::int64_t position);
    void update_scroll_bar();
    void request_pages();
    void update_automation();
    void paint(HDC print_dc);
    void draw_message(HDC dc, const RECT& area, const std::wstring& text);
    bool forward_to_frame(MSG* message);
    void release_bitmaps();
    [[nodiscard]] std::wstring load_text(int id) const;
    [[nodiscard]] int dpi() const;

    PreviewEnvironment environment_;
    std::atomic<long> references_{1};
    IStream* stream_{nullptr};
    IUnknown* site_{nullptr};
    HWND parent_{nullptr};
    HWND window_{nullptr};
    RECT rect_{};
    COLORREF background_;
    COLORREF text_color_;
    HFONT font_{nullptr};

    State state_{State::Idle};
    int message_id_{0};
    DWORD started_at_{0};
    std::unique_ptr<PageLayout> layout_;
    PageCache<HBITMAP> cache_;
    std::int64_t scroll_{0};
    std::uint32_t generation_{1};
    std::uint32_t last_current_page_{0xFFFFFFFFu};
    std::int64_t gesture_last_y_{0};
    PreviewProvider* provider_{nullptr};

    // Shared with the worker. The worker never touches a window, a layout or a cache.
    std::thread worker_;
    UniqueHandle stop_event_;
    UniqueHandle wake_event_;
    std::mutex shared_;
    std::vector<Job> jobs_;
    std::vector<Result> results_;
    Opened opened_;
    int failure_string_{0};
    HWND notify_window_{nullptr};
};

}  // namespace mikepdf::shellext
