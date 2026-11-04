// The form messages of schema 7 (docs/features/forms.md 10.3): GetFormFields, GetFormFieldDetail, SetFormFieldValue and FormAction.
//
// PDFium has no setter for a field value. A value changes only through the events a person would cause (focus the widget, select all, replace the selection, kill the focus; a click;
// set-index-selected), which is also what makes it enforce /MaxLen and the choice list and regenerate the field's appearance. The form-fill environment these events go to is the one
// the renderer already uses (Document::form_fill): headless, with no callbacks, no scripting and no network, so nothing a widget's actions say can run beyond a reset.
//
// A fieldId is the position of a widget in the document's widget list, which is built once, page by page, in document order (the pinned PDFium has no call that reads a page's /Tabs).
// Widgets that share a fully qualified name are one field and share a value. Every PDFium call here that is marked Experimental is listed in EXPERIMENTAL.md.
#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

#include "document_internal.h"

namespace mikepdf::pdfium {

using protocol::Result;
using protocol::Status;
using namespace detail;

namespace {

constexpr std::uint32_t kMaxWidgets = 100'000;
constexpr std::size_t kMaxNameBytes = 256;
constexpr unsigned long kMaxValueRawBytes = 1u << 20;  // a value larger than this reads as truncated and empty
constexpr std::size_t kMaxValueBytes = 8192;
constexpr int kMaxOptions = 10'000;
constexpr std::size_t kMaxPagesListed = 256;
constexpr unsigned int kPermFillOrAnnotate = 32u | 256u;  // bit 6 (add or modify annotations and fill forms) or bit 9 (fill in form fields)

constexpr std::uint16_t kStateChecked = 1;
constexpr std::uint16_t kStateScript = 2;
constexpr std::uint16_t kStateHidden = 4;
constexpr std::uint16_t kStateSkipped = 8;

constexpr int kFlagReadOnly = FPDF_FORMFLAG_READONLY;
constexpr int kFlagPassword = FPDF_FORMFLAG_TEXT_PASSWORD;
constexpr int kFlagComboEdit = FPDF_FORMFLAG_CHOICE_EDIT;
constexpr int kFlagMultiSelect = FPDF_FORMFLAG_CHOICE_MULTI_SELECT;

// Cuts at a code point boundary, so that the result is still valid UTF-8.
std::string cut_utf8(std::string text, std::size_t max_bytes) {
    if (text.size() <= max_bytes) {
        return text;
    }
    std::size_t end = max_bytes;
    while (end > 0 && (static_cast<unsigned char>(text[end]) & 0xC0) == 0x80) {
        --end;
    }
    text.resize(end);
    return text;
}

// One of the form getters (a form handle, an annotation, a UTF-16 buffer) as UTF-8. `getter(buffer, bytes)` returns the size in bytes with the terminator.
template <class Getter>
std::string form_text(Getter getter, std::size_t max_bytes, bool* truncated = nullptr) {
    if (truncated != nullptr) {
        *truncated = false;
    }
    const unsigned long needed = getter(nullptr, 0);
    if (needed <= 2 || needed % 2 != 0) {
        return {};
    }
    if (needed > kMaxValueRawBytes) {
        if (truncated != nullptr) {
            *truncated = true;
        }
        return {};
    }
    std::vector<char16_t> buffer(needed / 2);
    if (getter(reinterpret_cast<FPDF_WCHAR*>(buffer.data()), needed) != needed) {
        return {};
    }
    return utf16_to_utf8(buffer, max_bytes, truncated);
}

std::string field_name(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, std::size_t max_bytes) {
    return form_text([&](FPDF_WCHAR* b, unsigned long n) { return FPDFAnnot_GetFormFieldName(form, annot, b, n); }, max_bytes);
}

std::string field_alternate_name(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, std::size_t max_bytes) {
    return form_text([&](FPDF_WCHAR* b, unsigned long n) { return FPDFAnnot_GetFormFieldAlternateName(form, annot, b, n); }, max_bytes);
}

std::string field_value(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, std::size_t max_bytes, bool* truncated = nullptr) {
    return form_text([&](FPDF_WCHAR* b, unsigned long n) { return FPDFAnnot_GetFormFieldValue(form, annot, b, n); }, max_bytes, truncated);
}

std::string field_option(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, int index, std::size_t max_bytes) {
    return form_text([&](FPDF_WCHAR* b, unsigned long n) { return FPDFAnnot_GetOptionLabel(form, annot, index, b, n); }, max_bytes);
}

std::string field_export_value(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, std::size_t max_bytes) {
    return form_text([&](FPDF_WCHAR* b, unsigned long n) { return FPDFAnnot_GetFormFieldExportValue(form, annot, b, n); }, max_bytes);
}

int field_type(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot) {
    const int type = FPDFAnnot_GetFormFieldType(form, annot);
    return type < 0 || type > FPDF_FORMFIELD_SIGNATURE ? 0 : type;
}

int option_count(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot) {
    const int count = FPDFAnnot_GetOptionCount(form, annot);
    return count < 0 ? 0 : std::min(count, kMaxOptions);
}

// Whether the widget has an action script of any of the four kinds: keystroke, format, validate or calculate. Nothing here runs one.
bool has_script(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot) {
    for (const int event : {FPDF_ANNOT_AACTION_KEY_STROKE, FPDF_ANNOT_AACTION_FORMAT, FPDF_ANNOT_AACTION_VALIDATE, FPDF_ANNOT_AACTION_CALCULATE}) {
        if (FPDFAnnot_GetFormAdditionalActionJavaScript(form, annot, event, nullptr, 0) > 2) {
            return true;
        }
    }
    return false;
}

bool has_script_of(FPDF_FORMHANDLE form, FPDF_ANNOTATION annot, int event) { return FPDFAnnot_GetFormAdditionalActionJavaScript(form, annot, event, nullptr, 0) > 2; }

struct Rect {
    float left{0}, bottom{0}, right{0}, top{0};
};

Rect widget_rect(FPDF_ANNOTATION annot) {
    FS_RECTF raw{};
    Rect out;
    if (FPDFAnnot_GetRect(annot, &raw) == 0) {
        return out;
    }
    const float a = clamp_coordinate(raw.left);
    const float b = clamp_coordinate(raw.right);
    const float c = clamp_coordinate(raw.bottom);
    const float d = clamp_coordinate(raw.top);
    out.left = std::min(a, b);
    out.right = std::max(a, b);
    out.bottom = std::min(c, d);
    out.top = std::max(c, d);
    return out;
}

// A page loaded into the form-fill environment for as long as the object lives. PDFium answers some questions about a widget (is it checked, which options are selected, what is the
// export value) only for a widget whose page the environment has been told about: a check box on a page that was merely loaded reads as unchecked.
struct FormPage {
    PageHandle page;
    FPDF_FORMHANDLE form;
    bool loaded;

