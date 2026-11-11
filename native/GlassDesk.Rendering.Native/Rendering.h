#pragma once
#include <stdint.h>

// ABI v1, x64, default Windows packing (8). No STL or exception crosses this API.
// Calls that submit GPU work belong to the thread that created the engine.
// Release and Destroy can run on a finalizer thread. Destroy requires no leases.
#ifdef GD_RENDER_EXPORTS
#define GD_API extern "C" __declspec(dllexport)
#else
#define GD_API extern "C" __declspec(dllimport)
#endif

struct GD_Engine;
struct GD_CreateInfo {
    uint32_t size, version, width, height;
    void* window;
    uint32_t flags, reserved; // flags: 1 = permit diagnostic pixel readback
};
struct GD_Scene {
    uint32_t size, version;
    double seconds;
    float opacity, cornerRadius;
    uint32_t pattern, reserved; // 0 = alpha calibration; 1 = moving rounded panel
};
struct GD_Frame {
    uint32_t size, version;
    void* surface9; // IDirect3DSurface9*, valid until GD_Release(lease).
    uint64_t lease, sequence, generation;
    int64_t submittedQpc;
    uint32_t width, height;
};
struct GD_Stats {
    uint32_t size, version;
    uint64_t generation, submittedFrames, acquiredFrames, droppedFrames;
    uint64_t cpuReadbacks, outputTextureBytes; // excludes driver/WPF allocations
    uint32_t width, height, leasedSlots, pendingSlots, adapterLuidLow;
    int32_t adapterLuidHigh;
};

GD_API int32_t __cdecl GD_Create(const GD_CreateInfo*, GD_Engine**);
GD_API void __cdecl GD_Destroy(GD_Engine*);
GD_API int32_t __cdecl GD_Render(GD_Engine*, const GD_Scene*);
// Additive v1 static wallpaper prefilter. Upload is a bounded one-time CPU copy;
// output stays on GPU. The caller owns pixels only for the duration of Upload.
struct GD_Bitmap {
    uint32_t size, version, width, height, stride, reserved;
    const void* pixels;
};
struct GD_Material {
    uint32_t size, version;
    float dipWidth, dipHeight, strength, clarity, tintOpacity;
    uint32_t kind, dark, reserved1, reserved2, reserved3; // kind 0=liquid, 1=frosted
};
GD_API int32_t __cdecl GD_UploadBitmap(GD_Engine*, const GD_Bitmap*);
GD_API int32_t __cdecl GD_RenderMaterial(GD_Engine*, const GD_Material*);
static_assert(sizeof(GD_Bitmap) == 32);
static_assert(sizeof(GD_Material) == 48);
// S_FALSE: no completed frame; never wait for the GPU on the WPF UI thread.
GD_API int32_t __cdecl GD_Acquire(GD_Engine*, GD_Frame*);
// WPF must detach the surface under D3DImage.Lock before releasing its lease.
GD_API int32_t __cdecl GD_Release(GD_Engine*, uint64_t lease);
// Resize/Recreate return ERROR_BUSY while any surface is leased.
GD_API int32_t __cdecl GD_Resize(GD_Engine*, uint32_t width, uint32_t height);
GD_API int32_t __cdecl GD_Recreate(GD_Engine*);
GD_API int32_t __cdecl GD_GetStats(GD_Engine*, GD_Stats*);
// The only CPU readback API. Rejected unless explicitly enabled at creation.
GD_API int32_t __cdecl GD_ReadPixel(GD_Engine*, uint64_t lease, uint32_t x, uint32_t y, uint32_t* bgra);

static_assert(sizeof(GD_CreateInfo) == 32);
static_assert(sizeof(GD_Scene) == 32);
static_assert(sizeof(GD_Frame) == 56);
static_assert(sizeof(GD_Stats) == 80);
