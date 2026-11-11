#include "Rendering.h"
#include "CalibrationShader.h"
#include "ProductionMaterial.h"
#include <windows.h>
#include <d3d9.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <memory>
#include <mutex>
#include <new>

using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t Version = 1;
constexpr size_t SlotCount = 3;
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
bool Dimensions(uint32_t w, uint32_t h) {
    return w > 0 && h > 0 && w <= 8192 && h <= 8192 && uint64_t(w) * h <= 16777216;
}
struct Slot {
    ComPtr<IDirect3DTexture9> texture9;
    ComPtr<IDirect3DSurface9> surface9;
    ComPtr<ID3D11Texture2D> texture11;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11Query> complete;
    enum class State { Free, Pending, Leased } state = State::Free;
    uint64_t sequence = 0, lease = 0;
    int64_t qpc = 0;
};
struct Devices {
    ComPtr<IDirect3D9Ex> api9;
    ComPtr<IDirect3DDevice9Ex> device9;
    ComPtr<ID3D11Device> device11;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    std::unique_ptr<gd::ProductionMaterial> material;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11RasterizerState> raster;
    std::array<Slot, SlotCount> slots;
    LUID adapter{};
    HMONITOR monitor{};
};
std::array<Slot, SlotCount> MakeSlots(Devices& gpu, uint32_t width, uint32_t height) {
    std::array<Slot, SlotCount> slots;
    for (auto& slot : slots) {
        HANDLE shared = nullptr;
        Check(gpu.device9->CreateTexture(width, height, 1, D3DUSAGE_RENDERTARGET,
            D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &slot.texture9, &shared));
        // This is a legacy 9Ex share token, NOT an NT handle; never CloseHandle it.
        Check(slot.texture9->GetSurfaceLevel(0, &slot.surface9));
        Check(gpu.device11->OpenSharedResource(shared, IID_PPV_ARGS(&slot.texture11)));
        Check(gpu.device11->CreateRenderTargetView(slot.texture11.Get(), nullptr, &slot.target));
        D3D11_QUERY_DESC query{ D3D11_QUERY_EVENT, 0 };
        Check(gpu.device11->CreateQuery(&query, &slot.complete));
    }
    return slots;
}
std::unique_ptr<Devices> MakeDevices(HWND window, uint32_t width, uint32_t height) {
    auto gpu = std::make_unique<Devices>();
    Check(Direct3DCreate9Ex(D3D_SDK_VERSION, &gpu->api9));
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    gpu->monitor = monitor;
    UINT adapter9 = D3DADAPTER_DEFAULT;
    for (UINT i = 0; i < gpu->api9->GetAdapterCount(); ++i)
        if (gpu->api9->GetAdapterMonitor(i) == monitor) { adapter9 = i; break; }
    Check(gpu->api9->GetAdapterLUID(adapter9, &gpu->adapter));
    D3DPRESENT_PARAMETERS pp{};
    pp.Windowed = TRUE; pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = window; pp.BackBufferWidth = 1; pp.BackBufferHeight = 1;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    Check(gpu->api9->CreateDeviceEx(adapter9, D3DDEVTYPE_HAL, window,
        D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE,
        &pp, nullptr, &gpu->device9));
    ComPtr<IDXGIFactory1> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIAdapter1> adapter11;
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        const auto result = factory->EnumAdapters1(i, &candidate);
        if (result == DXGI_ERROR_NOT_FOUND) break;
        Check(result);
        DXGI_ADAPTER_DESC1 desc{}; Check(candidate->GetDesc1(&desc));
        if (desc.AdapterLuid.LowPart == gpu->adapter.LowPart && desc.AdapterLuid.HighPart == gpu->adapter.HighPart) {
            adapter11 = candidate; break;
        }
    }
    if (!adapter11) throw HRESULT(DXGI_ERROR_UNSUPPORTED);
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    Check(D3D11CreateDevice(adapter11.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, static_cast<UINT>(std::size(levels)),
        D3D11_SDK_VERSION, &gpu->device11, nullptr, &gpu->context));
    ComPtr<ID3DBlob> vs, ps, errors;
    Check(D3DCompile(GD_CalibrationShader, sizeof(GD_CalibrationShader) - 1,
        "Calibration", nullptr, nullptr, "VS", "vs_4_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &vs, &errors));
    errors.Reset();
    Check(D3DCompile(GD_CalibrationShader, sizeof(GD_CalibrationShader) - 1,
        "Calibration", nullptr, nullptr, "PS", "ps_4_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &ps, &errors));
    Check(gpu->device11->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &gpu->vertex));
    Check(gpu->device11->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &gpu->pixel));
    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = 32; buffer.Usage = D3D11_USAGE_DEFAULT; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    Check(gpu->device11->CreateBuffer(&buffer, nullptr, &gpu->constants));
    D3D11_RASTERIZER_DESC raster{};
    raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
    Check(gpu->device11->CreateRasterizerState(&raster, &gpu->raster));
    gpu->slots = MakeSlots(*gpu, width, height);
    return gpu;
}
}
struct GD_Engine {
    std::mutex gate;
    std::unique_ptr<Devices> gpu;
    HWND window{};
    DWORD thread{};
    uint32_t width{}, height{}, flags{};
    uint64_t generation = 1, submitted = 0, acquired = 0, dropped = 0, readbacks = 0, nextLease = 1;
    bool Leased() const {
        for (auto& slot : gpu->slots) if (slot.state == Slot::State::Leased) return true;
        return false;
    }
};
namespace {
template<typename F> HRESULT Boundary(GD_Engine* engine, bool ownerThread, F&& call) noexcept {
    if (!engine) return E_POINTER;
    try {
        std::scoped_lock lock(engine->gate);
        if (ownerThread && GetCurrentThreadId() != engine->thread) return RPC_E_WRONG_THREAD;
        return call(*engine);
    }
    catch (HRESULT error) { return error; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
bool Ready(Devices& gpu, Slot& slot) {
    const auto hr = gpu.context->GetData(slot.complete.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
    Check(hr); return hr == S_OK;
}
}
int32_t __cdecl GD_Create(const GD_CreateInfo* info, GD_Engine** output) {
    if (!output) return E_POINTER;
    *output = nullptr;
    if (!info || info->size != sizeof(GD_CreateInfo)) return E_INVALIDARG;
    if (info->version != Version) return E_NOINTERFACE;
    if (!Dimensions(info->width, info->height) || !IsWindow(static_cast<HWND>(info->window))
        || info->reserved || (info->flags & ~1u)) return E_INVALIDARG;
    try {
        auto engine = std::make_unique<GD_Engine>();
        engine->window = static_cast<HWND>(info->window); engine->thread = GetCurrentThreadId();
        engine->width = info->width; engine->height = info->height; engine->flags = info->flags;
        engine->gpu = MakeDevices(engine->window, engine->width, engine->height);
        *output = engine.release(); return S_OK;
    }
    catch (HRESULT error) { return error; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
void __cdecl GD_Destroy(GD_Engine* engine) { delete engine; }
int32_t __cdecl GD_Render(GD_Engine* engine, const GD_Scene* scene) {
    if (!scene || scene->size != sizeof(GD_Scene)) return E_INVALIDARG;
    if (scene->version != Version) return E_NOINTERFACE;
    if (!std::isfinite(scene->seconds) || !std::isfinite(scene->opacity) || !std::isfinite(scene->cornerRadius)
        || scene->seconds < 0 || scene->opacity < 0 || scene->opacity > 1 || scene->cornerRadius < 0
        || scene->pattern > 1 || scene->reserved) return E_INVALIDARG;
    return Boundary(engine, true, [&](GD_Engine& e) -> HRESULT {
        auto& gpu = *e.gpu;
        Slot* target = nullptr;
        for (auto& slot : gpu.slots) if (slot.state == Slot::State::Free) { target = &slot; break; }
        if (!target) {
            for (auto& slot : gpu.slots)
                if (slot.state == Slot::State::Pending && Ready(gpu, slot)) {
                    target = &slot; ++e.dropped; break;
                }
        }
        if (!target) { ++e.dropped; return S_FALSE; }
        const float constants[8] = { float(e.width), float(e.height), float(std::fmod(scene->seconds, 3600.0)),
            float(scene->pattern), scene->opacity, scene->cornerRadius, 0, 0 };
        gpu.context->UpdateSubresource(gpu.constants.Get(), 0, nullptr, constants, 0, 0);
        ID3D11Buffer* buffers[] = { gpu.constants.Get() };
        ID3D11RenderTargetView* targets[] = { target->target.Get() };
        gpu.context->OMSetRenderTargets(1, targets, nullptr);
        D3D11_VIEWPORT viewport{ 0, 0, float(e.width), float(e.height), 0, 1 };
        gpu.context->RSSetViewports(1, &viewport);
        gpu.context->RSSetState(gpu.raster.Get());
        gpu.context->IASetInputLayout(nullptr);
        gpu.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        gpu.context->VSSetShader(gpu.vertex.Get(), nullptr, 0);
        gpu.context->PSSetShader(gpu.pixel.Get(), nullptr, 0);
        gpu.context->PSSetConstantBuffers(0, 1, buffers);
        gpu.context->Draw(3, 0);
        gpu.context->OMSetRenderTargets(0, nullptr, nullptr);
        gpu.context->End(target->complete.Get());
        gpu.context->Flush();
        Check(gpu.device11->GetDeviceRemovedReason());
        target->sequence = ++e.submitted;
        LARGE_INTEGER qpc{}; QueryPerformanceCounter(&qpc); target->qpc = qpc.QuadPart;
        target->state = Slot::State::Pending;
        return S_OK;
    });
}
int32_t __cdecl GD_Acquire(GD_Engine* engine, GD_Frame* frame) {
    if (!frame || frame->size != sizeof(GD_Frame)) return E_INVALIDARG;
    if (frame->version != Version) return E_NOINTERFACE;
    // Failed acquisition cannot leave a previous caller's pointer/token behind.
    *frame = { sizeof(GD_Frame), Version, nullptr, 0, 0, 0, 0, 0, 0 };
    return Boundary(engine, true, [&](GD_Engine& e) -> HRESULT {
        auto& gpu = *e.gpu;
        Slot* newest = nullptr;
        for (auto& slot : gpu.slots)
            if (slot.state == Slot::State::Pending && Ready(gpu, slot)
                && (!newest || slot.sequence > newest->sequence)) newest = &slot;
        if (!newest) return S_FALSE;
        for (auto& slot : gpu.slots)
            if (&slot != newest && slot.state == Slot::State::Pending && slot.sequence < newest->sequence && Ready(gpu, slot)) {
                slot.state = Slot::State::Free; ++e.dropped;
            }
        newest->state = Slot::State::Leased; newest->lease = e.nextLease++;
        newest->surface9->AddRef(); ++e.acquired;
        *frame = { sizeof(GD_Frame), Version, newest->surface9.Get(), newest->lease, newest->sequence,
            e.generation, newest->qpc, e.width, e.height };
        return S_OK;
    });
}
int32_t __cdecl GD_UploadBitmap(GD_Engine* engine, const GD_Bitmap* bitmap) {
    const auto valid=gd::ValidateBitmap(bitmap);if(FAILED(valid))return valid;
    return Boundary(engine,true,[&](GD_Engine& e)->HRESULT {
        if(bitmap->width!=e.width || bitmap->height!=e.height)return E_INVALIDARG;
        auto& gpu=*e.gpu;
        if(!gpu.material)gpu.material=std::make_unique<gd::ProductionMaterial>(gpu.device11.Get());
        gpu.material->Upload(*bitmap);return S_OK;
    });
}
int32_t __cdecl GD_RenderMaterial(GD_Engine* engine, const GD_Material* material) {
    const auto valid=gd::ValidateMaterial(material);if(FAILED(valid))return valid;
    return Boundary(engine,true,[&](GD_Engine& e)->HRESULT {
        auto& gpu=*e.gpu;
        if(!gpu.material)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        if(MonitorFromWindow(e.window,MONITOR_DEFAULTTONEAREST)!=gpu.monitor)return DXGI_ERROR_UNSUPPORTED;
        Slot* target=nullptr;
        for(auto& slot:gpu.slots)if(slot.state==Slot::State::Free){target=&slot;break;}
        if(!target)for(auto& slot:gpu.slots)if(slot.state==Slot::State::Pending && Ready(gpu,slot)){
            target=&slot;++e.dropped;break;
        }
        if(!target){++e.dropped;return S_FALSE;}
        gpu.material->Render(gpu.context.Get(),target->target.Get(),*material);
        gpu.context->End(target->complete.Get());gpu.context->Flush();
        target->sequence=++e.submitted;
        LARGE_INTEGER qpc{};QueryPerformanceCounter(&qpc);target->qpc=qpc.QuadPart;
        target->state=Slot::State::Pending;return S_OK;
    });
}
int32_t __cdecl GD_Release(GD_Engine* engine, uint64_t lease) {
    return Boundary(engine, false, [&](GD_Engine& e) -> HRESULT {
        for (auto& slot : e.gpu->slots) if (slot.state == Slot::State::Leased && slot.lease == lease) {
            slot.surface9->Release(); slot.state = Slot::State::Free; slot.lease = 0; return S_OK;
        }
        return E_INVALIDARG;
    });
}
int32_t __cdecl GD_Resize(GD_Engine* engine, uint32_t width, uint32_t height) {
    if (!Dimensions(width, height)) return E_INVALIDARG;
    return Boundary(engine, true, [&](GD_Engine& e) -> HRESULT {
        if (e.Leased()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        if (e.width == width && e.height == height) return S_OK;
        auto slots = MakeSlots(*e.gpu, width, height);
        e.gpu->material.reset();
        e.gpu->slots = std::move(slots); e.width = width; e.height = height; ++e.generation;
        return S_OK;
    });
}
int32_t __cdecl GD_Recreate(GD_Engine* engine) {
    return Boundary(engine, true, [&](GD_Engine& e) -> HRESULT {
        if (e.Leased()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        auto gpu = MakeDevices(e.window, e.width, e.height);
        e.gpu = std::move(gpu); ++e.generation; return S_OK;
    });
}
int32_t __cdecl GD_GetStats(GD_Engine* engine, GD_Stats* stats) {
    if (!stats || stats->size != sizeof(GD_Stats)) return E_INVALIDARG;
    if (stats->version != Version) return E_NOINTERFACE;
    return Boundary(engine, false, [&](GD_Engine& e) -> HRESULT {
        uint32_t leased = 0, pending = 0;
        for (auto& slot : e.gpu->slots) {
            leased += slot.state == Slot::State::Leased;
            pending += slot.state == Slot::State::Pending;
        }
        *stats = { sizeof(GD_Stats), Version, e.generation, e.submitted, e.acquired, e.dropped, e.readbacks,
            uint64_t(e.width) * e.height * 4 * SlotCount, e.width, e.height, leased, pending,
            e.gpu->adapter.LowPart, e.gpu->adapter.HighPart };
        return S_OK;
    });
}
int32_t __cdecl GD_ReadPixel(GD_Engine* engine, uint64_t lease, uint32_t x, uint32_t y, uint32_t* bgra) {
    if (!bgra) return E_POINTER;
    *bgra = 0;
    return Boundary(engine, true, [&](GD_Engine& e) -> HRESULT {
        if (!(e.flags & 1)) return E_ACCESSDENIED;
        if (x >= e.width || y >= e.height) return E_INVALIDARG;
        Slot* input = nullptr;
        for (auto& slot : e.gpu->slots) if (slot.state == Slot::State::Leased && slot.lease == lease) input = &slot;
        if (!input) return E_INVALIDARG;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = desc.Height = desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        Check(e.gpu->device11->CreateTexture2D(&desc, nullptr, &staging));
        D3D11_BOX box{ x, y, 0, x + 1, y + 1, 1 };
        e.gpu->context->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, input->texture11.Get(), 0, &box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(e.gpu->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        memcpy(bgra, mapped.pData, sizeof(uint32_t));
        e.gpu->context->Unmap(staging.Get(), 0); ++e.readbacks;
        return S_OK;
    });
}
