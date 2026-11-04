// SaveDocument (schema 7, docs/features/save-pipeline.md 11.1): PDFium's save of the in-memory document into a file handle the broker duplicated into the engine.
// Criteria SP-11 (the engine half), AC-14 (a display mirror flag never reaches a saved file), and the handle rules of ADR-0005 (a handle that arrives is consumed on every path).
#include <gtest/gtest.h>

#include <Windows.h>

#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include "engine_core.h"
#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using engine::Clock;
using engine::EngineCore;
using engine::EngineKind;
using protocol::MessageType;
using protocol::RequestEnvelope;
using protocol::Status;
using support::PdfBuilder;

constexpr auto kNoDeadline = Clock::time_point::max();

RequestEnvelope make(MessageType type, std::uint32_t document_id, protocol::RequestPayload payload) {
    RequestEnvelope request;
    request.request_id = 1;
    request.document_id = document_id;
    request.message_type = type;
    request.payload = std::move(payload);
    return request;
}

// A one-page document with a highlight (index 0) and a note (index 1), both flagged Print (4).
std::vector<std::uint8_t> annotated() {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page = b.reserve();
    const int content = b.add_stream("q Q");
    const int highlight = b.add("<< /Type /Annot /Subtype /Highlight /Rect [10 10 100 30] /QuadPoints [10 30 100 30 10 10 100 10] /C [1 1 0] /F 4 /NM (h) >>");
    const int note = b.add("<< /Type /Annot /Subtype /Text /Rect [10 100 34 124] /Contents (note) /F 4 /NM (n) >>");
    b.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R >>");
    b.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 /MediaBox [0 0 200 200] >>");
    b.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /Contents " + std::to_string(content) + " 0 R /Annots [" + std::to_string(highlight) + " 0 R " + std::to_string(note) + " 0 R] >>");
    return support::bytes_of(b.build(catalog));
}

class SaveTest : public ::testing::Test {
protected:
    SaveTest() : core_(EngineKind::Pool) {  // a pool engine holds several documents, which the mirror test needs
        dir_ = std::filesystem::temp_directory_path() / ("mikepdf-save-test-" + std::to_string(GetCurrentProcessId()));
        std::filesystem::create_directories(dir_);
    }
    ~SaveTest() override {
        core_.close_all();
        std::error_code ignored;
        std::filesystem::remove_all(dir_, ignored);
    }

    std::filesystem::path file(const std::string& name) const { return dir_ / name; }

    void write(const std::filesystem::path& path, const std::vector<std::uint8_t>& bytes) const {
        std::ofstream out(path, std::ios::binary);
        out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    }

    static std::vector<std::uint8_t> read(const std::filesystem::path& path) {
        std::ifstream in(path, std::ios::binary);
        return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
    }

    std::uint32_t open(const std::vector<std::uint8_t>& bytes, const char* name = "input.pdf") {
        const auto path = file(name);
        write(path, bytes);
        protocol::OpenDocument payload;
        payload.handle = support::open_handle(path);
        auto request = make(MessageType::OpenDocument, 0, payload);
        const auto reply = core_.execute(request, kNoDeadline);
        EXPECT_EQ(reply.status, Status::Ok);
        return reply.payload ? std::get<protocol::OpenDocumentReply>(*reply.payload).document_id : 0;
    }

    // A fresh, empty, writable file as the broker would hand it over: the value is a handle in this process, and the engine closes it.
    std::uint64_t output_handle(const std::filesystem::path& path, DWORD access = GENERIC_WRITE) const {
        if (access == GENERIC_READ) {
            write(path, {'x'});
        }
        const HANDLE h = CreateFileW(path.c_str(), access, FILE_SHARE_READ, nullptr, access == GENERIC_READ ? OPEN_EXISTING : CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(h));
    }

    engine::Reply save(std::uint32_t document, std::uint16_t mode, std::uint32_t flags, std::uint64_t handle, Clock::time_point deadline = kNoDeadline) {
        protocol::SaveDocument payload;
        payload.mode = mode;
        payload.flags = flags;
        payload.output_handle = handle;
        auto request = make(MessageType::SaveDocument, document, payload);
        return core_.execute(request, deadline);
    }

    static protocol::ListAnnotationsReply annotations(const std::vector<std::uint8_t>& bytes) {
        auto doc = pdfium::Document::open(bytes, {});
        EXPECT_EQ(doc.status, Status::Ok);
        auto list = doc.value.list_annotations(0, 0, 64);
        EXPECT_EQ(list.status, Status::Ok);
        return list.value;
    }

    pdfium::Library library_;
    EngineCore core_;
    std::filesystem::path dir_;
};

TEST_F(SaveTest, AnIncrementalSaveWritesTheOriginalAsAPrefixAndReportsWhatItWrote) {
    const auto original = annotated();
    const auto id = open(original);
    const auto out = file("out.pdf");
    const auto reply = save(id, 0, 0, output_handle(out));
    ASSERT_EQ(reply.status, Status::Ok);
    const auto& r = std::get<protocol::SaveDocumentReply>(*reply.payload);
    const auto written = read(out);
    EXPECT_EQ(r.bytes_written, written.size());
    EXPECT_EQ(r.page_count, 1u);
    ASSERT_GT(written.size(), original.size());
    EXPECT_EQ(std::memcmp(written.data(), original.data(), original.size()), 0);
    EXPECT_TRUE(support::no_handles_left(out)) << "the engine closed the handle";
}

