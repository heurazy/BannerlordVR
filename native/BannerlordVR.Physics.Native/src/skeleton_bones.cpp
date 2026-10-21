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

// The skinning palette. After writing a bone's record the engine calls
// 0x361010(skeleton, bone): it turns the record's drawn frame (quaternion +
// position) into a matrix, multiplies it by the record's rest matrix at +0x60
// and stores the result, the matrix the GPU skins the bone with, in a global
// table of 64-byte entries: [module+0xd96d18] + 0xc28, buffer
// [+0x250] x 0x128, pages of 0x2000 entries, at the skeleton's base index for
// this frame (+0x44, allocated by 0x35cac0 on the frame's first write, tagged
// with the frame number at +0x4c from [[module+0xd96cf0] + 0x28c]).
namespace
{
    using RefreshBone = void(__fastcall*)(void* skeleton, int8_t bone);
    const uint8_t RefreshCode[] = { 0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x18, 0x48, 0x89, 0x78, 0x20, 0x55, 0x48, 0x8d, 0x68, 0xc8 };
    const uint8_t PaletteIndexCode[] = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x05, 0x1f, 0xa2, 0xa3 };
    constexpr uintptr_t RefreshRva = 0x361010, PaletteIndexRva = 0x35cac0, PaletteGlobalRva = 0xd96d18, FrameGlobalRva = 0xd96cf0;

    uint8_t* engine_base()
    {
        static uint8_t* base = nullptr;
        static bool checked = false;
        if (checked) return base;
        checked = true;
        auto module = reinterpret_cast<uint8_t*>(GetModuleHandleW(L"TaleWorlds.Native.dll"));
        if (!module || !readable(module + RefreshRva, sizeof RefreshCode) || !readable(module + PaletteIndexRva, sizeof PaletteIndexCode)) return nullptr;
        if (std::memcmp(module + RefreshRva, RefreshCode, sizeof RefreshCode) != 0 ||
            std::memcmp(module + PaletteIndexRva, PaletteIndexCode, sizeof PaletteIndexCode) != 0) return nullptr;
        base = module;
        return base;
    }

    float* palette_entry(uint8_t* module, uint8_t* skeleton, int32_t bone)
    {
        if (!readable(module + FrameGlobalRva, 8) || !readable(module + PaletteGlobalRva, 8)) return nullptr;
        auto frames = *reinterpret_cast<uint8_t**>(module + FrameGlobalRva);
        auto palette = *reinterpret_cast<uint8_t**>(module + PaletteGlobalRva);
        if (!readable(frames + 0x28c, 4) || !palette || !readable(palette + 0xc28 + 0x250, 4)) return nullptr;
        if (!readable(skeleton + 0x44, 12)) return nullptr;
        // Only this frame's slots: written by the refresh just before.
        if (*reinterpret_cast<int32_t*>(skeleton + 0x4c) != *reinterpret_cast<int32_t*>(frames + 0x28c)) return nullptr;
        uint8_t* table = palette + 0xc28;
        const int32_t buffer = *reinterpret_cast<int32_t*>(table + 0x250);
        if (buffer < 0 || buffer > 8) return nullptr;
        uint8_t* pages = table + static_cast<intptr_t>(buffer) * 0x128;
        const uint32_t index = static_cast<uint32_t>(*reinterpret_cast<int32_t*>(skeleton + 0x44) + bone);
        const uint32_t page = index >> 13, slot = index & 0x1fff;
        if (page > 32 || !readable(pages + 8 + page * 8, 8)) return nullptr;
        auto entries = *reinterpret_cast<uint8_t**>(pages + 8 + page * 8);
        float* entry = reinterpret_cast<float*>(entries + static_cast<size_t>(slot) * 64);
        return readable(entry, 64) ? entry : nullptr;
    }
}

// The engine's own follow-up to a record write: the bone's palette matrix.
int32_t BVR_CALL bvr_skeleton_refresh_bone(uintptr_t skeleton_value, int32_t bone)
{
    uint8_t* module = engine_base();
    if (!module) return -23;
    if (!record_of(skeleton_value, bone)) return -21;
    reinterpret_cast<RefreshBone>(module + RefreshRva)(reinterpret_cast<void*>(skeleton_value), static_cast<int8_t>(bone));
    return BVR_OK;
}

// A bone's palette matrix collapsed onto one point (entity space): every
// vertex it skins is drawn there. Refreshes the bone first so this frame's
// slot exists, then overwrites its three axes with zero.
int32_t BVR_CALL bvr_skeleton_collapse_bone(uintptr_t skeleton_value, int32_t bone, float x, float y, float z)
{
    uint8_t* module = engine_base();
    if (!module) return -23;
    if (!record_of(skeleton_value, bone)) return -21;
    auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
    reinterpret_cast<RefreshBone>(module + RefreshRva)(skeleton, static_cast<int8_t>(bone));
    float* entry = palette_entry(module, skeleton, bone);
    if (!entry) return -24;
    for (int k = 0; k < 12; k++) entry[k] = 0;
    entry[12] = x; entry[13] = y; entry[14] = z;
    return BVR_OK;
}
