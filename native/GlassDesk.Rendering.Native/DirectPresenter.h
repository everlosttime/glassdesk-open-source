#pragma once
#include "Rendering.h"

// Independent opt-in presentation experiment, ABI v1. No D3D9 or WPF surface.
// Creation/presentation/resize/statistics run on the creating thread.
// A borrowed wait handle remains valid until Destroy; callers never CloseHandle it.
struct GD_Presenter;
struct GD_PresenterStats {
    uint32_t size, version;
    uint64_t submitted, wouldBlock;
    uint32_t width, height, bufferCount, d3d9ResourceCount;
    uint64_t bufferBytes;
};
static_assert(sizeof(GD_PresenterStats) == 48);
GD_API int32_t __cdecl GD_PresenterCreate(const GD_CreateInfo*, GD_Presenter**);
// Explicit same-adapter sharing; no global cache keeps a device alive.
// Call on the parent's creation thread. A child outlives its parent safely.
GD_API int32_t __cdecl GD_PresenterCreateSibling(GD_Presenter*, const GD_CreateInfo*, GD_Presenter**);
struct GD_DeviceStats {
    uint32_t size, version;
    uint64_t identity;
    uint32_t adapterLuidLow;
    int32_t adapterLuidHigh;
    uint32_t ownerThread, presenters, liveDevices, reserved;
};
static_assert(sizeof(GD_DeviceStats) == 40);
GD_API int32_t __cdecl GD_PresenterGetDeviceStatistics(GD_Presenter*, GD_DeviceStats*);
GD_API void __cdecl GD_PresenterDestroy(GD_Presenter*);
GD_API int32_t __cdecl GD_PresenterClear(GD_Presenter*, float r, float g, float b, float a);
// S_FALSE means bounded swapchain backpressure/occlusion, not a completed display frame.
GD_API int32_t __cdecl GD_PresenterPresent(GD_Presenter*);
GD_API int32_t __cdecl GD_PresenterResize(GD_Presenter*, uint32_t width, uint32_t height);
GD_API int32_t __cdecl GD_PresenterGetStatistics(GD_Presenter*, GD_PresenterStats*);
GD_API void* __cdecl GD_PresenterGetWaitHandle(GD_Presenter*);
// Explicit diagnostic opt-in (Create flags=1); never used by the normal presenter.
GD_API int32_t __cdecl GD_PresenterReadPixel(GD_Presenter*, uint32_t x, uint32_t y, uint32_t* bgra);
GD_API int32_t __cdecl GD_PresenterUploadBitmap(GD_Presenter*, const GD_Bitmap*);
GD_API int32_t __cdecl GD_PresenterRenderMaterial(GD_Presenter*, const GD_Material*);
struct GD_GlassScene {
    uint32_t size,version;
    float hostWidth,hostHeight,panelX,panelY,panelWidth,panelHeight;
    float backdropX,backdropY,backdropWidth,backdropHeight;
    float radius,strength,dispersion,innerGain,outerGain;
    // v1 requires outline=0. v2: 0=regular polyline, 1=production Dock compact cubic.
    uint32_t launchpad,outline,reserved2;
};
static_assert(sizeof(GD_GlassScene)==80);
// E_NOTIMPL leaves the candidate frame unchanged: frosted chrome and effective
// radii below 16 DIP require the legacy path until complete optical parity.
GD_API int32_t __cdecl GD_PresenterRenderGlass(GD_Presenter*, const GD_Material*, const GD_GlassScene*);
GD_API int32_t __cdecl GD_PresenterUploadPreparedBackdrop(GD_Presenter*, uint32_t slot, const GD_Bitmap*);
GD_API int32_t __cdecl GD_PresenterRenderPreparedGlass(GD_Presenter*, uint32_t slot, const GD_GlassScene*, uint32_t frosted);
GD_API int32_t __cdecl GD_PresenterCompositePreparedGlass(GD_Presenter*, uint32_t slot, const GD_GlassScene*, uint32_t frosted);
GD_API int32_t __cdecl GD_PresenterReadPixels(GD_Presenter*, void* pixels, uint64_t capacity, uint32_t stride);
// Counters describe the current uploaded-material generation (reset on Resize).
struct GD_DirectMaterialStats {
    uint32_t size,version;
    uint64_t filterRuns,sourceBytes,scratchBytes,glassDraws;
};
static_assert(sizeof(GD_DirectMaterialStats)==40);
GD_API int32_t __cdecl GD_PresenterGetMaterialStatistics(GD_Presenter*,GD_DirectMaterialStats*);

