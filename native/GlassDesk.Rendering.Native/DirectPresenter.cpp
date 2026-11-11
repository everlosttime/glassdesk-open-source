#include "DirectPresenter.h"
#include "ProductionMaterial.h"
#include "DirectGlass.h"
#include "DirectImages.h"
#include "DirectShadow.h"
#include "DirectVectors.h"
#include "DirectGlyphs.h"
#include "DirectGroups.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <cmath>
#include <memory>
#include <mutex>
#include <new>
#include <atomic>

using Microsoft::WRL::ComPtr;
namespace {
void Check(HRESULT result) { if (FAILED(result)) throw result; }
bool ValidSize(uint32_t w, uint32_t h) { return w && h && w <= 8192 && h <= 8192 && uint64_t(w) * h <= 16777216; }
constexpr UINT SwapFlags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
std::atomic<uint64_t> nextIdentity{0};
std::atomic<uint32_t> liveDevices{0};

ComPtr<IDXGIAdapter1> FindAdapter(IDXGIFactory2* factory, HWND window) {
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        auto hr = factory->EnumAdapters1(i, &candidate);
        if (hr == DXGI_ERROR_NOT_FOUND) throw HRESULT(DXGI_ERROR_UNSUPPORTED);
        Check(hr);
        for (UINT j = 0;; ++j) {
            ComPtr<IDXGIOutput> output;
            hr = candidate->EnumOutputs(j, &output);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            Check(hr);
            DXGI_OUTPUT_DESC desc{}; Check(output->GetDesc(&desc));
            if (desc.Monitor == monitor) return candidate;
        }
    }
}

struct GraphicsSession {
    // Every immediate-context/DComp operation, including finalizer teardown,
    // uses this gate. Recursive for failed child creation inside Boundary.
    std::recursive_mutex gate;
    DWORD thread = GetCurrentThreadId();
    uint64_t identity{};
    LUID adapterLuid{};
    uint32_t presenters{};
    ComPtr<IDXGIFactory2> factory;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDCompositionDevice> composition;
    ComPtr<IDWriteFactory> textFactory;
    void EnsureTextFactory(){if(!textFactory)Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(textFactory.GetAddressOf())));}

    explicit GraphicsSession(HWND window) {
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        auto adapter = FindAdapter(factory.Get(), window);
        DXGI_ADAPTER_DESC1 desc{}; Check(adapter->GetDesc1(&desc)); adapterLuid = desc.AdapterLuid;
        Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIDevice> dxgi; Check(device.As(&dxgi));
        Check(DCompositionCreateDevice(dxgi.Get(), IID_PPV_ARGS(&composition)));
        identity = ++nextIdentity; ++liveDevices;
    }
    ~GraphicsSession() {
        std::scoped_lock lock(gate);
        context->ClearState(); context->Flush();
        textFactory.Reset();composition.Reset(); context.Reset(); device.Reset(); factory.Reset();
        --liveDevices;
    }
};
}
struct GD_Presenter {
    std::shared_ptr<GraphicsSession> session;
    uint32_t width{}, height{}, flags{};
    uint64_t submitted{}, wouldBlock{};
    ComPtr<IDXGISwapChain2> swap;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<IDCompositionTarget> windowTarget;
    ComPtr<IDCompositionVisual> visual;
    std::unique_ptr<gd::ProductionMaterial> material;
    std::unique_ptr<gd::DirectGlass> glass;
    std::unique_ptr<gd::DirectImages> images;
    std::unique_ptr<gd::DirectShadow> shadow;
    std::unique_ptr<gd::DirectVectors> vectors;
    std::unique_ptr<gd::DirectGlyphs> glyphs;
    std::unique_ptr<gd::DirectGroups> groups;
    ComPtr<ID3D11Texture2D> preparedBackdrop[2];
    ComPtr<ID3D11ShaderResourceView> preparedView[2];
    uint32_t preparedWidth[2]{}, preparedHeight[2]{};
    HWND window{};
    uint64_t glassDraws{};
    // Owned by the presenter; DXGI requires CloseHandle on this returned handle.
    HANDLE available{};

