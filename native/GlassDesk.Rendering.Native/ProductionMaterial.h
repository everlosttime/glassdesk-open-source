#pragma once
#include "Rendering.h"
#include "StaticMaterialShader.h"
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <cstring>

// One production prefilter implementation for both legacy interop and direct
// presentation. Owns only the uploaded source and two quantized scratch surfaces.
namespace gd {
inline void CheckMaterial(HRESULT hr) { if (FAILED(hr)) throw hr; }
inline HRESULT ValidateBitmap(const GD_Bitmap* b) {
    if (!b || b->size != sizeof(GD_Bitmap)) return E_INVALIDARG;
    if (b->version != 1) return E_NOINTERFACE;
    if (!b->pixels || b->reserved || !b->width || !b->height || b->width > 8192 || b->height > 8192
        || uint64_t(b->width)*b->height > 16777216 || b->stride != b->width*4) return E_INVALIDARG;
    return S_OK;
}
inline HRESULT ValidateMaterial(const GD_Material* m) {
    if (!m || m->size != sizeof(GD_Material)) return E_INVALIDARG;
    if (m->version != 1) return E_NOINTERFACE;
    if (!std::isfinite(m->dipWidth) || !std::isfinite(m->dipHeight) || m->dipWidth < 1 || m->dipHeight < 1
        || !std::isfinite(m->strength) || m->strength < 0 || m->strength > 1
        || !std::isfinite(m->clarity) || m->clarity < 0 || m->clarity > 1
        || !std::isfinite(m->tintOpacity) || m->tintOpacity < 0 || m->tintOpacity > 1
        || m->kind > 1 || m->dark > 1 || m->reserved1 || m->reserved2 || m->reserved3) return E_INVALIDARG;
    return S_OK;
}
class ProductionMaterial {
    template<typename T> using Ptr = Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D11Device> device;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> pixel;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11ShaderResourceView> wallpaper;
    std::array<Ptr<ID3D11ShaderResourceView>,2> views;
    std::array<Ptr<ID3D11RenderTargetView>,2> targets;
    uint32_t width{},height{};
    bool filtered{};
    GD_Material filteredMaterial{};
    uint64_t filterRuns{};
public:
    uint32_t Width() const { return width; }
    uint32_t Height() const { return height; }
    uint64_t FilterRuns() const { return filterRuns; }
    ID3D11ShaderResourceView* Filter(ID3D11DeviceContext* context,const GD_Material& m) {
        if(!filtered || std::memcmp(&filteredMaterial,&m,sizeof(m))!=0) {
            Render(context,targets[1].Get(),m);filteredMaterial=m;filtered=true;
        }
        return views[1].Get();
    }
    explicit ProductionMaterial(ID3D11Device* source) : device(source) {
        constexpr char shader[]="float4 VS(uint v:SV_VertexID):SV_Position {return float4(v==2?3:-1,v==1?3:-1,0,1);}";
        Ptr<ID3DBlob> vs,ps,error;
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Fullscreen",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));
        error.Reset();
        CheckMaterial(D3DCompile(GD_StaticMaterialShader,sizeof(GD_StaticMaterialShader)-1,"ProductionMaterial",nullptr,nullptr,"PS","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC cb{}; cb.ByteWidth=32;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
        CheckMaterial(device->CreateRasterizerState(&rs,&raster));
    }
    void Upload(const GD_Bitmap& b) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=b.width;desc.Height=b.height;desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{b.pixels,b.stride,0};
        Ptr<ID3D11Texture2D> input;Ptr<ID3D11ShaderResourceView> uploaded;
        CheckMaterial(device->CreateTexture2D(&desc,&data,&input));
        CheckMaterial(device->CreateShaderResourceView(input.Get(),nullptr,&uploaded));
        std::array<Ptr<ID3D11ShaderResourceView>,2> nextViews;
        std::array<Ptr<ID3D11RenderTargetView>,2> nextTargets;
        desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags|=D3D11_BIND_RENDER_TARGET;
        for(size_t i=0;i<2;i++) {
            Ptr<ID3D11Texture2D> texture;
            CheckMaterial(device->CreateTexture2D(&desc,nullptr,&texture));
            CheckMaterial(device->CreateShaderResourceView(texture.Get(),nullptr,&nextViews[i]));
            CheckMaterial(device->CreateRenderTargetView(texture.Get(),nullptr,&nextTargets[i]));
        }
        wallpaper=std::move(uploaded);views=std::move(nextViews);targets=std::move(nextTargets);width=b.width;height=b.height;filtered=false;
    }
    void Render(ID3D11DeviceContext* context,ID3D11RenderTargetView* output,const GD_Material& m) {
        if(!wallpaper) throw HRESULT_FROM_WIN32(ERROR_NOT_READY);
        const double blurScale=m.clarity<=.5?1.5-m.clarity:2*(1-m.clarity);
        const double sigma=m.kind==1?(12+8*(1-m.clarity))*(.7+.3*m.strength):3.2*m.strength*blurScale;
        const double sx=sigma*width/m.dipWidth,sy=sigma*height/m.dipHeight;
        if(sx>128 || sy>128) throw HRESULT(E_INVALIDARG);
        filtered=false;
        const auto radius=[](double value,int pass) {
            if(value<.1)return 0;
            int lower=std::max(1,int(std::floor(std::sqrt(4*value*value+1))));if(lower%2==0)--lower;
            const int count=int(std::nearbyint((12*value*value-3*lower*lower-12*lower-9)/(-4.0*lower-4)));
            return (pass<count?lower-1:lower+1)/2;
        };
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};
        context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
        context->OMSetBlendState(nullptr,nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);
        ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);
        const float tint=(m.kind==1?(m.dark?24.f:246.f):(m.dark?18.f:250.f))/255;
        const float opacity=m.kind==1?.5f*(1-m.clarity):m.tintOpacity*m.strength;
        const auto draw=[&](ID3D11ShaderResourceView* input,ID3D11RenderTargetView* target,float mode,int r) {
            const float values[8]={float(width),float(height),float(r),0,m.kind==1?0.f:m.strength,tint,opacity,mode};
            context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
            context->OMSetRenderTargets(1,&target,nullptr);context->PSSetShaderResources(0,1,&input);context->Draw(3,0);
            ID3D11ShaderResourceView* empty=nullptr;context->PSSetShaderResources(0,1,&empty);context->OMSetRenderTargets(0,nullptr,nullptr);
        };
        draw(wallpaper.Get(),targets[0].Get(),0,0);
        for(int pass=0;pass<3;pass++) {
            draw(views[0].Get(),targets[1].Get(),1,radius(sx,pass));
            draw(views[1].Get(),targets[0].Get(),2,radius(sy,pass));
        }
        draw(views[0].Get(),output,3,0);
        CheckMaterial(device->GetDeviceRemovedReason());
        ++filterRuns;
    }
};
}