// Foreground assets are premultiplied BGRA; keys are nonzero and local to this
// presenter. Upload copies the caller's bytes once, and replacement is atomic.
struct GD_ImageDraw {
    uint32_t key,reserved;
    float x,y,width,height,opacity;
    uint32_t reserved2;
};
struct GD_ImageBatch {
    uint32_t size,version,count,reserved;
    float hostWidth,hostHeight;
    const GD_ImageDraw* items;
};
struct GD_ImageStats {
    uint32_t size,version,imageCount,reserved;
    uint64_t sourceBytes,cacheBytes,prefilterRuns;
};
static_assert(sizeof(GD_ImageDraw)==32);
static_assert(sizeof(GD_ImageBatch)==32);
static_assert(sizeof(GD_ImageStats)==40);
GD_API int32_t __cdecl GD_PresenterSetImage(GD_Presenter*,uint32_t key,const GD_Bitmap*);
GD_API int32_t __cdecl GD_PresenterRemoveImage(GD_Presenter*,uint32_t key);
GD_API int32_t __cdecl GD_PresenterDrawImages(GD_Presenter*,const GD_ImageBatch*);
GD_API int32_t __cdecl GD_PresenterGetImageStatistics(GD_Presenter*,GD_ImageStats*);

// Coverage can be guideline-snapped while bitmap sampling retains its original
// world transform. Keep the original image/vector ABI unchanged.
struct GD_MappedImageDraw { GD_ImageDraw geometry; float mapX,mapY,mapWidth,mapHeight; };
struct GD_MappedImageBatch {
    uint32_t size,version,count,reserved;
    float hostWidth,hostHeight;
    const GD_MappedImageDraw* items;
};
static_assert(sizeof(GD_MappedImageDraw)==48);
static_assert(sizeof(GD_MappedImageBatch)==32);
GD_API int32_t __cdecl GD_PresenterDrawMappedImages(GD_Presenter*,const GD_MappedImageBatch*);

// Dedicated transparent shadow output; caller retains its independent HWND,
// input transparency and desktop Z order. Layers retain production draw order.
struct GD_ShadowLayer {
    float extent,offset,opacity,red,green,blue,alpha;
    uint32_t reserved;
};
struct GD_ShadowScene {
    uint32_t size,version;
    float hostWidth,hostHeight,panelX,panelY,panelWidth,panelHeight,radius,opacity;
    uint32_t outline,steps,count,reserved;
    const GD_ShadowLayer* layers;
};
struct GD_ShadowStats {
    uint32_t size,version,contourCount,reserved;
    uint64_t geometryBuilds,draws,targetAllocations,targetBytes;
};
static_assert(sizeof(GD_ShadowLayer)==32);
static_assert(sizeof(GD_ShadowScene)==64);
static_assert(sizeof(GD_ShadowStats)==48);
GD_API int32_t __cdecl GD_PresenterRenderShadow(GD_Presenter*,const GD_ShadowScene*);
GD_API int32_t __cdecl GD_PresenterGetShadowStatistics(GD_Presenter*,GD_ShadowStats*);

// Bounded cached vector geometry. Move flags bit 0 marks a filled figure;
// operation 0=Move, 1=Line, 2=Cubic, 3=Close. Other flags must be zero.
// Upload a filled device-space widened contour for strokes. Nonzero raw
// stroke paint returns E_NOTIMPL; Direct2D's different AA is never substituted.
struct GD_VectorSegment { uint32_t operation,flags; float x1,y1,x2,y2,x3,y3; };
struct GD_VectorInfo {
    uint32_t size,version;
    float width,height,fillR,fillG,fillB,fillA,strokeR,strokeG,strokeB,strokeA,strokeWidth;
    uint32_t count,fillRule,reserved; // fillRule 0=even-odd, 1=nonzero
    const GD_VectorSegment* segments;
};
struct GD_VectorStats {
    uint32_t size,version,vectorCount,reserved;
    uint64_t segmentBytes,geometryBuilds,draws,coverageBuilds,cacheBytes;
};
// cacheBytes covers vector interval CPU capacity + GPU buffers only. It excludes
// shader/pipeline, driver and managed allocations; never process-memory proof.
static_assert(sizeof(GD_VectorSegment)==32);
static_assert(sizeof(GD_VectorInfo)==72);
static_assert(sizeof(GD_VectorStats)==56);
GD_API int32_t __cdecl GD_PresenterSetVector(GD_Presenter*,uint32_t key,const GD_VectorInfo*);
GD_API int32_t __cdecl GD_PresenterRemoveVector(GD_Presenter*,uint32_t key);
// Same bounded draw layout as images; keys use a separate vector namespace.
GD_API int32_t __cdecl GD_PresenterDrawVectors(GD_Presenter*,const GD_ImageBatch*);
GD_API int32_t __cdecl GD_PresenterGetVectorStatistics(GD_Presenter*,GD_VectorStats*);

