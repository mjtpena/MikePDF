// MikePDF-owned MessagePack core (ADR-0005). Only the subset the protocol uses, and nothing that can recurse:
//   - Reader: bounds-checked cursor over a span. Every read checks the bytes remaining first; every count and length is
//     checked against the bytes remaining and then against the caller's schema limit before anything is allocated.
//   - Writer: appends into a growable buffer that refuses to grow past a fixed ceiling.
// Unsupported on purpose (rejected as a ProtocolError): bin, ext, float64, map, nil where a value is required, and any
// string that is not strict UTF-8. Integers are read in any MessagePack width, as long as the value fits the target type;
// the writer always emits the shortest form.
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "mikepdf/protocol/enums.h"

namespace mikepdf::protocol {

/// Outcome of a decode or encode step. `value` is only meaningful when ok(); on failure it is default-constructed.
/// `detail` points at a string literal and is for diagnostics only.
template <class T>
struct [[nodiscard]] Result {
    Status status{Status::ProtocolError};
    T value{};
    const char* detail{""};

    [[nodiscard]] bool ok() const noexcept { return status == Status::Ok; }
    explicit operator bool() const noexcept { return ok(); }
};

/// Strict UTF-8 (no overlong forms, no surrogates, nothing above U+10FFFF).
[[nodiscard]] bool is_valid_utf8(std::span<const std::uint8_t> bytes) noexcept;

/// Strict UTF-8 and no NUL character: the rule for every string field.
[[nodiscard]] bool is_valid_text(std::string_view text) noexcept;

/// Failures are reported with two statuses only:
///   ProtocolError   - the bytes are not a valid encoding of the expected type (wrong type, truncated, a value that does
///                     not fit the wire type, invalid UTF-8).
///   InvalidArgument - well formed, but outside a schema limit (range, length, count, NUL in text, undefined enum value,
///                     non-finite float).
/// After the first failure the reader's position is unspecified and the caller must stop.
class Reader {
public:
    explicit Reader(std::span<const std::uint8_t> data) noexcept : data_(data) {}

    [[nodiscard]] std::size_t remaining() const noexcept { return data_.size() - pos_; }
    [[nodiscard]] bool at_end() const noexcept { return pos_ == data_.size(); }

    /// Why the first failure happened (a string literal). Empty if none.
    [[nodiscard]] const char* detail() const noexcept { return detail_; }

    /// Records a failure (the first one wins) and returns its status.
    Status fail(Status status, const char* why) noexcept;

    /// An array header whose count must equal `exact`.
    [[nodiscard]] Status expect_array(std::size_t exact) noexcept;

    /// An array header. The count is checked against the bytes remaining (each element takes at least one byte) and then
    /// against `max_count`, before the caller allocates anything.
    [[nodiscard]] Status read_array_header(std::size_t& count, std::size_t max_count) noexcept;

    [[nodiscard]] bool next_is_nil() const noexcept;
    [[nodiscard]] Status read_nil() noexcept;
    [[nodiscard]] Status read_bool(bool& out) noexcept;

    [[nodiscard]] Status read_u16(std::uint16_t& out, std::uint16_t min, std::uint16_t max) noexcept;
    [[nodiscard]] Status read_u32(std::uint32_t& out, std::uint32_t min, std::uint32_t max) noexcept;
    [[nodiscard]] Status read_u64(std::uint64_t& out, std::uint64_t min, std::uint64_t max) noexcept;
    [[nodiscard]] Status read_i32(std::int32_t& out, std::int32_t min, std::int32_t max) noexcept;

    /// float32 only. Rejects NaN and infinities, then applies min <= v <= max (or min < v when `min_exclusive`).
    [[nodiscard]] Status read_f32(float& out, float min, float max, bool min_exclusive) noexcept;

    /// A string of at most `max_bytes` UTF-8 bytes with no NUL.
    [[nodiscard]] Status read_string(std::string& out, std::size_t max_bytes);
    /// As read_string, but nil is accepted and leaves `out` empty.
    [[nodiscard]] Status read_optional_string(std::optional<std::string>& out, std::size_t max_bytes);

    /// An enum stored as an unsigned integer; `is_defined` is the generated is_defined_xxx(uint16_t).
    template <class Enum, class IsDefined>
    [[nodiscard]] Status read_enum(Enum& out, IsDefined is_defined) noexcept {
        std::uint16_t raw = 0;
        Status s = read_u16(raw, 0, 0xFFFF);
        if (s != Status::Ok) {
            return s;
        }
        if (!is_defined(raw)) {
            return fail(Status::InvalidArgument, "undefined enum value");
        }
        out = static_cast<Enum>(raw);
        return Status::Ok;
    }

private:
    [[nodiscard]] Status take(std::size_t n, const std::uint8_t*& at) noexcept;
    [[nodiscard]] Status read_integer(bool& negative, std::uint64_t& magnitude) noexcept;
    [[nodiscard]] Status read_unsigned(std::uint64_t type_max, std::uint64_t min, std::uint64_t max, std::uint64_t& out) noexcept;
    [[nodiscard]] Status read_string_view(std::size_t max_bytes, std::string_view& out) noexcept;

    std::span<const std::uint8_t> data_;
    std::size_t pos_{0};
    const char* detail_{""};
};

/// Appends MessagePack to a buffer that never grows past `max_bytes`. Writing past the ceiling sets a sticky error and the
/// rest of the writes are ignored, so a caller checks status() once at the end. After an overflow the buffer holds an
/// unspecified prefix of what was written and must be discarded.
class Writer {
public:
    explicit Writer(std::size_t max_bytes = kMaxFrameBytes) : max_bytes_(max_bytes) {}

    void write_nil();
    void write_bool(bool v);
    void write_uint(std::uint64_t v);  // shortest unsigned form
    void write_int(std::int64_t v);    // shortest form; non-negative values use the unsigned forms
    void write_f32(float v);
    void write_string(std::string_view text);
    void write_array_header(std::size_t count);

    /// Ok, or InvalidArgument if the ceiling was hit.
    [[nodiscard]] Status status() const noexcept { return overflow_ ? Status::InvalidArgument : Status::Ok; }
    [[nodiscard]] std::span<const std::uint8_t> bytes() const noexcept { return buffer_; }
    [[nodiscard]] std::vector<std::uint8_t> take() noexcept { return std::move(buffer_); }

private:
    void put(std::uint8_t b);
    void put_be(std::uint64_t v, int bytes);
    void put_bytes(const char* p, std::size_t n);
    [[nodiscard]] bool room(std::size_t n) noexcept;

    std::vector<std::uint8_t> buffer_;
    std::size_t max_bytes_;
    bool overflow_{false};
};

}  // namespace mikepdf::protocol
