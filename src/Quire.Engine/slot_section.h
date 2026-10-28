// Maps the slot ring the broker duplicated into this process (ADR-0005 rule 4, AttachSlotRing). Nothing is inherited: the handle
// value arrives in a message, after Hello, and is checked like any other input.
#pragma once

#include <cstdint>

#include "quire/protocol/msgpack.h"
#include "slot_memory.h"

namespace quire::engine {

inline constexpr std::uint32_t kMaxSlotCount = 1024;  // the schema's slotIndex limit is 1023
inline constexpr std::uint32_t kMinSlotSize = 4096;
inline constexpr std::uint32_t kMaxSlotSize = 64u << 20;
inline constexpr std::uint64_t kMaxSectionSize = 1ull << 30;

/// Takes ownership of `handle_value` (it is closed on every path). Maps the section writable and checks that it really holds
/// `slot_count` slots of `slot_size` bytes. InvalidArgument for a handle that is not a section, or a section that is too small
/// or described with impossible numbers. The view is never unmapped: the ring lives as long as the process.
[[nodiscard]] protocol::Result<SlotMemory> map_slot_section(std::uint64_t handle_value, std::uint32_t slot_count, std::uint32_t slot_size) noexcept;

}  // namespace quire::engine
