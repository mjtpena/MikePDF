// The stream copy and the bitmap: what the handler does with bytes it never parses.
#include <gtest/gtest.h>

#include <shlwapi.h>

#include <vector>

#include "bitmap.h"
#include "section.h"
#include "validation.h"

namespace {

using namespace mikepdf::shellext;

IStream* memory_stream(const std::vector<std::uint8_t>& bytes) { return SHCreateMemStream(bytes.data(), static_cast<UINT>(bytes.size())); }

TEST(StreamCopy, CopiesEveryByteIntoASection) {
    std::vector<std::uint8_t> bytes(3'000'000);
    for (std::size_t i = 0; i < bytes.size(); ++i) {
        bytes[i] = static_cast<std::uint8_t>(i * 31 + 7);
    }
    IStream* stream = memory_stream(bytes);
    ASSERT_NE(stream, nullptr);
    Section section;
    ASSERT_EQ(copy_stream_to_section(*stream, kMaxStreamBytes, section), S_OK);
    EXPECT_EQ(section.bytes, bytes.size());
    MappedView view;
    ASSERT_EQ(view.map(section.handle.get(), section.bytes), S_OK);
    EXPECT_EQ(std::memcmp(view.data(), bytes.data(), bytes.size()), 0);
    stream->Release();
}

TEST(StreamCopy, StartsFromTheBeginningWhateverThePosition) {
    const std::vector<std::uint8_t> bytes = {1, 2, 3, 4, 5, 6, 7, 8};
    IStream* stream = memory_stream(bytes);
    LARGE_INTEGER middle{};
    middle.QuadPart = 5;
    ASSERT_EQ(stream->Seek(middle, STREAM_SEEK_SET, nullptr), S_OK);
    Section section;
    ASSERT_EQ(copy_stream_to_section(*stream, kMaxStreamBytes, section), S_OK);
    MappedView view;
    ASSERT_EQ(view.map(section.handle.get(), section.bytes), S_OK);
    EXPECT_EQ(view.data()[0], 1);
    EXPECT_EQ(section.bytes, 8u);
    stream->Release();
}

TEST(StreamCopy, AnEmptyStreamFails) {
    IStream* stream = memory_stream({});
    // SHCreateMemStream with no bytes still gives a stream of size 0.
    ASSERT_NE(stream, nullptr);
    Section section;
    EXPECT_EQ(copy_stream_to_section(*stream, kMaxStreamBytes, section), HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
    EXPECT_FALSE(section.handle);
    stream->Release();
}

TEST(StreamCopy, AStreamOverTheCapFailsBeforeAnythingIsRead) {
    const std::vector<std::uint8_t> bytes(4096, 0x25);
    IStream* stream = memory_stream(bytes);
    Section section;
    EXPECT_EQ(copy_stream_to_section(*stream, 4095, section), HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
    EXPECT_FALSE(section.handle) << "no section may be created for a refused stream";
    EXPECT_EQ(copy_stream_to_section(*stream, 4096, section), S_OK) << "exactly at the cap is fine";
    stream->Release();
}

TEST(StreamCopy, TheCapIs32MiB) { EXPECT_EQ(kMaxStreamBytes, 33554432u); }

TEST(Sections, RefuseNonsenseSizes) {
    Section section;
    EXPECT_EQ(create_section(0, section), E_INVALIDARG);
    EXPECT_EQ(create_section(0x1'0000'0000ull, section), E_INVALIDARG);
    EXPECT_EQ(create_section(4096, section), S_OK);
    EXPECT_EQ(section.bytes, 4096u);
}

TEST(Bitmap, IsTopDownBgraWithAnOpaqueAlphaByte) {
    // 2 x 2: the engine's bytes may carry any alpha; the bitmap must not trust it.
    const std::uint8_t pixels[16] = {0x10, 0x20, 0x30, 0x00, 0x40, 0x50, 0x60, 0x7F, 0x70, 0x80, 0x90, 0xFF, 0xA0, 0xB0, 0xC0, 0x01};
    HBITMAP bitmap = nullptr;
    ASSERT_EQ(make_bitmap(pixels, 2, 2, bitmap), S_OK);
    BITMAP info{};
    ASSERT_GT(GetObjectW(bitmap, sizeof(info), &info), 0);
    EXPECT_EQ(info.bmWidth, 2);
    EXPECT_EQ(info.bmHeight, 2);
    EXPECT_EQ(info.bmBitsPixel, 32);
    std::uint32_t got[4]{};
    ASSERT_EQ(GetBitmapBits(bitmap, sizeof(got), got), static_cast<LONG>(sizeof(got)));
    EXPECT_EQ(got[0], 0xFF302010u);
    EXPECT_EQ(got[1], 0xFF605040u);
    EXPECT_EQ(got[2], 0xFF908070u);
    EXPECT_EQ(got[3], 0xFFC0B0A0u);
    DeleteObject(bitmap);
}

TEST(Bitmap, RefusesSizesOutsideTheLimits) {
    const std::uint8_t pixel[4] = {};
    HBITMAP bitmap = nullptr;
    EXPECT_EQ(make_bitmap(pixel, 0, 1, bitmap), E_INVALIDARG);
    EXPECT_EQ(make_bitmap(pixel, 1, 0, bitmap), E_INVALIDARG);
    EXPECT_EQ(make_bitmap(pixel, 1025, 1, bitmap), E_INVALIDARG);
    EXPECT_EQ(make_bitmap(nullptr, 1, 1, bitmap), E_INVALIDARG);
    EXPECT_EQ(bitmap, nullptr);
}

}  // namespace
