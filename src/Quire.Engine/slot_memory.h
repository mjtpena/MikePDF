// The engine's view of the slot ring (ADR-0005): `slot_count` slots of `slot_size` bytes, back to back, in a section the
// broker created and duplicated into this process with write access. The broker maps it read-only.
#pragma once

#include <cstddef>
#include <cstdint>
#include <span>

namespace quire::engine {

struct SlotMemory {
    std::uint8_t* base{nullptr};
    std::uint32_t slot_count{0};
    std::size_t slot_size{0};

    /// Empty if `index` is outside the ring.
    [[nodiscard]] std::span<std::uint8_t> slot(std::uint32_t index) const noexcept {
        if (base == nullptr || index >= slot_count) {
            return {};
        }
        return {base + std::size_t{index} * slot_size, slot_size};
    }
};

}  // namespace quire::engine
