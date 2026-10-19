#include "bvr_api.h"
#include <windows.h>
#include <cstdint>
#include <cstring>

namespace {
using Release = void(__fastcall*)(void*, void*);
Release release_body = nullptr;
DWORD owner_thread = 0;

bool readable(const void* pointer, size_t bytes)
{
    const uintptr_t start = reinterpret_cast<uintptr_t>(pointer);
    if (!start || start + bytes < start) return false;
    MEMORY_BASIC_INFORMATION info{};
    if (!VirtualQuery(pointer, &info, sizeof(info)) || info.State != MEM_COMMIT ||
        (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const uintptr_t end = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
    return start + bytes <= end;
}
template<class T> T read(const void* object, size_t offset)
{
    T value{};
    std::memcpy(&value, static_cast<const uint8_t*>(object) + offset, sizeof(T));
    return value;
}
}

// Called only after the managed layer verifies the complete loaded module's
// on-disk SHA-256. The prefix additionally refuses a hook at the selected entry.
int32_t BVR_CALL bvr_ragdoll_prepare(void)
{
    release_body = nullptr;
    owner_thread = 0;
    HMODULE module = GetModuleHandleW(L"TaleWorlds.Native.dll");
    if (!module) return BVR_NOT_INITIALIZED;
    auto entry = reinterpret_cast<const uint8_t*>(module) + 0x5d33f0;
    const uint8_t prefix[] = {
        0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x4c,0x8b,0x82,0x10,0x08,0x00,
        0x00,0x48,0x8b,0xda,0x48,0x8b,0xf9,0x4d,0x85,0xc0,0x74,0x5e,0x41,0x0f,0xb7,0x40,
        0x40,0x66,0x83,0xe8,0x02,0x66,0x83,0xf8,0x02,0x77,0x4f,0xf3,0x41,0x0f,0x10,0x48
    };
    if (!readable(entry, sizeof(prefix)) || std::memcmp(entry, prefix, sizeof(prefix)))
        return BVR_GRAPHICS_MISMATCH;
    release_body = reinterpret_cast<Release>(const_cast<uint8_t*>(entry));
    owner_thread = GetCurrentThreadId();
    return BVR_OK;
}

int32_t BVR_CALL bvr_ragdoll_release(uintptr_t mission_value, uintptr_t agent_value, uintptr_t skeleton_value)
{
    const int32_t validation = bvr_ragdoll_validate(mission_value, agent_value, skeleton_value);
    if (validation != BVR_OK) return validation;
    auto mission = reinterpret_cast<void*>(mission_value);
    auto agent = reinterpret_cast<void*>(agent_value);
    void* body = read<void*>(agent, 0x810);
    const uint16_t state = read<uint16_t>(body, 0x40);
    if (state < 2 || state > 4) return -28;
    // The existing engine helper marks NeedsDeactivation and removes this
    // agent from its mission ragdoll list. The engine owns actual body teardown.
    release_body(mission, agent);
    // A callback may complete the transition immediately. Do not dereference
    // the old body after invoking engine code; managed observes the new state.
    return BVR_OK;
}

int32_t BVR_CALL bvr_ragdoll_validate(uintptr_t mission_value, uintptr_t agent_value, uintptr_t skeleton_value)
{
    if (!release_body || GetCurrentThreadId() != owner_thread) return BVR_NOT_INITIALIZED;
    auto mission = reinterpret_cast<void*>(mission_value);
    auto agent = reinterpret_cast<void*>(agent_value);
    auto skeleton = reinterpret_cast<void*>(skeleton_value);
    if (!readable(agent, 0xc60)) return -20;
    if (!readable(skeleton, 0x2a0)) return -21;
    // Validate the actual two mission fields read by the helper. Requiring the
    // entire 18 MB mission to share one VirtualQuery region rejects valid heaps.
    if (!mission_value || mission_value + 0x11849f0 < mission_value ||
        !readable(reinterpret_cast<void*>(mission_value + 0x11849e0), 16)) return -22;
    // The recovered helper is only admitted for the live agent, mission and
    // skeleton whose identities were checked by managed ownership immediately
    // before this call. Never change AgentState or health to reach corpse code.
    if (read<int32_t>(agent, 0x560) != 1) return -23;
    if (read<uintptr_t>(agent, 0xab8) != mission_value) return -24;
    void* body = read<void*>(agent, 0x810);
    if (!body || body != read<void*>(skeleton, 0x298) || !readable(body, 0x180))
        return -25;
    const uintptr_t begin = read<uintptr_t>(mission, 0x11849e0);
    const uintptr_t end = read<uintptr_t>(mission, 0x11849e8);
    if (end < begin || (end - begin) % sizeof(uintptr_t) || (end - begin) / sizeof(uintptr_t) > 16384 ||
        (end != begin && !readable(reinterpret_cast<void*>(begin), end - begin)))
        return -27;
    return BVR_OK;
}