    FormPage(FPDF_DOCUMENT doc, FPDF_FORMHANDLE form_handle, std::uint32_t index) : page(doc, static_cast<int>(index)), form(form_handle), loaded(page.page != nullptr) {
        if (loaded) {
            FORM_OnAfterLoadPage(page.page, form);
        }
    }
    ~FormPage() {
        if (loaded) {
            FORM_OnBeforeClosePage(page.page, form);
        }
    }
    FormPage(const FormPage&) = delete;
    FormPage& operator=(const FormPage&) = delete;

    [[nodiscard]] FPDF_PAGE get() const { return page.page; }
};

// A widget opened for an edit: its page is loaded into the form-fill environment for as long as the object lives, and the focus is taken away again at the end (which is also what
// commits a value that was typed). Declaration order matters: the body of the destructor runs before the annotation and the page are closed.
struct OpenWidget {
    PageHandle page;
    AnnotHandle annot;
    FPDF_FORMHANDLE form;
    bool loaded{false};

    OpenWidget(FPDF_DOCUMENT doc, FPDF_FORMHANDLE form_handle, std::uint32_t page_index, std::uint32_t annot_index)
        : page(doc, static_cast<int>(page_index)), annot(page.page != nullptr ? FPDFPage_GetAnnot(page.page, static_cast<int>(annot_index)) : nullptr), form(form_handle) {
        if (page.page != nullptr && annot.annot != nullptr && FPDFAnnot_GetSubtype(annot.annot) == FPDF_ANNOT_WIDGET) {
            FORM_OnAfterLoadPage(page.page, form);
            loaded = true;
        }
    }
    ~OpenWidget() {
        if (loaded) {
            (void)FORM_ForceToKillFocus(form);
            FORM_OnBeforeClosePage(page.page, form);
        }
    }
    OpenWidget(const OpenWidget&) = delete;
    OpenWidget& operator=(const OpenWidget&) = delete;