struct GD_GradientStop {float offset,red,green,blue,alpha;uint32_t reserved;};
struct GD_VectorBrushInfo {
    uint32_t size,version,kind,count; // 1=relative linear, 2=centered relative radial; pad, sRGB interpolation
    float x1,y1,x2,y2; // linear start/end, or radial center/radii
    const GD_GradientStop* stops;
};
static_assert(sizeof(GD_GradientStop)==24);
static_assert(sizeof(GD_VectorBrushInfo)==40);
GD_API int32_t __cdecl GD_PresenterSetVectorBrush(GD_Presenter*,uint32_t key,const GD_VectorBrushInfo*);

struct GD_GlyphOffset {float x,y;};
struct GD_GlyphRunInfo {
    uint32_t size,version,count,faceIndex,simulations,bidiLevel,sideways,reserved;
    float emSize;uint32_t measuringMode;
    const wchar_t* fontPath;const uint16_t* indices;const float* advances;const GD_GlyphOffset* offsets;
};
struct GD_GlyphDraw {
    uint32_t key,reserved;
    float x,y,scaleX,scaleY,opacity,red,green,blue,alpha;
    uint32_t reserved2;
};
struct GD_GlyphBatch {uint32_t size,version,count,reserved;float hostWidth,hostHeight;const GD_GlyphDraw* items;};
static_assert(sizeof(GD_GlyphRunInfo)==72);static_assert(sizeof(GD_GlyphDraw)==48);static_assert(sizeof(GD_GlyphBatch)==32);
GD_API int32_t __cdecl GD_PresenterSetGlyphRun(GD_Presenter*,uint32_t key,const GD_GlyphRunInfo*);
GD_API int32_t __cdecl GD_PresenterDrawGlyphRuns(GD_Presenter*,const GD_GlyphBatch*);
struct GD_GlyphStats {uint32_t size,version,runCount,reserved;uint64_t sourceBytes,maskBytes,rasterizations,draws;};
static_assert(sizeof(GD_GlyphStats)==48);
GD_API int32_t __cdecl GD_PresenterGetGlyphStatistics(GD_Presenter*,GD_GlyphStats*);
GD_API int32_t __cdecl GD_PresenterRemoveGlyphRun(GD_Presenter*,uint32_t key);
GD_API int32_t __cdecl GD_PresenterInvalidateGlyphRasters(GD_Presenter*);

// Groups isolate ordered drawing, then composite opacity and optional alpha mask
// once. Existing draw APIs target the current color/mask surface. No CPU readback.
// Optional bounds are in target pixels, rounded outwards and intersected with
// the parent. They preserve WPF layer rasterization at clipped curve edges.
struct GD_GroupInfo {uint32_t size,version;float opacity,left,top,right,bottom;uint32_t bounded;};
static_assert(sizeof(GD_GroupInfo)==32);
GD_API int32_t __cdecl GD_PresenterPushGroup(GD_Presenter*,const GD_GroupInfo*);
GD_API int32_t __cdecl GD_PresenterBeginGroupMask(GD_Presenter*);
GD_API int32_t __cdecl GD_PresenterPopGroup(GD_Presenter*);
GD_API int32_t __cdecl GD_PresenterDiscardGroups(GD_Presenter*);
GD_API int32_t __cdecl GD_PresenterTrimGroupCache(GD_Presenter*);
struct GD_GroupStats {uint32_t size,version,depth,cachedSlots;uint64_t colorBytes,maskBytes,allocations,composites;};
static_assert(sizeof(GD_GroupStats)==48);
GD_API int32_t __cdecl GD_PresenterGetGroupStatistics(GD_Presenter*,GD_GroupStats*);
// Internal content positioning for bounded overscan; never changes the HWND.
GD_API int32_t __cdecl GD_PresenterSetVisualOffset(GD_Presenter*,float x,float y);
GD_API int32_t __cdecl GD_PresenterPushEffectGroup(GD_Presenter*,const GD_GroupInfo*);
struct GD_GroupShadow {uint32_t size,version,radius,reserved;float offsetX,offsetY,red,green,blue,opacity,outputX,outputY;};
static_assert(sizeof(GD_GroupShadow)==48);
GD_API int32_t __cdecl GD_PresenterApplyGroupShadow(GD_Presenter*,const GD_GroupShadow*);
struct GD_GroupEffectStats {uint32_t size,version;uint64_t textureBytes,allocations,passes;};
static_assert(sizeof(GD_GroupEffectStats)==32);
GD_API int32_t __cdecl GD_PresenterGetGroupEffectStatistics(GD_Presenter*,GD_GroupEffectStats*);
