// Per-bone records of a TaleWorlds.Native skeleton, for hiding a severed
// limb. A skeleton keeps one 0x100-byte record per bone in an array at +0x18,
// its bone count in the byte at +0x20 (the rest-pose reset at 0x35d3f0 walks
// them, writing each record under the spin lock in its first dword). Which
// record fields the renderer draws from is found in game by the managed
// probe (LimbHide), which matches them against GetBoneEntitialFrameWithIndex.
#include "bvr_physics_api.h"
#include <windows.h>
#include <intrin.h>
#include <cstring>

namespace
{
    bool readable(const void* pointer, size_t bytes)
    {
        if (!pointer) return false;
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(pointer, &info, sizeof info) || info.State != MEM_COMMIT) return false;
        if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
        const auto begin = reinterpret_cast<uintptr_t>(pointer);
        const auto end = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        return begin + bytes <= end;
    }

    constexpr size_t RecordBytes = 0x100;

    uint8_t* record_of(uintptr_t skeleton_value, int32_t bone)
    {
        auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
        if (!readable(skeleton, 0x28)) return nullptr;
        const int32_t count = *reinterpret_cast<int8_t*>(skeleton + 0x20);
        if (bone < 0 || bone >= count || count > 128) return nullptr;
        auto records = *reinterpret_cast<uint8_t**>(skeleton + 0x18);
        if (!records) return nullptr;
        uint8_t* record = records + static_cast<size_t>(bone) * RecordBytes;
        return readable(record, RecordBytes) ? record : nullptr;
    }
}

int32_t BVR_CALL bvr_skeleton_bone_count(uintptr_t skeleton_value)
{
    auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
    if (!readable(skeleton, 0x28)) return -20;
    return *reinterpret_cast<int8_t*>(skeleton + 0x20);
}

int32_t BVR_CALL bvr_skeleton_bone_record(uintptr_t skeleton_value, int32_t bone, uint8_t* out, int32_t bytes)
{
    if (!out || bytes <= 0) return BVR_SESSION_NOT_READY;
    uint8_t* record = record_of(skeleton_value, bone);
    if (!record) return -21;
    const size_t n = static_cast<size_t>(bytes) < RecordBytes ? static_cast<size_t>(bytes) : RecordBytes;
    std::memcpy(out, record, n);
    return static_cast<int32_t>(n);
}

// Writes count floats at offset (4-byte aligned, past the lock dword) under the
// record's lock, as the engine itself writes it.
int32_t BVR_CALL bvr_skeleton_write_record(uintptr_t skeleton_value, int32_t bone, int32_t offset,
    const float* values, int32_t count)
{
    if (!values || count <= 0 || offset < 4 || (offset & 3) != 0 ||
        static_cast<size_t>(offset) + static_cast<size_t>(count) * 4 > RecordBytes) return BVR_SESSION_NOT_READY;
    uint8_t* record = record_of(skeleton_value, bone);
    if (!record) return -21;
    auto lock = reinterpret_cast<volatile long*>(record);
    int spins = 0;
    while (_interlockedbittestandset(lock, 0))
    {
        _mm_pause();
        if (++spins > 1000000) return -22;
    }
    std::memcpy(record + offset, values, static_cast<size_t>(count) * 4);
    _InterlockedExchange(lock, 0);
    return BVR_OK;
}
