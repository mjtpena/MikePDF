// What the translation units of Document share and the public header must not show: the private nested types, the text conversions and the small RAII wrappers over PDFium handles.
// Only src/*.cpp include this file.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include <fpdf_annot.h>
#include <fpdf_formfill.h>
#include <fpdf_text.h>
#include <fpdfview.h>

#include "mikepdf/pdfium/document.h"

namespace mikepdf::pdfium {

/// The parsed page (and, on demand, its text) of the last page a reading service touched.
struct Document::PageCache {
    std::uint32_t index{};
    FPDF_PAGE page{nullptr};
    FPDF_TEXTPAGE text{nullptr};
    bool text_failed{false};

    PageCache() = default;
    PageCache(const PageCache&) = delete;
    PageCache& operator=(const PageCache&) = delete;
    // The text page must go before the page it was made from, and both before the document.
    ~PageCache() {
        if (text != nullptr) {
            FPDFText_ClosePage(text);
        }
        if (page != nullptr) {
            FPDF_ClosePage(page);
        }
    }

    FPDF_TEXTPAGE text_page() {
        if (text == nullptr && !text_failed) {
            text = FPDFText_LoadPage(page);
            text_failed = text == nullptr;
        }
        return text;
    }
};

/// PDFium's form-fill environment for one document. The info struct is read by PDFium for as long as the environment lives, so it is kept here; every callback in it is
/// null, which is what makes the environment headless: no scripting (no JavaScript platform), no timers, no alerts, no network or file access, no clipboard.
struct Document::FormFill {
    FPDF_FORMFILLINFO info{};
    FPDF_FORMHANDLE handle{nullptr};

    FormFill() = default;
    FormFill(const FormFill&) = delete;
    FormFill& operator=(const FormFill&) = delete;
    ~FormFill() {
        if (handle != nullptr) {
            FPDFDOC_ExitFormFillEnvironment(handle);
        }
    }
};

namespace detail {

inline constexpr double kMaxCoordinate = 1'000'000.0;  // the schema's range for every coordinate
inline constexpr unsigned long kMaxMetaRawBytes = 64 * 1024;  // refuse to copy a larger raw string out of PDFium

template <class T>
protocol::Result<T> failure(protocol::Status status, const char* detail) {
    return {status, T{}, detail};
}

inline float clamp_coordinate(double value) {
    if (!std::isfinite(value)) {
        return 0.0f;
    }
    return static_cast<float>(std::clamp(value, -kMaxCoordinate, kMaxCoordinate));
}

inline std::size_t utf8_length(char32_t cp) { return cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4; }

inline void append_utf8(std::string& out, char32_t cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

/// UTF-16LE to strict UTF-8: stops at the first NUL, replaces unpaired surrogates with U+FFFD, and stops before a code point that would not fit in `max_bytes`, so the result
/// always satisfies the schema's text rule. `truncated` (when given) says that it stopped for lack of room rather than at the end of the text.
inline std::string utf16_to_utf8(const std::vector<char16_t>& units, std::size_t max_bytes, bool* truncated = nullptr) {
    std::string out;
    if (truncated != nullptr) {
        *truncated = false;
    }
    for (std::size_t i = 0; i < units.size();) {
        char32_t cp = units[i++];
        if (cp == 0) {
            break;
        }
        if (cp >= 0xD800 && cp <= 0xDBFF && i < units.size() && units[i] >= 0xDC00 && units[i] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (units[i++] - 0xDC00);
        } else if (cp >= 0xD800 && cp <= 0xDFFF) {
            cp = 0xFFFD;
        }
        if (out.size() + utf8_length(cp) > max_bytes) {
            if (truncated != nullptr) {
                *truncated = true;
            }
            break;
        }
        append_utf8(out, cp);
    }
    return out;
}

/// The input has passed protocol::is_valid_text, so it is well-formed UTF-8.
inline std::u16string utf8_to_utf16(std::string_view text) {
    std::u16string out;
    out.reserve(text.size());
    for (std::size_t i = 0; i < text.size();) {
        const auto lead = static_cast<unsigned char>(text[i]);
        char32_t cp = 0;
        std::size_t extra = 0;
        if (lead < 0x80) {
            cp = lead;
        } else if (lead < 0xE0) {
            cp = lead & 0x1Fu;
            extra = 1;
        } else if (lead < 0xF0) {
            cp = lead & 0x0Fu;
            extra = 2;
        } else {
            cp = lead & 0x07u;
            extra = 3;
        }
        ++i;
        for (std::size_t k = 0; k < extra && i < text.size(); ++k, ++i) {
            cp = (cp << 6) | (static_cast<unsigned char>(text[i]) & 0x3Fu);
        }
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out += static_cast<char16_t>(0xD800 + (cp >> 10));
            out += static_cast<char16_t>(0xDC00 + (cp & 0x3FF));
        } else {
            out += static_cast<char16_t>(cp);
        }
    }
    return out;
}

struct PageHandle {
    FPDF_PAGE page{nullptr};
    PageHandle(FPDF_DOCUMENT doc, int index) : page(FPDF_LoadPage(doc, index)) {}
    ~PageHandle() {
        if (page != nullptr) {
            FPDF_ClosePage(page);
        }
    }
    PageHandle(const PageHandle&) = delete;
    PageHandle& operator=(const PageHandle&) = delete;
};

/// An annotation handle that is closed when it goes out of scope. Null when the position holds no dictionary.
struct AnnotHandle {
    FPDF_ANNOTATION annot{nullptr};
    AnnotHandle(FPDF_PAGE page, int index) : annot(FPDFPage_GetAnnot(page, index)) {}
    explicit AnnotHandle(FPDF_ANNOTATION raw) : annot(raw) {}
    ~AnnotHandle() {
        if (annot != nullptr) {
            FPDFPage_CloseAnnot(annot);
        }
    }
    AnnotHandle(const AnnotHandle&) = delete;
    AnnotHandle& operator=(const AnnotHandle&) = delete;
};

/// A string entry of an annotation (or form widget) dictionary, decoded to UTF-8. Empty when the key is absent or not a string or name. `max_raw_bytes` bounds the UTF-16 text PDFium is
/// asked to build: an entry larger than that reads as absent and sets `too_large`. `truncated` says that the text was cut at `max_bytes`.
inline std::string annot_text(FPDF_ANNOTATION annot, const char* key, std::size_t max_bytes, unsigned long max_raw_bytes = 64 * 1024, bool* truncated = nullptr, bool* too_large = nullptr) {
    if (truncated != nullptr) {
        *truncated = false;
    }
    if (too_large != nullptr) {
        *too_large = false;
    }
    const unsigned long needed = FPDFAnnot_GetStringValue(annot, key, nullptr, 0);  // bytes, with the 2-byte terminator
    if (needed <= 2 || needed % 2 != 0) {
        return {};
    }
    if (needed > max_raw_bytes) {
        if (too_large != nullptr) {
            *too_large = true;
        }
        return {};
    }
    std::vector<char16_t> buffer(needed / 2);
    if (FPDFAnnot_GetStringValue(annot, key, reinterpret_cast<FPDF_WCHAR*>(buffer.data()), needed) != needed) {
        return {};
    }
    return utf16_to_utf8(buffer, max_bytes, truncated);
}

}  // namespace detail
}  // namespace mikepdf::pdfium