TEST_F(SaveTest, AFullSaveRewritesTheFile) {
    const auto original = annotated();
    const auto id = open(original);
    const auto out = file("full.pdf");
    const auto reply = save(id, 1, 0, output_handle(out));
    ASSERT_EQ(reply.status, Status::Ok);
    const auto written = read(out);
    EXPECT_EQ(std::get<protocol::SaveDocumentReply>(*reply.payload).bytes_written, written.size());
    ASSERT_GE(written.size(), 8u);
    EXPECT_EQ(std::string(written.begin(), written.begin() + 5), "%PDF-");
    EXPECT_NE(std::memcmp(written.data(), original.data(), std::min(written.size(), original.size())), 0) << "not an extension of the original";
    EXPECT_EQ(annotations(written).total, 2u);
}

TEST_F(SaveTest, AMirrorFlagIsNeverWritten) {
    // AC-14 through the message path: SetAnnotationHidden, then SaveDocument both ways.
    for (const std::uint16_t mode : {std::uint16_t{0}, std::uint16_t{1}}) {
        const auto id = open(annotated(), ("in" + std::to_string(mode) + ".pdf").c_str());
        for (const std::uint32_t index : {0u, 1u}) {
            protocol::SetAnnotationHidden hide;
            hide.page = 0;
            hide.index = index;
            hide.hidden = true;
            auto request = make(MessageType::SetAnnotationHidden, id, hide);
            ASSERT_EQ(core_.execute(request, kNoDeadline).status, Status::Ok);
        }
        const auto out = file("mirror" + std::to_string(mode) + ".pdf");
        ASSERT_EQ(save(id, mode, 0, output_handle(out)).status, Status::Ok);
        const auto list = annotations(read(out));
        ASSERT_EQ(list.flags.size(), 2u);
        EXPECT_EQ(list.flags[0], 4u) << "mode " << mode;
        EXPECT_EQ(list.flags[1], 4u) << "mode " << mode;

        // And what a reader of this engine sees is unchanged: still hidden, still reported with the file's flags.
        protocol::ListAnnotations ask;
        ask.page = 0;
        ask.first = 0;
        ask.count = 2;
        auto request = make(MessageType::ListAnnotations, id, ask);
        const auto after = core_.execute(request, kNoDeadline);
        ASSERT_EQ(after.status, Status::Ok);
        EXPECT_EQ(std::get<protocol::ListAnnotationsReply>(*after.payload).flags, (std::vector<std::uint32_t>{4, 4}));
        const auto saved = read(out);
        ASSERT_GE(saved.size(), 5u);
        EXPECT_EQ(std::string(saved.begin(), saved.begin() + 5), "%PDF-");
    }
}

TEST_F(SaveTest, RefusalsConsumeTheHandleToo) {
    const auto id = open(annotated());
    {
        const auto out = file("flattened.pdf");
        EXPECT_EQ(save(id, 2, 0, output_handle(out)).status, Status::Unsupported);  // the flattened copy is the writer host's
        EXPECT_TRUE(support::no_handles_left(out));
        EXPECT_TRUE(read(out).empty());
    }
    {
        const auto out = file("flags.pdf");
        EXPECT_EQ(save(id, 0, 1, output_handle(out)).status, Status::InvalidArgument);  // flatten flags without the flattened mode
        EXPECT_TRUE(support::no_handles_left(out));
    }
    {
        const auto out = file("late.pdf");
        EXPECT_EQ(save(id, 0, 0, output_handle(out), Clock::now() - std::chrono::seconds(1)).status, Status::Timeout);
        EXPECT_TRUE(support::no_handles_left(out));
        EXPECT_TRUE(read(out).empty());
    }
    {
        const auto out = file("nodoc.pdf");
        EXPECT_EQ(save(id + 5, 0, 0, output_handle(out)).status, Status::InvalidArgument);  // no such document
        EXPECT_TRUE(support::no_handles_left(out));
    }
    {
        const auto out = file("readonly.pdf");
        EXPECT_EQ(save(id, 0, 0, output_handle(out, GENERIC_READ)).status, Status::PermissionDenied);  // a handle that cannot be written
        EXPECT_TRUE(support::no_handles_left(out));
    }
    EXPECT_EQ(save(id, 0, 0, 0x7777).status, Status::InvalidArgument);  // a value that is not a handle costs one request, not the process
    EXPECT_EQ(save(id, 0, 0, ~0ull).status, Status::InvalidArgument);
}

TEST_F(SaveTest, TheShellKindDoesNotServeItAndStillClosesTheHandle) {
    EngineCore shell(EngineKind::Shell);
    const auto out = file("shell.pdf");
    protocol::SaveDocument payload;
    payload.mode = 0;
    payload.flags = 0;
    payload.output_handle = output_handle(out);
    auto request = make(MessageType::SaveDocument, 1, payload);
    EXPECT_EQ(shell.execute(request, kNoDeadline).status, Status::Unsupported);
    EXPECT_TRUE(support::no_handles_left(out));
    shell.close_all();
}

}  // namespace
