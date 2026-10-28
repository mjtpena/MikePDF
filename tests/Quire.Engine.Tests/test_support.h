// Helpers shared by the engine unit tests: fixture loading and a tiny PDF builder.
#pragma once

#include <Windows.h>

#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#ifndef QUIRE_FIXTURE_DIR
#error "QUIRE_FIXTURE_DIR must point at tests/Quire.Integration.Tests/Fixtures"
#endif

namespace quire::support {

inline std::filesystem::path fixture_path(const std::string& name) { return std::filesystem::path(QUIRE_FIXTURE_DIR) / name; }

inline std::vector<std::uint8_t> read_fixture(const std::string& name) {
    std::ifstream in(fixture_path(name), std::ios::binary);
    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}

inline std::vector<std::uint8_t> bytes_of(const std::string& text) { return {text.begin(), text.end()}; }

/// Opens a file read-only the way the broker does and returns the handle value for an OpenDocument request.
/// The engine side closes it.
inline std::uint64_t open_handle(const std::filesystem::path& path) {
    const HANDLE handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(handle));
}

/// True when nothing holds the file open any more. The files are opened with FILE_SHARE_READ only, so a write open succeeds
/// exactly when every read handle (and mapping) is gone. Handle values are recycled quickly, so probing the value would lie.
inline bool no_handles_left(const std::filesystem::path& path) {
    const HANDLE probe = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (probe == INVALID_HANDLE_VALUE) {
        return false;
    }
    CloseHandle(probe);
    return true;
}

/// A private copy of a fixture in the temp directory, removed on destruction.
struct TempCopy {
    std::filesystem::path path;
    explicit TempCopy(const std::string& fixture)
        : path(std::filesystem::temp_directory_path() / ("quire-engine-test-" + std::to_string(GetCurrentProcessId()) + "-" + fixture)) {
        std::filesystem::copy_file(fixture_path(fixture), path, std::filesystem::copy_options::overwrite_existing);
    }
    ~TempCopy() {
        std::error_code ignored;
        std::filesystem::remove(path, ignored);
    }
    TempCopy(const TempCopy&) = delete;
    TempCopy& operator=(const TempCopy&) = delete;
};

/// A one-page PDF with the given content stream, MediaBox, optional /Info dictionary body and extra page dictionary entries.
inline std::string build_pdf(const std::string& content, const std::string& media_box = "0 0 200 200", const std::string& info = "",
                            const std::string& page_extra = "") {
    std::string pdf = "%PDF-1.7\n";
    std::vector<std::size_t> offsets;
    const auto add = [&](const std::string& body) {
        offsets.push_back(pdf.size());
        pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n";
    };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [" + media_box + "] >>");
    add("<< /Type /Page /Parent 2 0 R /Contents 4 0 R " + page_extra + " >>");
    add("<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream");
    if (!info.empty()) {
        add("<< " + info + " >>");
    }
    const std::size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (const std::size_t offset : offsets) {
        char line[32];
        std::snprintf(line, sizeof line, "%010zu 00000 n \n", offset);
        pdf += line;
    }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R" + (info.empty() ? "" : " /Info 5 0 R") + " >>\nstartxref\n" +
           std::to_string(xref) + "\n%%EOF\n";
    return pdf;
}

/// A page with `count` small filled rectangles: enough objects that PDFium consults its pause callback.
inline std::string many_rectangles(int count) {
    std::string content = "0.2 0.4 0.8 rg\n";
    for (int i = 0; i < count; ++i) {
        content += std::to_string(i % 190) + " " + std::to_string((i / 190) % 190) + " 5 5 re f\n";
    }
    return content;
}

struct Bgra {
    std::uint8_t b, g, r, a;
};

inline Bgra pixel(const std::vector<std::uint8_t>& buffer, std::uint32_t stride, std::uint32_t x, std::uint32_t y) {
    const std::size_t at = std::size_t{y} * stride + std::size_t{x} * 4;
    return {buffer[at], buffer[at + 1], buffer[at + 2], buffer[at + 3]};
}

}  // namespace quire::support
