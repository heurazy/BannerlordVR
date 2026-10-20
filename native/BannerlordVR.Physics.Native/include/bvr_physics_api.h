/* BannerlordVR.Physics.Native - living ragdoll bridge for the physics module.
   Independent of OpenXR and of the BannerlordVR VR layer: it only reads and
   drives TaleWorlds.Native.dll ragdoll state, after the managed side has
   verified the complete loaded module's SHA-256. Main thread only. */
#ifndef BVR_PHYSICS_API_H
#define BVR_PHYSICS_API_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef BVR_PHYSICS_BUILD
#    define BVR_API __declspec(dllexport)
#  else
#    define BVR_API __declspec(dllimport)
#  endif
#  define BVR_CALL __cdecl
#else
#  define BVR_API
#  define BVR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum BvrStatus {
    BVR_OK                 =  0,
    BVR_NOT_INITIALIZED    = -1,
    BVR_GRAPHICS_MISMATCH  = -4,   /* reused: an expected engine code prefix did not match */
    BVR_SESSION_NOT_READY  = -5    /* reused: a required output pointer was null */
} BvrStatus;

BVR_API int32_t BVR_CALL bvr_ragdoll_prepare(void);
BVR_API int32_t BVR_CALL bvr_ragdoll_release(uintptr_t mission, uintptr_t agent, uintptr_t skeleton);
BVR_API int32_t BVR_CALL bvr_ragdoll_validate(uintptr_t mission, uintptr_t agent, uintptr_t skeleton);
/* Read-only developer evidence: module-relative implementations reached by
   ApplyForceOnRagdoll's two virtual calls. Does not call either method. */
BVR_API int32_t BVR_CALL bvr_ragdoll_force_api(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone, uintptr_t* vectorMethodRva, uintptr_t* scalarMethodRva);
BVR_API int32_t BVR_CALL bvr_ragdoll_mass(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone_count, float* total_mass, int32_t* body_count);
BVR_API int32_t BVR_CALL bvr_ragdoll_bone_masses(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone_count, float* bone_mass, float* total_mass, int32_t* body_count);

#ifdef __cplusplus
}
#endif

#endif /* BVR_PHYSICS_API_H */