    [[nodiscard]] bool ok() const { return loaded; }
};

// The value a person would read: the field's text, or the export value of the selected option for a choice field.
std::string value_of(OpenWidget& w, std::size_t max_bytes = kMaxValueBytes, bool* truncated = nullptr) { return field_value(w.form, w.annot.annot, max_bytes, truncated); }

// Text goes in through the events a person's typing causes. The edit enforces /MaxLen itself. A text field takes the whole text at once (replacing the selection, which is everything).
// An editable combo box has to be typed into: PDFium keeps the text of its edit only when it was changed by a key press, so a replaced selection is thrown away when the focus goes.
bool put_text(OpenWidget& w, const std::string& utf8, bool typed = false) {
    const std::u16string wide = utf8_to_utf16(utf8);
    if (FORM_SetFocusedAnnot(w.form, w.annot.annot) == 0) {
        return false;
    }
    (void)FORM_SelectAllText(w.form, w.page.page);
    if (typed) {
        constexpr int kBackspace = 8;
        if (wide.empty()) {
            FORM_OnChar(w.form, w.page.page, kBackspace, 0);  // deletes the selection
        }
        for (const char16_t unit : wide) {
            FORM_OnChar(w.form, w.page.page, unit, 0);
        }
    } else {
        FORM_ReplaceSelection(w.form, w.page.page, reinterpret_cast<FPDF_WIDESTRING>(wide.c_str()));
    }
    return FORM_ForceToKillFocus(w.form) != 0;  // commits the value
}

// A click in the middle of the widget, if that is the widget PDFium finds there; otherwise at four points inside its corners.
bool click(OpenWidget& w) {
    const Rect r = widget_rect(w.annot.annot);
    const int own = FPDFPage_GetAnnotIndex(w.page.page, w.annot.annot);
    const double dx = (static_cast<double>(r.right) - r.left);
    const double dy = (static_cast<double>(r.top) - r.bottom);
    if (!(dx > 0.0) || !(dy > 0.0)) {
        return false;
    }
    const double xs[] = {r.left + dx / 2, r.left + dx * 0.25, r.left + dx * 0.75, r.left + dx * 0.25, r.left + dx * 0.75};
    const double ys[] = {r.bottom + dy / 2, r.bottom + dy * 0.25, r.bottom + dy * 0.25, r.bottom + dy * 0.75, r.bottom + dy * 0.75};
    for (int i = 0; i < 5; ++i) {
        const FS_POINTF point{static_cast<float>(xs[i]), static_cast<float>(ys[i])};
        AnnotHandle hit(FPDFAnnot_GetFormFieldAtPoint(w.form, w.page.page, &point));
        if (hit.annot == nullptr || FPDFPage_GetAnnotIndex(w.page.page, hit.annot) != own) {
            continue;  // another widget lies over that point
        }
        const bool down = FORM_OnLButtonDown(w.form, w.page.page, 0, xs[i], ys[i]) != 0;
        const bool up = FORM_OnLButtonUp(w.form, w.page.page, 0, xs[i], ys[i]) != 0;
        (void)FORM_ForceToKillFocus(w.form);
        return down || up;
    }
    return false;
}

std::vector<int> selected_options(OpenWidget& w, int total, std::size_t limit) {
    std::vector<int> selected;
    for (int i = 0; i < total && selected.size() < limit; ++i) {
        if (FPDFAnnot_IsOptionSelected(w.form, w.annot.annot, i) != 0) {
            selected.push_back(i);
        }
    }
    return selected;
}

// Sets exactly these options (a list box) or this one option (a combo box, which cannot be left without one). Returns whether the field ends up as asked.
bool put_choice(OpenWidget& w, const std::vector<std::uint32_t>& wanted, int total, int type) {
    if (FORM_SetFocusedAnnot(w.form, w.annot.annot) == 0) {
        return false;
    }
    if (type == FPDF_FORMFIELD_LISTBOX) {
        for (const int index : selected_options(w, total, static_cast<std::size_t>(total))) {
            if (std::find(wanted.begin(), wanted.end(), static_cast<std::uint32_t>(index)) == wanted.end()) {
                (void)FORM_SetIndexSelected(w.form, w.page.page, index, 0);
            }
        }
    }
    for (const std::uint32_t index : wanted) {
        if (FORM_IsIndexSelected(w.form, w.page.page, static_cast<int>(index)) == 0 && FORM_SetIndexSelected(w.form, w.page.page, static_cast<int>(index), 1) == 0) {
            return false;
        }
    }
    return FORM_ForceToKillFocus(w.form) != 0;
}

}  // namespace

// ---- the widget list ----------------------------------------------------------------------------------------------------------------------

Status Document::ensure_widgets(const std::function<bool()>& expired) {
    if (widgets_ready_) {
        return Status::Ok;
    }
    FormFill* form = form_fill();
    if (form == nullptr) {
        return Status::Unsupported;
    }
    if (FPDF_GetFormType(handle_) == FORMTYPE_NONE) {
        widgets_ready_ = true;  // without an /AcroForm a widget is a drawing, not a field
        return Status::Ok;
    }
    while (widgets_scanned_pages_ < page_count_ && widgets_.size() < kMaxWidgets) {
        if (expired && expired()) {
            return Status::Timeout;
        }
        PageHandle page(handle_, static_cast<int>(widgets_scanned_pages_));
        if (page.page != nullptr) {
            const int counted = FPDFPage_GetAnnotCount(page.page);
            const int entries = counted < 0 ? 0 : static_cast<int>(std::min<std::uint32_t>(static_cast<std::uint32_t>(counted), 100'000));
            for (int i = 0; i < entries && widgets_.size() < kMaxWidgets; ++i) {
                AnnotHandle annot(page.page, i);
                if (annot.annot == nullptr || FPDFAnnot_GetSubtype(annot.annot) != FPDF_ANNOT_WIDGET) {
                    continue;
                }
                widgets_.push_back({widgets_scanned_pages_, static_cast<std::uint32_t>(i), field_type(form->handle, annot.annot), field_name(form->handle, annot.annot, kMaxNameBytes)});
            }
        }
        ++widgets_scanned_pages_;
    }
    widgets_ready_ = true;
    return Status::Ok;
}

Result<protocol::GetFormFieldsReply> Document::form_fields(std::uint32_t first, std::uint32_t count, const std::function<bool()>& expired) {
    using Reply = protocol::GetFormFieldsReply;
    if (handle_ == nullptr || count == 0 || count > 64 || first > 99'999) {
        return failure<Reply>(Status::InvalidArgument, "form field request out of bounds");
    }
    if (const Status built = ensure_widgets(expired); built != Status::Ok) {
        return failure<Reply>(built, built == Status::Timeout ? "the form fields could not be listed in time" : "no form-fill environment");
    }
    FormFill* form = form_fill();
    Reply reply;
    reply.total = static_cast<std::uint32_t>(widgets_.size());
    const std::uint32_t end = static_cast<std::uint32_t>(std::min<std::uint64_t>(std::uint64_t{first} + count, widgets_.size()));
    std::unique_ptr<FormPage> page;
    std::uint32_t open_page = 0xFFFFFFFFu;
    FS_RECTF visible{};
    bool have_visible = false;
    for (std::uint32_t id = first; id < end; ++id) {
        const Widget& w = widgets_[id];
        if (page == nullptr || open_page != w.page) {
            page = std::make_unique<FormPage>(handle_, form->handle, w.page);
            open_page = w.page;
            have_visible = page->get() != nullptr && FPDF_GetPageBoundingBox(page->get(), &visible) != 0;
        }
        AnnotHandle annot(page->get() != nullptr ? FPDFPage_GetAnnot(page->get(), static_cast<int>(w.index)) : nullptr);
        reply.field_ids.push_back(id);
        reply.pages.push_back(w.page);
        if (annot.annot == nullptr) {
            reply.lefts.push_back(0.f);
            reply.bottoms.push_back(0.f);
            reply.rights.push_back(0.f);
            reply.tops.push_back(0.f);
            reply.types.push_back(0);
            reply.flags.push_back(0);
            reply.states.push_back(kStateSkipped);
            reply.max_lens.push_back(0);
            reply.names.emplace_back();
            reply.alternate_names.emplace_back();
            reply.values.emplace_back();
            continue;
        }
        const Rect r = widget_rect(annot.annot);
        reply.lefts.push_back(r.left);
        reply.bottoms.push_back(r.bottom);
        reply.rights.push_back(r.right);
        reply.tops.push_back(r.top);
        const int type = w.type;
        const int flags = FPDFAnnot_GetFormFieldFlags(form->handle, annot.annot);
        const int annot_flags = file_flags(w.page, w.index, FPDFAnnot_GetFlags(annot.annot));
        reply.types.push_back(static_cast<std::uint16_t>(type));
        reply.flags.push_back(static_cast<std::uint32_t>(flags));
        const std::string value = (flags & kFlagPassword) != 0 ? std::string{} : field_value(form->handle, annot.annot, 128);
        std::uint16_t state = 0;
        if (type == FPDF_FORMFIELD_CHECKBOX || type == FPDF_FORMFIELD_RADIOBUTTON) {
            state |= FPDFAnnot_IsChecked(form->handle, annot.annot) != 0 ? kStateChecked : 0;
        } else if (type == FPDF_FORMFIELD_COMBOBOX || type == FPDF_FORMFIELD_LISTBOX) {
            state |= value.empty() ? 0 : kStateChecked;
        }
        if (has_script(form->handle, annot.annot)) {
            state |= kStateScript;
        }
        const bool hidden = (annot_flags & (FPDF_ANNOT_FLAG_HIDDEN | FPDF_ANNOT_FLAG_NOVIEW)) != 0;
        if (hidden) {
            state |= kStateHidden;
        }
        const bool outside = have_visible && (r.right < std::min(visible.left, visible.right) || r.left > std::max(visible.left, visible.right) ||
                                              r.top < std::min(visible.bottom, visible.top) || r.bottom > std::max(visible.bottom, visible.top));
        if (hidden || (flags & kFlagReadOnly) != 0 || outside) {
            state |= kStateSkipped;
        }
        reply.states.push_back(state);
        float max_len = 0;
        const bool has_max = FPDFAnnot_GetNumberValue(annot.annot, "MaxLen", &max_len) != 0 && std::isfinite(max_len) && max_len >= 1.0f;
        reply.max_lens.push_back(has_max ? static_cast<std::uint16_t>(std::min(max_len, 65'535.0f)) : std::uint16_t{0});
        reply.names.push_back(cut_utf8(w.name, 128));
        reply.alternate_names.push_back(field_alternate_name(form->handle, annot.annot, 128));
        reply.values.push_back(value);
    }
    return {Status::Ok, std::move(reply), ""};
}

Result<protocol::GetFormFieldDetailReply> Document::form_field_detail(std::uint32_t field_id, std::uint32_t first_option, const std::function<bool()>& expired) {
    using Reply = protocol::GetFormFieldDetailReply;
    if (handle_ == nullptr || field_id > 99'999 || first_option > 9999) {
        return failure<Reply>(Status::InvalidArgument, "form field request out of bounds");
    }
    if (const Status built = ensure_widgets(expired); built != Status::Ok) {
        return failure<Reply>(built, "the form fields could not be listed");
    }
    if (field_id >= widgets_.size()) {
        return failure<Reply>(Status::InvalidArgument, "no such field");
    }
    FormFill* form = form_fill();
    const Widget& w = widgets_[field_id];
    FormPage page(handle_, form->handle, w.page);
    AnnotHandle handle(page.get() != nullptr ? FPDFPage_GetAnnot(page.get(), static_cast<int>(w.index)) : nullptr);
    FPDF_ANNOTATION annot = handle.annot;
    if (annot == nullptr) {
        return failure<Reply>(Status::Damaged, "the widget cannot be loaded");
    }
    Reply reply;
    const int type = w.type;
    reply.type = static_cast<std::uint16_t>(type);
    reply.flags = static_cast<std::uint32_t>(FPDFAnnot_GetFormFieldFlags(form->handle, annot));
    reply.name = cut_utf8(field_name(form->handle, annot, 512), 512);
    reply.alternate_name = field_alternate_name(form->handle, annot, 256);
    reply.value = field_value(form->handle, annot, kMaxValueBytes, &reply.value_truncated);
    bool default_truncated = false;
    bool default_large = false;
    reply.default_value = annot_text(annot, "DV", kMaxValueBytes, 1u << 20, &default_truncated, &default_large);
    float number = 0;
    if (FPDFAnnot_GetNumberValue(annot, "MaxLen", &number) != 0 && std::isfinite(number) && number >= 1.0f) {
        reply.max_len = static_cast<std::uint16_t>(std::min(number, 65'535.0f));
    }
    number = 0;
    if (FPDFAnnot_GetNumberValue(annot, "Q", &number) != 0 && number >= 1.0f) {
        reply.quadding = number >= 2.0f ? 2 : 1;
    }
    float font_size = 0;
    if (FPDFAnnot_GetFontSize(form->handle, annot, &font_size) != 0 && std::isfinite(font_size)) {
        reply.font_size = std::clamp(font_size, 0.0f, 1000.0f);
    }
    reply.widget_count = 1;
    if (type == FPDF_FORMFIELD_CHECKBOX || type == FPDF_FORMFIELD_RADIOBUTTON) {
        reply.export_value = field_export_value(form->handle, annot, 64);
        reply.checked = FPDFAnnot_IsChecked(form->handle, annot) != 0;
        const int group = FPDFAnnot_GetFormControlCount(form->handle, annot);
        reply.widget_count = group < 1 ? 1 : static_cast<std::uint32_t>(std::min(group, 100'000));
    }
    if (type == FPDF_FORMFIELD_COMBOBOX || type == FPDF_FORMFIELD_LISTBOX) {
        const int total = option_count(form->handle, annot);
        reply.options_total = static_cast<std::uint32_t>(total);
        for (int i = static_cast<int>(first_option); i < total && reply.option_labels.size() < 64; ++i) {
            reply.option_labels.push_back(field_option(form->handle, annot, i, 128));
        }
        reply.option_values = reply.option_labels;  // the pinned PDFium reads an option's label only
        for (int i = 0; i < total && reply.selected_indices.size() < 64; ++i) {
            if (FPDFAnnot_IsOptionSelected(form->handle, annot, i) != 0) {
                reply.selected_indices.push_back(static_cast<std::uint32_t>(i));
            }
        }
    }
    reply.has_format_script = has_script_of(form->handle, annot, FPDF_ANNOT_AACTION_FORMAT);
    reply.has_validate_script = has_script_of(form->handle, annot, FPDF_ANNOT_AACTION_VALIDATE);
    reply.has_calculate_script = has_script_of(form->handle, annot, FPDF_ANNOT_AACTION_CALCULATE);
    return {Status::Ok, std::move(reply), ""};
}

// ---- changing values -----------------------------------------------------------------------------------------------------------------------

namespace {

// The pages with a widget of the same field as widget `id`: its own page first, then the others in ascending order. Fields without a name are their one widget.
template <class Widgets>
void affected_pages(const Widgets& widgets, std::uint32_t id, std::vector<std::uint32_t>& pages, bool& truncated) {
    pages.push_back(widgets[id].page);
    if (widgets[id].name.empty()) {
        return;
    }
    std::vector<std::uint32_t> others;
    std::uint32_t last = 0xFFFFFFFFu;
    for (const auto& w : widgets) {  // widgets are in page order, so equal pages are adjacent
        if (w.name == widgets[id].name && w.type == widgets[id].type && w.page != widgets[id].page && w.page != last) {
            others.push_back(w.page);
            last = w.page;
        }
    }
    for (const std::uint32_t page : others) {
        if (pages.size() >= kMaxPagesListed) {
            truncated = true;
            return;
        }
        pages.push_back(page);
    }
}

}  // namespace

Result<protocol::SetFormFieldValueReply> Document::set_form_field_value(const protocol::SetFormFieldValue& request, const std::function<bool()>& expired) {
    using Reply = protocol::SetFormFieldValueReply;
    if (handle_ == nullptr || request.field_id > 99'999 || request.kind > 2 || request.text.size() > kMaxValueBytes || request.selected.size() > 64) {
        return failure<Reply>(Status::InvalidArgument, "form value request out of bounds");
    }
    FormFill* form = form_fill();
    if (form == nullptr) {
        return failure<Reply>(Status::Unsupported, "no form-fill environment");
    }
    if (const Status built = ensure_widgets(expired); built != Status::Ok) {
        return failure<Reply>(built, "the form fields could not be listed");
    }
    if (request.field_id >= widgets_.size()) {
        return failure<Reply>(Status::InvalidArgument, "no such field");
    }
    if ((static_cast<unsigned int>(FPDF_GetDocPermissions(handle_)) & kPermFillOrAnnotate) == 0) {
        return failure<Reply>(Status::PermissionDenied, "the document does not allow filling in forms");
    }
    const Widget& w = widgets_[request.field_id];
    OpenWidget open(handle_, form->handle, w.page, w.index);
    if (!open.ok()) {
        return failure<Reply>(Status::Damaged, "the widget cannot be loaded");
    }
    const int type = w.type;
    const int flags = FPDFAnnot_GetFormFieldFlags(form->handle, open.annot.annot);
    if ((flags & kFlagReadOnly) != 0) {
        return failure<Reply>(Status::PermissionDenied, "the field is read-only");
    }

    bool accepted = false;
    if (request.kind == 0) {
        if (type != FPDF_FORMFIELD_TEXTFIELD && !(type == FPDF_FORMFIELD_COMBOBOX && (flags & kFlagComboEdit) != 0)) {
            return failure<Reply>(Status::InvalidArgument, "the field does not take text");
        }
        if (!put_text(open, request.text, type == FPDF_FORMFIELD_COMBOBOX)) {
            return failure<Reply>(Status::Damaged, "the widget could not take the focus");
        }
        accepted = value_of(open) == request.text;
    } else if (request.kind == 1) {
        if (type != FPDF_FORMFIELD_CHECKBOX && type != FPDF_FORMFIELD_RADIOBUTTON) {
            return failure<Reply>(Status::InvalidArgument, "the field is not a check box or a radio button");
        }
        if ((FPDFAnnot_IsChecked(form->handle, open.annot.annot) != 0) != request.checked && !click(open)) {
            return failure<Reply>(Status::Damaged, "the widget could not be clicked");
        }
        accepted = (FPDFAnnot_IsChecked(form->handle, open.annot.annot) != 0) == request.checked;
    } else {
        if (type != FPDF_FORMFIELD_COMBOBOX && type != FPDF_FORMFIELD_LISTBOX) {
            return failure<Reply>(Status::InvalidArgument, "the field is not a choice field");
        }
        const int total = option_count(form->handle, open.annot.annot);
        const bool single = type == FPDF_FORMFIELD_COMBOBOX || (flags & kFlagMultiSelect) == 0;
        if ((single && request.selected.size() > 1) || (type == FPDF_FORMFIELD_COMBOBOX && request.selected.empty())) {
            return failure<Reply>(Status::InvalidArgument, "the number of selected options does not fit the field");
        }
        for (std::size_t i = 0; i < request.selected.size(); ++i) {
            if (request.selected[i] >= static_cast<std::uint32_t>(total) || (i > 0 && request.selected[i] <= request.selected[i - 1])) {
                return failure<Reply>(Status::InvalidArgument, "an option is out of range or the options are not in ascending order");
            }
        }
        if (!put_choice(open, request.selected, total, type)) {
            return failure<Reply>(Status::Damaged, "the options could not be selected");
        }
        const std::vector<int> now = selected_options(open, total, request.selected.size() + 1);
        accepted = now.size() == request.selected.size() && std::equal(now.begin(), now.end(), request.selected.begin(), [](int a, std::uint32_t b) { return static_cast<std::uint32_t>(a) == b; });
    }

    Reply reply;
    reply.accepted = accepted;
    reply.value = value_of(open);
    affected_pages(widgets_, request.field_id, reply.affected_pages, reply.truncated);
    return {Status::Ok, std::move(reply), ""};
}

namespace {

// Puts one widget's field back to its default. Returns whether anything changed. The default is /DV of the widget's own dictionary (a value kept on a parent field cannot be reached with the
// pinned PDFium), or nothing: empty text, no selection (a combo box that has to keep one is left as it is), unchecked.
bool reset_widget(FPDF_DOCUMENT doc, FPDF_FORMHANDLE form, std::uint32_t page_index, std::uint32_t annot_index, int type) {
    OpenWidget w(doc, form, page_index, annot_index);
    if (!w.ok()) {
        return false;
    }
    const int flags = FPDFAnnot_GetFormFieldFlags(form, w.annot.annot);
    if ((flags & kFlagReadOnly) != 0) {
        return false;
    }
    const std::string wanted = annot_text(w.annot.annot, "DV", kMaxValueBytes, 1u << 20);
    switch (type) {
        case FPDF_FORMFIELD_TEXTFIELD:
            return value_of(w) != wanted && put_text(w, wanted) && value_of(w) == wanted;
        case FPDF_FORMFIELD_COMBOBOX: {
            if ((flags & kFlagComboEdit) != 0) {
                return value_of(w) != wanted && put_text(w, wanted, true) && value_of(w) == wanted;
            }
            const int total = option_count(form, w.annot.annot);
            for (int i = 0; i < total; ++i) {
                if (field_option(form, w.annot.annot, i, kMaxValueBytes) == wanted && !wanted.empty()) {
                    return FPDFAnnot_IsOptionSelected(form, w.annot.annot, i) == 0 && put_choice(w, {static_cast<std::uint32_t>(i)}, total, type);
                }
            }
            return false;
        }
        case FPDF_FORMFIELD_LISTBOX: {
            const int total = option_count(form, w.annot.annot);
            std::vector<std::uint32_t> want;
            for (int i = 0; i < total && !wanted.empty(); ++i) {
                if (field_option(form, w.annot.annot, i, kMaxValueBytes) == wanted) {
                    want.push_back(static_cast<std::uint32_t>(i));
                    break;
                }
            }
            const std::vector<int> now = selected_options(w, total, static_cast<std::size_t>(total));
            const bool same = now.size() == want.size() && std::equal(now.begin(), now.end(), want.begin(), [](int a, std::uint32_t b) { return static_cast<std::uint32_t>(a) == b; });
            return !same && put_choice(w, want, total, type);
        }
        case FPDF_FORMFIELD_CHECKBOX:
        case FPDF_FORMFIELD_RADIOBUTTON: {
            const bool on_by_default = !wanted.empty() && wanted != "Off" && wanted == field_export_value(form, w.annot.annot, 64);
            return (FPDFAnnot_IsChecked(form, w.annot.annot) != 0) != on_by_default && click(w) && (FPDFAnnot_IsChecked(form, w.annot.annot) != 0) == on_by_default;
        }
        default:
            return false;
    }
}

bool resettable(int type) { return type >= FPDF_FORMFIELD_CHECKBOX && type <= FPDF_FORMFIELD_TEXTFIELD; }

}  // namespace

Result<protocol::FormActionReply> Document::form_action(std::uint32_t field_id, std::uint16_t action, const std::function<bool()>& expired) {
    using Reply = protocol::FormActionReply;
    if (handle_ == nullptr || field_id > 99'999 || action > 2) {
        return failure<Reply>(Status::InvalidArgument, "form action request out of bounds");
    }
    FormFill* form = form_fill();
    if (form == nullptr) {
        return failure<Reply>(Status::Unsupported, "no form-fill environment");
    }
    if (const Status built = ensure_widgets(expired); built != Status::Ok) {
        return failure<Reply>(built, "the form fields could not be listed");
    }
    if (action != 1 && field_id >= widgets_.size()) {
        return failure<Reply>(Status::InvalidArgument, "no such field");
    }
    if ((static_cast<unsigned int>(FPDF_GetDocPermissions(handle_)) & kPermFillOrAnnotate) == 0) {
        return failure<Reply>(Status::PermissionDenied, "the document does not allow filling in forms");
    }
    Reply reply;
    std::vector<std::uint32_t> pages;  // every page whose widgets were reset, without duplicates, in the order found

    if (action == 0) {
        const Widget& target = widgets_[field_id];
        if (!resettable(target.type)) {
            return failure<Reply>(Status::InvalidArgument, "the field cannot be reset");
        }
        // A radio group or a check box group is one field whose buttons are reset one by one; a text or choice field shares one value across its widgets, so its own widget is enough.
        const bool grouped = target.type == FPDF_FORMFIELD_CHECKBOX || target.type == FPDF_FORMFIELD_RADIOBUTTON;
        bool changed = false;
        for (std::uint32_t id = 0; id < widgets_.size(); ++id) {
            const Widget& w = widgets_[id];
            const bool member = id == field_id || (grouped && !target.name.empty() && w.name == target.name && w.type == target.type);
            if (!member) {
                continue;
            }
            if (reset_widget(handle_, form->handle, w.page, w.index, w.type)) {
                changed = true;
                std::vector<std::uint32_t> field_pages;
                bool ignored = false;
                affected_pages(widgets_, id, field_pages, ignored);
                for (const std::uint32_t p : field_pages) {
                    if (std::find(pages.begin(), pages.end(), p) == pages.end()) {
                        pages.push_back(p);
                    }
                }
            }
        }
        reply.performed = changed ? 1 : 0;
    } else if (action == 1) {
        std::unordered_set<std::string> done;
        bool changed = false;
        for (std::uint32_t id = 0; id < widgets_.size(); ++id) {
            const Widget& w = widgets_[id];
            if (!resettable(w.type)) {
                continue;
            }
            const bool grouped = w.type == FPDF_FORMFIELD_CHECKBOX || w.type == FPDF_FORMFIELD_RADIOBUTTON;
            if (!w.name.empty() && !grouped && !done.insert(std::to_string(w.type) + ":" + w.name).second) {
                continue;  // a text or choice field shares one value across its widgets
            }
            if (expired && expired()) {
                return failure<Reply>(Status::Timeout, "the reset ran out of time");
            }
            if (reset_widget(handle_, form->handle, w.page, w.index, w.type)) {
                changed = true;
                // The other widgets of the field show the new value too.
                std::vector<std::uint32_t> field_pages;
                bool ignored = false;
                affected_pages(widgets_, id, field_pages, ignored);
                for (const std::uint32_t p : field_pages) {
                    if (std::find(pages.begin(), pages.end(), p) == pages.end()) {
                        pages.push_back(p);
                    }
                }
            }
        }
        reply.performed = changed ? 1 : 0;
    } else {
        const Widget& button = widgets_[field_id];
        if (button.type != FPDF_FORMFIELD_PUSHBUTTON) {
            return failure<Reply>(Status::InvalidArgument, "the field is not a push button");
        }
        // The button is pressed in an environment with no callbacks and no script engine, so a reset is the only thing it can do. The values of every field before and after tell whether it did.
        const auto snapshot = [&]() {
            std::vector<std::string> values;
            std::unique_ptr<FormPage> page;
            std::uint32_t open_page = 0xFFFFFFFFu;
            for (const Widget& w : widgets_) {
                if (!resettable(w.type)) {
                    values.emplace_back();
                    continue;
                }
                if (page == nullptr || open_page != w.page) {
                    page = std::make_unique<FormPage>(handle_, form->handle, w.page);
                    open_page = w.page;
                }
                AnnotHandle annot(page->get() != nullptr ? FPDFPage_GetAnnot(page->get(), static_cast<int>(w.index)) : nullptr);
                std::string value;
                if (annot.annot != nullptr) {
                    value = field_value(form->handle, annot.annot, kMaxValueBytes);
                    if (w.type == FPDF_FORMFIELD_CHECKBOX || w.type == FPDF_FORMFIELD_RADIOBUTTON) {
                        value += FPDFAnnot_IsChecked(form->handle, annot.annot) != 0 ? "\x01" : "\x02";
                    }
                }
                values.push_back(std::move(value));
            }
            return values;
        };
        const std::vector<std::string> before = snapshot();
        {
            OpenWidget open(handle_, form->handle, button.page, button.index);
            if (!open.ok()) {
                return failure<Reply>(Status::Damaged, "the widget cannot be loaded");
            }
            if (!click(open)) {
                return failure<Reply>(Status::Damaged, "the button could not be pressed");
            }
        }
        const std::vector<std::string> after = snapshot();
        for (std::size_t i = 0; i < widgets_.size() && i < before.size() && i < after.size(); ++i) {
            if (before[i] != after[i] && std::find(pages.begin(), pages.end(), widgets_[i].page) == pages.end()) {
                pages.push_back(widgets_[i].page);
            }
        }
        reply.performed = pages.empty() ? 2 : 1;
    }

    std::sort(pages.begin(), pages.end());
    if (pages.size() > kMaxPagesListed) {
        pages.resize(kMaxPagesListed);
        reply.truncated = true;
    }
    reply.affected_pages = std::move(pages);
    return {Status::Ok, std::move(reply), ""};
}

}  // namespace mikepdf::pdfium