    explicit GD_Presenter(std::shared_ptr<GraphicsSession> graphics) : session(std::move(graphics)) {
        ++session->presenters;
    }

    void BindBuffer() {
        ComPtr<ID3D11Texture2D> buffer;
        Check(swap->GetBuffer(0, IID_PPV_ARGS(&buffer)));
        Check(session->device->CreateRenderTargetView(buffer.Get(), nullptr, &target));
    }
    ID3D11RenderTargetView* Output()const{return groups && groups->Depth()?groups->Current():target.Get();}
    bool HasGroups()const{return groups && groups->Depth()!=0;}
    ~GD_Presenter() {
        std::scoped_lock lock(session->gate);
        // Sever the visual graph before releasing its device and swapchain.
        if (windowTarget) windowTarget->SetRoot(nullptr);
        if (visual) visual->SetContent(nullptr);
        session->composition->Commit();
        session->context->ClearState(); session->context->Flush();
        // Release all GPU resources while still holding the session gate;
        // implicit member destruction would happen after this lock is gone.
        groups.reset();glyphs.reset();vectors.reset();shadow.reset(); images.reset(); glass.reset(); material.reset();
        for(auto& view:preparedView)view.Reset();for(auto& texture:preparedBackdrop)texture.Reset();
        target.Reset(); visual.Reset(); windowTarget.Reset(); swap.Reset();
        if (available) { CloseHandle(available); available = nullptr; }
        --session->presenters;
    }
};
namespace {
template<typename F> HRESULT Boundary(GD_Presenter* p, F&& function) noexcept {
    if (!p) return E_POINTER;
    try {
        std::scoped_lock lock(p->session->gate);
        if (GetCurrentThreadId() != p->session->thread) return RPC_E_WRONG_THREAD;
        return function(*p);
    }
    catch (HRESULT error) { return error; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
}
namespace {
HRESULT ValidateCreate(const GD_CreateInfo* info) {
    if (!info || info->size != sizeof(GD_CreateInfo)) return E_INVALIDARG;
    if (info->version != 1) return E_NOINTERFACE;
    if (!ValidSize(info->width, info->height) || (info->flags & ~1u) || info->reserved) return E_INVALIDARG;
    auto window = static_cast<HWND>(info->window);
    DWORD process{};
    if (!IsWindow(window) || !GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId()) return E_INVALIDARG;
    return S_OK;
}
HRESULT CreatePresenter(const GD_CreateInfo* info, std::shared_ptr<GraphicsSession> session, GD_Presenter** output) {
    try {
        std::scoped_lock lock(session->gate);
        auto p = std::make_unique<GD_Presenter>(session);
        p->width = info->width; p->height = info->height; p->flags = info->flags;
        p->window=static_cast<HWND>(info->window);
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = p->width; desc.Height = p->height;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED; desc.Flags = SwapFlags;
        ComPtr<IDXGISwapChain1> swap;
        Check(session->factory->CreateSwapChainForComposition(session->device.Get(), &desc, nullptr, &swap));
        Check(swap.As(&p->swap)); Check(p->swap->SetMaximumFrameLatency(1));
        p->available = p->swap->GetFrameLatencyWaitableObject();
        if (!p->available) return E_FAIL;
        p->BindBuffer();
        Check(session->composition->CreateTargetForHwnd(static_cast<HWND>(info->window), TRUE, &p->windowTarget));
        Check(session->composition->CreateVisual(&p->visual));
        Check(p->visual->SetContent(p->swap.Get())); Check(p->windowTarget->SetRoot(p->visual.Get()));
        Check(session->composition->Commit());
        *output = p.release(); return S_OK;
    }
    catch (HRESULT error) { return error; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
}
int32_t __cdecl GD_PresenterCreate(const GD_CreateInfo* info, GD_Presenter** output) {
    if (!output) return E_POINTER;
    *output = nullptr;
    const auto valid = ValidateCreate(info); if (FAILED(valid)) return valid;
    try { return CreatePresenter(info, std::make_shared<GraphicsSession>(static_cast<HWND>(info->window)), output); }
    catch (HRESULT error) { return error; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
int32_t __cdecl GD_PresenterCreateSibling(GD_Presenter* parent, const GD_CreateInfo* info, GD_Presenter** output) {
    if (!output) return E_POINTER;
    *output = nullptr;
    return Boundary(parent, [&](GD_Presenter& e) -> HRESULT {
        const auto valid = ValidateCreate(info); if (FAILED(valid)) return valid;
        auto adapter = FindAdapter(e.session->factory.Get(), static_cast<HWND>(info->window));
        DXGI_ADAPTER_DESC1 desc{}; Check(adapter->GetDesc1(&desc));
        if (desc.AdapterLuid.LowPart != e.session->adapterLuid.LowPart ||
            desc.AdapterLuid.HighPart != e.session->adapterLuid.HighPart) return DXGI_ERROR_UNSUPPORTED;
        return CreatePresenter(info, e.session, output);
    });
}
int32_t __cdecl GD_PresenterGetDeviceStatistics(GD_Presenter* p, GD_DeviceStats* stats) {
    if (!stats || stats->size != sizeof(GD_DeviceStats)) return E_INVALIDARG;
    if (stats->version != 1) return E_NOINTERFACE;
    return Boundary(p, [&](GD_Presenter& e) -> HRESULT {
        const auto& s = *e.session;
        *stats = {sizeof(GD_DeviceStats),1,s.identity,s.adapterLuid.LowPart,s.adapterLuid.HighPart,
            s.thread,s.presenters,liveDevices.load(),0};
        return S_OK;
    });
}
void __cdecl GD_PresenterDestroy(GD_Presenter* p) { delete p; }
int32_t __cdecl GD_PresenterClear(GD_Presenter* p, float r, float g, float b, float a) {
    for (float value : {r,g,b,a}) if (!std::isfinite(value) || value < 0 || value > 1) return E_INVALIDARG;
    return Boundary(p, [&](GD_Presenter& e) -> HRESULT {
        if (!e.target) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        const float color[4] = {r*a,g*a,b*a,a};
        // Flip-model Present and sibling teardown unbind the output. Rebind
        // before *any* use of the current back buffer, including a clear-only
        // frame; otherwise a later Present can retain an older candidate.
        auto output=e.Output();e.session->context->OMSetRenderTargets(1,&output,nullptr);
        e.session->context->ClearRenderTargetView(output, color);
        return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterPresent(GD_Presenter* p) {
    return Boundary(p, [](GD_Presenter& e) -> HRESULT {
        if(e.HasGroups())return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        const auto result = e.swap->Present(1, DXGI_PRESENT_DO_NOT_WAIT);
        if (result == DXGI_ERROR_WAS_STILL_DRAWING || result == DXGI_STATUS_OCCLUDED) { ++e.wouldBlock; return S_FALSE; }
        Check(result); ++e.submitted; return S_OK;
    });
}
int32_t __cdecl GD_PresenterResize(GD_Presenter* p, uint32_t width, uint32_t height) {
    if (!ValidSize(width,height)) return E_INVALIDARG;
    return Boundary(p, [&](GD_Presenter& e) -> HRESULT {
        if(e.HasGroups())return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if (width == e.width && height == e.height) return S_OK;
        if(e.groups)Check(e.groups->Trim());
        if(e.shadow)e.shadow->ReleaseTarget();
        if(e.vectors)e.vectors->ReleaseTarget();
        if(e.glyphs)e.glyphs->ReleaseTarget();
        e.session->context->OMSetRenderTargets(0,nullptr,nullptr); e.target.Reset();
        const auto result = e.swap->ResizeBuffers(2,width,height,DXGI_FORMAT_B8G8R8A8_UNORM,SwapFlags);
        if (FAILED(result)) { e.BindBuffer(); return result; }
        e.width=width; e.height=height; e.material.reset(); e.glassDraws=0; e.BindBuffer(); return S_OK;
    });
}
int32_t __cdecl GD_PresenterUploadBitmap(GD_Presenter* p, const GD_Bitmap* bitmap) {
    const auto valid=gd::ValidateBitmap(bitmap);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.material)e.material=std::make_unique<gd::ProductionMaterial>(e.session->device.Get());
        e.material->Upload(*bitmap);return S_OK;
    });
}
int32_t __cdecl GD_PresenterRenderMaterial(GD_Presenter* p, const GD_Material* material) {
    const auto valid=gd::ValidateMaterial(material);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.material || !e.target)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(e.material->Width()!=e.width || e.material->Height()!=e.height)return E_INVALIDARG;
        e.material->Render(e.session->context.Get(),e.Output(),*material);return S_OK;
    });
}
int32_t __cdecl GD_PresenterRenderGlass(GD_Presenter* p,const GD_Material* material,const GD_GlassScene* scene) {
    auto valid=gd::ValidateMaterial(material);if(FAILED(valid))return valid;
    valid=gd::ValidateGlass(scene);if(FAILED(valid))return valid;
    if(material->kind!=0)return E_NOTIMPL; // Frosted rim and plain material have separate production chrome.
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        // For small radii the production straight-normal switch is inside its
        // 24-DIP lens band. D3D9/11 interpolation can choose different sides at
        // exact bisectors. Fail before changing any frame or GPU resource until
        // that parity is established; the caller must retain the legacy path.
        if(std::min(scene->radius,std::min(scene->panelWidth,scene->panelHeight)*.5f)<16)return E_NOTIMPL;
        if(!e.material || !e.target)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(!e.glass)e.glass=std::make_unique<gd::DirectGlass>(e.session->device.Get());
        auto input=e.material->Filter(e.session->context.Get(),*material);
        e.glass->Render(e.session->context.Get(),input,e.Output(),e.width,e.height,*scene);
        ++e.glassDraws;
        return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterUploadPreparedBackdrop(GD_Presenter* p,uint32_t slot,const GD_Bitmap* bitmap) {
    const auto valid=gd::ValidateBitmap(bitmap);if(FAILED(valid))return valid;
    if(slot>1 || uint64_t(bitmap->width)*bitmap->height*4>16*1024*1024)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.preparedBackdrop[slot] || e.preparedWidth[slot]!=bitmap->width || e.preparedHeight[slot]!=bitmap->height) {
            D3D11_TEXTURE2D_DESC desc{};desc.Width=bitmap->width;desc.Height=bitmap->height;desc.MipLevels=desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA data{bitmap->pixels,bitmap->stride,0};
            ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11ShaderResourceView> view;
            Check(e.session->device->CreateTexture2D(&desc,&data,&texture));
            Check(e.session->device->CreateShaderResourceView(texture.Get(),nullptr,&view));
            e.preparedBackdrop[slot]=texture;e.preparedView[slot]=view;e.preparedWidth[slot]=bitmap->width;e.preparedHeight[slot]=bitmap->height;
        } else e.session->context->UpdateSubresource(e.preparedBackdrop[slot].Get(),0,nullptr,bitmap->pixels,bitmap->stride,0);
        return S_OK;
    });
}
int32_t __cdecl GD_PresenterRenderPreparedGlass(GD_Presenter* p,uint32_t slot,const GD_GlassScene* scene,uint32_t frosted) {
    const auto valid=gd::ValidateGlass(scene);if(FAILED(valid))return valid;
    if(frosted>1 || slot>1)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.preparedView[slot])return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(!e.glass)e.glass=std::make_unique<gd::DirectGlass>(e.session->device.Get());
        e.glass->Render(e.session->context.Get(),e.preparedView[slot].Get(),e.Output(),e.width,e.height,*scene,frosted!=0);
        ++e.glassDraws;return S_OK;
    });
}
int32_t __cdecl GD_PresenterCompositePreparedGlass(GD_Presenter* p,uint32_t slot,const GD_GlassScene* scene,uint32_t frosted) {
    const auto valid=gd::ValidateGlass(scene);if(FAILED(valid))return valid;
    if(frosted>1 || slot>1)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.preparedView[slot])return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(!e.glass)e.glass=std::make_unique<gd::DirectGlass>(e.session->device.Get());
        e.glass->Render(e.session->context.Get(),e.preparedView[slot].Get(),e.Output(),e.width,e.height,*scene,frosted!=0,true);
        ++e.glassDraws;return S_OK;
    });
}
int32_t __cdecl GD_PresenterGetMaterialStatistics(GD_Presenter* p,GD_DirectMaterialStats* stats) {
    if(!stats || stats->size!=sizeof(GD_DirectMaterialStats))return E_INVALIDARG;
    if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        const uint64_t bytes=e.material?uint64_t(e.material->Width())*e.material->Height()*4:0;
        *stats={sizeof(GD_DirectMaterialStats),1,e.material?e.material->FilterRuns():0,bytes,bytes*2,e.glassDraws};
        return S_OK;
    });
}
int32_t __cdecl GD_PresenterSetImage(GD_Presenter* p,uint32_t key,const GD_Bitmap* bitmap) {
    if(!key)return E_INVALIDARG;
    const auto valid=gd::ValidateBitmap(bitmap);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.images)e.images=std::make_unique<gd::DirectImages>(e.session->device.Get());
        e.images->Set(key,*bitmap);return S_OK;
    });
}
int32_t __cdecl GD_PresenterRemoveImage(GD_Presenter* p,uint32_t key) {
    if(!key)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{return e.images?e.images->Remove(key):S_FALSE;});
}
int32_t __cdecl GD_PresenterDrawImages(GD_Presenter* p,const GD_ImageBatch* batch) {
    const auto valid=gd::ValidateImages(batch);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!batch->count)return S_OK;
        if(!e.target)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(!e.images)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        e.images->Render(e.session->context.Get(),e.Output(),e.width,e.height,*batch);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterDrawMappedImages(GD_Presenter* p,const GD_MappedImageBatch* batch) {
    const auto valid=gd::ValidateMappedImages(batch);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!batch->count)return S_OK;
        if(!e.target)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(!e.images)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        e.images->RenderMapped(e.session->context.Get(),e.Output(),e.width,e.height,*batch);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterGetImageStatistics(GD_Presenter* p,GD_ImageStats* stats) {
    if(!stats || stats->size!=sizeof(GD_ImageStats))return E_INVALIDARG;
    if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        *stats=e.images?e.images->Statistics():GD_ImageStats{sizeof(GD_ImageStats),1,0,0,0,0,0};return S_OK;
    });
}
int32_t __cdecl GD_PresenterSetVector(GD_Presenter* p,uint32_t key,const GD_VectorInfo* info) {
    if(!key)return E_INVALIDARG;
    const auto valid=gd::ValidateVector(info);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.vectors)e.vectors=std::make_unique<gd::DirectVectors>(e.session->device.Get());
        e.vectors->Set(key,*info);return S_OK;
    });
}
int32_t __cdecl GD_PresenterRemoveVector(GD_Presenter* p,uint32_t key) {
    if(!key)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{return e.vectors?e.vectors->Remove(key):S_FALSE;});
}
int32_t __cdecl GD_PresenterSetVectorBrush(GD_Presenter* p,uint32_t key,const GD_VectorBrushInfo* info) {
    if(!key)return E_INVALIDARG;
    const auto valid=gd::ValidateVectorBrush(info);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{return e.vectors?e.vectors->SetBrush(key,*info):HRESULT_FROM_WIN32(ERROR_NOT_FOUND);});
}
int32_t __cdecl GD_PresenterSetGlyphRun(GD_Presenter* p,uint32_t key,const GD_GlyphRunInfo* info) {
    if(!key)return E_INVALIDARG;const auto valid=gd::ValidateGlyphRun(info);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.glyphs){e.session->EnsureTextFactory();e.glyphs=std::make_unique<gd::DirectGlyphs>(e.session->device.Get(),e.session->textFactory.Get(),e.window);}
        e.glyphs->Set(key,*info);return S_OK;
    });
}
int32_t __cdecl GD_PresenterDrawGlyphRuns(GD_Presenter* p,const GD_GlyphBatch* batch) {
    const auto valid=gd::ValidateGlyphBatch(batch);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!batch->count)return S_OK;if(!e.glyphs)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        e.glyphs->Render(e.session->context.Get(),e.Output(),e.width,e.height,*batch);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterGetGlyphStatistics(GD_Presenter* p,GD_GlyphStats* stats) {
    if(!stats || stats->size!=sizeof(GD_GlyphStats))return E_INVALIDARG;if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{*stats=e.glyphs?e.glyphs->Statistics():GD_GlyphStats{sizeof(GD_GlyphStats),1,0,0,0,0,0,0};return S_OK;});
}
int32_t __cdecl GD_PresenterRemoveGlyphRun(GD_Presenter* p,uint32_t key) {
    if(!key)return E_INVALIDARG;return Boundary(p,[&](GD_Presenter& e)->HRESULT{return e.glyphs?e.glyphs->Remove(key):S_FALSE;});
}
int32_t __cdecl GD_PresenterInvalidateGlyphRasters(GD_Presenter* p) {
    return Boundary(p,[](GD_Presenter& e)->HRESULT{if(e.glyphs)e.glyphs->ReleaseTarget();return S_OK;});
}
int32_t __cdecl GD_PresenterDrawVectors(GD_Presenter* p,const GD_ImageBatch* batch) {
    const auto valid=gd::ValidateImages(batch);if(FAILED(valid))return valid;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!batch->count)return S_OK;
        if(!e.vectors)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        e.vectors->Render(e.session->context.Get(),e.Output(),e.width,e.height,*batch,e.groups?e.groups->Bounds():nullptr);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterGetVectorStatistics(GD_Presenter* p,GD_VectorStats* stats) {
    if(!stats || stats->size!=sizeof(GD_VectorStats))return E_INVALIDARG;
    if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{*stats=e.vectors?e.vectors->Statistics():GD_VectorStats{sizeof(GD_VectorStats),1,0,0,0,0,0,0,0};return S_OK;});
}
int32_t __cdecl GD_PresenterRenderShadow(GD_Presenter* p,const GD_ShadowScene* scene) {
    const auto result=gd::ValidateShadow(scene);if(FAILED(result))return result;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.shadow)e.shadow=std::make_unique<gd::DirectShadow>(e.session->device.Get());
        e.shadow->Render(e.Output(),e.session->context.Get(),*scene,e.width,e.height);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterGetShadowStatistics(GD_Presenter* p,GD_ShadowStats* stats) {
    if(!stats || stats->size!=sizeof(GD_ShadowStats))return E_INVALIDARG;
    if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {*stats=e.shadow?e.shadow->Statistics():GD_ShadowStats{sizeof(GD_ShadowStats),1,0,0,0,0,0,0};return S_OK;});
}
int32_t __cdecl GD_PresenterGetStatistics(GD_Presenter* p, GD_PresenterStats* stats) {
    if (!stats || stats->size != sizeof(GD_PresenterStats)) return E_INVALIDARG;
    if (stats->version != 1) return E_NOINTERFACE;
    return Boundary(p, [&](GD_Presenter& e) -> HRESULT {
        DXGI_SWAP_CHAIN_DESC1 desc{};
        Check(e.swap->GetDesc1(&desc));
        *stats={sizeof(GD_PresenterStats),1,e.submitted,e.wouldBlock,desc.Width,desc.Height,desc.BufferCount,0,
            uint64_t(desc.Width)*desc.Height*4*desc.BufferCount};
        return S_OK;
    });
}
int32_t __cdecl GD_PresenterPushGroup(GD_Presenter* p,const GD_GroupInfo* info) {
    if(!info || info->size!=sizeof(GD_GroupInfo))return E_INVALIDARG;
    if(info->version!=1)return E_NOINTERFACE;
    if(!std::isfinite(info->opacity) || info->opacity<0 || info->opacity>1 || info->bounded>1)return E_INVALIDARG;
    for(float v:{info->left,info->top,info->right,info->bottom})if(!std::isfinite(v) || std::abs(v)>1048576)return E_INVALIDARG;
    if(info->right<info->left || info->bottom<info->top)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.groups)e.groups=std::make_unique<gd::DirectGroups>(e.session->device.Get());
        e.groups->Push(e.session->context.Get(),e.width,e.height,*info);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterBeginGroupMask(GD_Presenter* p) {
    return Boundary(p,[](GD_Presenter& e)->HRESULT {
        if(!e.groups)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        Check(e.groups->BeginMask(e.session->context.Get(),e.width,e.height));return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterPushEffectGroup(GD_Presenter* p,const GD_GroupInfo* info) {
    if(!info || info->size!=sizeof(GD_GroupInfo))return E_INVALIDARG;
    if(info->version!=1)return E_NOINTERFACE;
    if(!std::isfinite(info->opacity) || info->opacity<0 || info->opacity>1 || info->bounded>1)return E_INVALIDARG;
    for(float v:{info->left,info->top,info->right,info->bottom})if(!std::isfinite(v) || std::abs(v)>1048576)return E_INVALIDARG;
    if(info->right<info->left || info->bottom<info->top)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.groups)e.groups=std::make_unique<gd::DirectGroups>(e.session->device.Get());
        e.groups->Push(e.session->context.Get(),e.width,e.height,*info,true);return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterApplyGroupShadow(GD_Presenter* p,const GD_GroupShadow* info) {
    if(!info || info->size!=sizeof(GD_GroupShadow))return E_INVALIDARG;
    if(info->version!=1)return E_NOINTERFACE;
    if(info->radius>100 || info->reserved)return E_INVALIDARG;
    for(float v:{info->outputX,info->outputY})if(!std::isfinite(v) || std::abs(v)>1)return E_INVALIDARG;
    for(float v:{info->offsetX,info->offsetY})if(!std::isfinite(v) || std::abs(v)>256)return E_INVALIDARG;
    for(float v:{info->red,info->green,info->blue,info->opacity})if(!std::isfinite(v) || v<0 || v>1)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!e.groups)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        Check(e.groups->ApplyShadow(e.session->context.Get(),e.width,e.height,*info));return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterGetGroupEffectStatistics(GD_Presenter* p,GD_GroupEffectStats* stats) {
    if(!stats || stats->size!=sizeof(GD_GroupEffectStats))return E_INVALIDARG;if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT{*stats=e.groups?e.groups->EffectStatistics():GD_GroupEffectStats{sizeof(GD_GroupEffectStats),1,0,0,0};return S_OK;});
}
int32_t __cdecl GD_PresenterPopGroup(GD_Presenter* p) {
    return Boundary(p,[](GD_Presenter& e)->HRESULT {
        if(!e.groups)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        Check(e.groups->Pop(e.session->context.Get(),e.target.Get(),e.width,e.height));return e.session->device->GetDeviceRemovedReason();
    });
}
int32_t __cdecl GD_PresenterDiscardGroups(GD_Presenter* p) {
    return Boundary(p,[](GD_Presenter& e)->HRESULT {if(e.groups)e.groups->Discard(e.session->context.Get());return S_OK;});
}
int32_t __cdecl GD_PresenterTrimGroupCache(GD_Presenter* p) {
    return Boundary(p,[](GD_Presenter& e)->HRESULT {return e.groups?e.groups->Trim():S_OK;});
}
int32_t __cdecl GD_PresenterGetGroupStatistics(GD_Presenter* p,GD_GroupStats* stats) {
    if(!stats || stats->size!=sizeof(GD_GroupStats))return E_INVALIDARG;
    if(stats->version!=1)return E_NOINTERFACE;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        *stats=e.groups?e.groups->Statistics():GD_GroupStats{sizeof(GD_GroupStats),1,0,0,0,0,0,0};return S_OK;
    });
}
void* __cdecl GD_PresenterGetWaitHandle(GD_Presenter* p) {
    if (!p || GetCurrentThreadId() != p->session->thread) return nullptr;
    return p->available;
}
int32_t __cdecl GD_PresenterSetVisualOffset(GD_Presenter* p,float x,float y) {
    if(!std::isfinite(x) || !std::isfinite(y) || std::abs(x)>32768 || std::abs(y)>32768)return E_INVALIDARG;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(e.HasGroups())return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        Check(e.visual->SetOffsetX(x));Check(e.visual->SetOffsetY(y));return e.session->composition->Commit();
    });
}
int32_t __cdecl GD_PresenterReadPixel(GD_Presenter* p, uint32_t x, uint32_t y, uint32_t* bgra) {
    if (!bgra) return E_POINTER;
    *bgra=0;
    return Boundary(p, [&](GD_Presenter& e) -> HRESULT {
        if (!(e.flags & 1)) return E_ACCESSDENIED;
        if (x >= e.width || y >= e.height) return E_INVALIDARG;
        ComPtr<ID3D11Texture2D> source, staging;
        Check(e.swap->GetBuffer(0,IID_PPV_ARGS(&source)));
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=desc.Height=desc.MipLevels=desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count=1;
        desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        Check(e.session->device->CreateTexture2D(&desc,nullptr,&staging));
        const D3D11_BOX box{x,y,0,x+1,y+1,1};
        e.session->context->CopySubresourceRegion(staging.Get(),0,0,0,0,source.Get(),0,&box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(e.session->context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        memcpy(bgra,mapped.pData,sizeof(uint32_t));
        e.session->context->Unmap(staging.Get(),0); return S_OK;
    });
}
int32_t __cdecl GD_PresenterReadPixels(GD_Presenter* p, void* pixels, uint64_t capacity, uint32_t stride) {
    if(!pixels)return E_POINTER;
    return Boundary(p,[&](GD_Presenter& e)->HRESULT {
        if(!(e.flags&1))return E_ACCESSDENIED;
        if(stride<uint64_t(e.width)*4 || capacity<uint64_t(e.height-1)*stride+uint64_t(e.width)*4)return E_INVALIDARG;
        ComPtr<ID3D11Texture2D> source,staging;
        Check(e.swap->GetBuffer(0,IID_PPV_ARGS(&source)));
        D3D11_TEXTURE2D_DESC desc{};source->GetDesc(&desc);
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.MiscFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        Check(e.session->device->CreateTexture2D(&desc,nullptr,&staging));
        e.session->context->CopyResource(staging.Get(),source.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};Check(e.session->context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        for(uint32_t y=0;y<e.height;y++)memcpy(static_cast<uint8_t*>(pixels)+uint64_t(y)*stride,
            static_cast<const uint8_t*>(mapped.pData)+uint64_t(y)*mapped.RowPitch,size_t(e.width)*4);
        e.session->context->Unmap(staging.Get(),0);return S_OK;
    });
}
