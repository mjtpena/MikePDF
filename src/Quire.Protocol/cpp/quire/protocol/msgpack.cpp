#include "quire/protocol/msgpack.h"

#include <bit>
#include <cmath>
#include <limits>

namespace quire::protocol {

namespace {

constexpr std::uint64_t be(const std::uint8_t* p, int n) noexcept {
    std::uint64_t v = 0;
    for (int i = 0; i < n; ++i) {
        v = (v << 8) | p[i];
    }
    return v;
}

}  // namespace

bool is_valid_utf8(std::span<const std::uint8_t> s) noexcept {
    // Well-formed byte sequences, Unicode 3.9 table 3-7.
    std::size_t i = 0;
    const std::size_t n = s.size();
    while (i < n) {
        const std::uint8_t b = s[i];
        if (b < 0x80) {
            ++i;
            continue;
        }
        std::size_t len = 0;
        std::uint8_t lo = 0x80;
        std::uint8_t hi = 0xBF;
        if (b >= 0xC2 && b <= 0xDF) {
            len = 2;
        } else if (b == 0xE0) {
            len = 3;
            lo = 0xA0;
        } else if (b == 0xED) {
            len = 3;
            hi = 0x9F;
        } else if (b >= 0xE1 && b <= 0xEF) {
            len = 3;
        } else if (b == 0xF0) {
            len = 4;
            lo = 0x90;
        } else if (b == 0xF4) {
            len = 4;
            hi = 0x8F;
        } else if (b >= 0xF1 && b <= 0xF3) {
            len = 4;
        } else {
            return false;
        }
        if (n - i < len) {
            return false;
        }
        if (s[i + 1] < lo || s[i + 1] > hi) {
            return false;
        }
        for (std::size_t k = 2; k < len; ++k) {
            if (s[i + k] < 0x80 || s[i + k] > 0xBF) {
                return false;
            }
        }
        i += len;
    }
    return true;
}

bool is_valid_text(std::string_view text) noexcept {
    return text.find('\0') == std::string_view::npos &&
           is_valid_utf8({reinterpret_cast<const std::uint8_t*>(text.data()), text.size()});
}

// ---- Reader ----------------------------------------------------------------------------------

Status Reader::fail(Status status, const char* why) noexcept {
    if (*detail_ == '\0') {
        detail_ = why;
    }
    return status;
}

Status Reader::take(std::size_t n, const std::uint8_t*& at) noexcept {
    if (n > remaining()) {
        return fail(Status::ProtocolError, "truncated message");
    }
    at = data_.data() + pos_;
    pos_ += n;
    return Status::Ok;
}

Status Reader::expect_array(std::size_t exact) noexcept {
    std::size_t count = 0;
    Status s = read_array_header(count, exact);
    if (s == Status::Ok && count != exact) {
        return fail(Status::ProtocolError, "wrong number of array elements");
    }
    if (s == Status::InvalidArgument) {
        return fail(Status::ProtocolError, "wrong number of array elements");
    }
    return s;
}

Status Reader::read_array_header(std::size_t& count, std::size_t max_count) noexcept {
    const std::uint8_t* p = nullptr;
    Status s = take(1, p);
    if (s != Status::Ok) {
        return s;
    }
    std::uint64_t n = 0;
    const std::uint8_t b = *p;
    if (b >= 0x90 && b <= 0x9F) {
        n = b & 0x0F;
    } else if (b == 0xDC || b == 0xDD) {
        s = take(b == 0xDC ? 2 : 4, p);
        if (s != Status::Ok) {
            return s;
        }
        n = be(p, b == 0xDC ? 2 : 4);
    } else {
        return fail(Status::ProtocolError, "expected an array");
    }
    // Every element takes at least one byte, so a count beyond the bytes left cannot be genuine.
    if (n > remaining()) {
        return fail(Status::ProtocolError, "array count exceeds the message");
    }
    if (n > max_count) {
        return fail(Status::InvalidArgument, "array has more elements than its limit");
    }
    count = static_cast<std::size_t>(n);
    return Status::Ok;
}

bool Reader::next_is_nil() const noexcept {
    return remaining() > 0 && data_[pos_] == 0xC0;
}

Status Reader::read_nil() noexcept {
    if (!next_is_nil()) {
        return fail(Status::ProtocolError, "expected nil");
    }
    ++pos_;
    return Status::Ok;
}

Status Reader::read_bool(bool& out) noexcept {
    const std::uint8_t* p = nullptr;
    Status s = take(1, p);
    if (s != Status::Ok) {
        return s;
    }
    if (*p != 0xC2 && *p != 0xC3) {
        return fail(Status::ProtocolError, "expected a boolean");
    }
    out = *p == 0xC3;
    return Status::Ok;
}

Status Reader::read_integer(bool& negative, std::uint64_t& magnitude) noexcept {
    const std::uint8_t* p = nullptr;
    Status s = take(1, p);
    if (s != Status::Ok) {
        return s;
    }
    const std::uint8_t b = *p;
    negative = false;
    if (b <= 0x7F) {
        magnitude = b;
        return Status::Ok;
    }
    if (b >= 0xE0) {
        negative = true;
        magnitude = static_cast<std::uint64_t>(0x100 - b);
        return Status::Ok;
    }
    std::uint64_t raw = 0;
    int width = 0;
    bool is_signed = false;
    switch (b) {
        case 0xCC: width = 1; break;
        case 0xCD: width = 2; break;
        case 0xCE: width = 4; break;
        case 0xCF: width = 8; break;
        case 0xD0: width = 1; is_signed = true; break;
        case 0xD1: width = 2; is_signed = true; break;
        case 0xD2: width = 4; is_signed = true; break;
        case 0xD3: width = 8; is_signed = true; break;
        default: return fail(Status::ProtocolError, "expected an integer");
    }
    s = take(static_cast<std::size_t>(width), p);
    if (s != Status::Ok) {
        return s;
    }
    raw = be(p, width);
    if (!is_signed) {
        magnitude = raw;
        return Status::Ok;
    }
    // Sign-extend to 64 bits, then take the magnitude with two's complement (safe for the most negative value).
    const int shift = 64 - 8 * width;
    const std::uint64_t extended = static_cast<std::uint64_t>(static_cast<std::int64_t>(raw << shift) >> shift);
    if ((extended >> 63) != 0) {
        negative = true;
        magnitude = ~extended + 1;
    } else {
        magnitude = extended;
    }
    return Status::Ok;
}

Status Reader::read_unsigned(std::uint64_t type_max, std::uint64_t min, std::uint64_t max, std::uint64_t& out) noexcept {
    bool negative = false;
    std::uint64_t magnitude = 0;
    Status s = read_integer(negative, magnitude);
    if (s != Status::Ok) {
        return s;
    }
    if (negative || magnitude > type_max) {
        return fail(Status::ProtocolError, "integer does not fit the field type");
    }
    if (magnitude < min || magnitude > max) {
        return fail(Status::InvalidArgument, "integer is outside its range");
    }
    out = magnitude;
    return Status::Ok;
}

Status Reader::read_u16(std::uint16_t& out, std::uint16_t min, std::uint16_t max) noexcept {
    std::uint64_t v = 0;
    Status s = read_unsigned(std::numeric_limits<std::uint16_t>::max(), min, max, v);
    if (s == Status::Ok) {
        out = static_cast<std::uint16_t>(v);
    }
    return s;
}

Status Reader::read_u32(std::uint32_t& out, std::uint32_t min, std::uint32_t max) noexcept {
    std::uint64_t v = 0;
    Status s = read_unsigned(std::numeric_limits<std::uint32_t>::max(), min, max, v);
    if (s == Status::Ok) {
        out = static_cast<std::uint32_t>(v);
    }
    return s;
}

Status Reader::read_u64(std::uint64_t& out, std::uint64_t min, std::uint64_t max) noexcept {
    return read_unsigned(std::numeric_limits<std::uint64_t>::max(), min, max, out);
}

Status Reader::read_i32(std::int32_t& out, std::int32_t min, std::int32_t max) noexcept {
    bool negative = false;
    std::uint64_t magnitude = 0;
    Status s = read_integer(negative, magnitude);
    if (s != Status::Ok) {
        return s;
    }
    constexpr std::uint64_t kMaxPositive = static_cast<std::uint64_t>(std::numeric_limits<std::int32_t>::max());
    if (magnitude > (negative ? kMaxPositive + 1 : kMaxPositive)) {
        return fail(Status::ProtocolError, "integer does not fit the field type");
    }
    const std::int64_t v = negative ? -static_cast<std::int64_t>(magnitude) : static_cast<std::int64_t>(magnitude);
    if (v < min || v > max) {
        return fail(Status::InvalidArgument, "integer is outside its range");
    }
    out = static_cast<std::int32_t>(v);
    return Status::Ok;
}

Status Reader::read_f32(float& out, float min, float max, bool min_exclusive) noexcept {
    const std::uint8_t* p = nullptr;
    Status s = take(1, p);
    if (s != Status::Ok) {
        return s;
    }
    if (*p != 0xCA) {
        return fail(Status::ProtocolError, "expected a float32");
    }
    s = take(4, p);
    if (s != Status::Ok) {
        return s;
    }
    const float v = std::bit_cast<float>(static_cast<std::uint32_t>(be(p, 4)));
    if (!std::isfinite(v)) {
        return fail(Status::InvalidArgument, "float is not finite");
    }
    if (v > max || (min_exclusive ? v <= min : v < min)) {
        return fail(Status::InvalidArgument, "float is outside its range");
    }
    out = v;
    return Status::Ok;
}

Status Reader::read_string_view(std::size_t max_bytes, std::string_view& out) noexcept {
    const std::uint8_t* p = nullptr;
    Status s = take(1, p);
    if (s != Status::Ok) {
        return s;
    }
    const std::uint8_t b = *p;
    std::uint64_t len = 0;
    if (b >= 0xA0 && b <= 0xBF) {
        len = b & 0x1F;
    } else if (b == 0xD9 || b == 0xDA || b == 0xDB) {
        const int width = b == 0xD9 ? 1 : b == 0xDA ? 2 : 4;
        s = take(static_cast<std::size_t>(width), p);
        if (s != Status::Ok) {
            return s;
        }
        len = be(p, width);
    } else {
        return fail(Status::ProtocolError, "expected a string");
    }
    if (len > remaining()) {
        return fail(Status::ProtocolError, "string length exceeds the message");
    }
    if (len > max_bytes) {
        return fail(Status::InvalidArgument, "string is longer than its limit");
    }
    s = take(static_cast<std::size_t>(len), p);
    if (s != Status::Ok) {
        return s;
    }
    if (!is_valid_utf8({p, static_cast<std::size_t>(len)})) {
        return fail(Status::ProtocolError, "string is not valid UTF-8");
    }
    out = std::string_view(reinterpret_cast<const char*>(p), static_cast<std::size_t>(len));
    if (out.find('\0') != std::string_view::npos) {
        return fail(Status::InvalidArgument, "string contains NUL");
    }
    return Status::Ok;
}

Status Reader::read_string(std::string& out, std::size_t max_bytes) {
    std::string_view view;
    Status s = read_string_view(max_bytes, view);
    if (s == Status::Ok) {
        out.assign(view);
    }
    return s;
}

Status Reader::read_optional_string(std::optional<std::string>& out, std::size_t max_bytes) {
    if (next_is_nil()) {
        ++pos_;
        out.reset();
        return Status::Ok;
    }
    std::string_view view;
    Status s = read_string_view(max_bytes, view);
    if (s == Status::Ok) {
        out.emplace(view);
    }
    return s;
}

// ---- Writer ----------------------------------------------------------------------------------

bool Writer::room(std::size_t n) noexcept {
    // buffer_.size() <= max_bytes_ always holds, so the subtraction cannot wrap.
    if (overflow_ || n > max_bytes_ - buffer_.size()) {
        overflow_ = true;
        return false;
    }
    return true;
}

void Writer::put(std::uint8_t b) {
    if (room(1)) {
        buffer_.push_back(b);
    }
}

void Writer::put_be(std::uint64_t v, int bytes) {
    if (!room(static_cast<std::size_t>(bytes))) {
        return;
    }
    for (int i = bytes - 1; i >= 0; --i) {
        buffer_.push_back(static_cast<std::uint8_t>(v >> (8 * i)));
    }
}

void Writer::put_bytes(const char* p, std::size_t n) {
    if (!room(n)) {
        return;
    }
    buffer_.insert(buffer_.end(), reinterpret_cast<const std::uint8_t*>(p), reinterpret_cast<const std::uint8_t*>(p) + n);
}

void Writer::write_nil() {
    put(0xC0);
}

void Writer::write_bool(bool v) {
    put(v ? 0xC3 : 0xC2);
}

void Writer::write_uint(std::uint64_t v) {
    if (v <= 0x7F) {
        put(static_cast<std::uint8_t>(v));
    } else if (v <= 0xFF) {
        put(0xCC);
        put_be(v, 1);
    } else if (v <= 0xFFFF) {
        put(0xCD);
        put_be(v, 2);
    } else if (v <= 0xFFFFFFFFull) {
        put(0xCE);
        put_be(v, 4);
    } else {
        put(0xCF);
        put_be(v, 8);
    }
}

void Writer::write_int(std::int64_t v) {
    if (v >= 0) {
        write_uint(static_cast<std::uint64_t>(v));
    } else if (v >= -32) {
        put(static_cast<std::uint8_t>(v));
    } else if (v >= -128) {
        put(0xD0);
        put_be(static_cast<std::uint64_t>(v), 1);
    } else if (v >= -32768) {
        put(0xD1);
        put_be(static_cast<std::uint64_t>(v), 2);
    } else if (v >= std::numeric_limits<std::int32_t>::min()) {
        put(0xD2);
        put_be(static_cast<std::uint64_t>(v), 4);
    } else {
        put(0xD3);
        put_be(static_cast<std::uint64_t>(v), 8);
    }
}

void Writer::write_f32(float v) {
    put(0xCA);
    put_be(std::bit_cast<std::uint32_t>(v), 4);
}

void Writer::write_string(std::string_view text) {
    const std::size_t n = text.size();
    if (n <= 31) {
        put(static_cast<std::uint8_t>(0xA0 | n));
    } else if (n <= 0xFF) {
        put(0xD9);
        put_be(n, 1);
    } else if (n <= 0xFFFF) {
        put(0xDA);
        put_be(n, 2);
    } else if (n <= 0xFFFFFFFFull) {
        put(0xDB);
        put_be(n, 4);
    } else {
        overflow_ = true;
        return;
    }
    put_bytes(text.data(), n);
}

void Writer::write_array_header(std::size_t count) {
    if (count <= 15) {
        put(static_cast<std::uint8_t>(0x90 | count));
    } else if (count <= 0xFFFF) {
        put(0xDC);
        put_be(count, 2);
    } else if (count <= 0xFFFFFFFFull) {
        put(0xDD);
        put_be(count, 4);
    } else {
        overflow_ = true;
    }
}

}  // namespace quire::protocol
