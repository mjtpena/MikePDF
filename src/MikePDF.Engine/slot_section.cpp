#include "slot_section.h"

#include <Windows.h>

#include "handle_guard.h"

namespace mikepdf::engine {

using protocol::Result;
using protocol::Status;

namespace {
Result<SlotMemory> fail(const char* detail) { return {Status::InvalidArgument, SlotMemory{}, detail}; }
}  // namespace

Result<SlotMemory> map_slot_section(std::uint64_t handle_value, std::uint32_t slot_count, std::uint32_t slot_size) noexcept {
    const HANDLE section = reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(handle_value));
    if (section == nullptr || section == INVALID_HANDLE_VALUE) {
        return fail("no handle");
    }
    // Whatever it is, it was given to us to consume.
    struct Closer {
        HANDLE handle;
        ~Closer() { close_if_handle(handle); }
    } closer{section};

    if (slot_count == 0 || slot_count > kMaxSlotCount || slot_size < kMinSlotSize || slot_size > kMaxSlotSize || slot_size % kMinSlotSize != 0) {
        return fail("impossible slot geometry");
    }
    const std::uint64_t total = std::uint64_t{slot_count} * slot_size;
    if (total > kMaxSectionSize) {
        return fail("section too large");
    }
    void* view = map_view_or_null(section, FILE_MAP_WRITE);
    if (view == nullptr) {
        return fail("not a writable section");
    }
    // The view covers the whole section: it must hold every slot that was promised.
    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(view, &info, sizeof(info)) == 0 || info.RegionSize < total) {
        UnmapViewOfFile(view);
        return fail("section smaller than the slots");
    }
    SlotMemory memory;
    memory.base = static_cast<std::uint8_t*>(view);
    memory.slot_count = slot_count;
    memory.slot_size = slot_size;
    return {Status::Ok, memory, ""};
}

}  // namespace mikepdf::engine
