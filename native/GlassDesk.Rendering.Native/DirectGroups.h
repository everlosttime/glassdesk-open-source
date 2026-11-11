#pragma once
#include "ProductionMaterial.h"
#include "DirectPresenter.h"
#include <vector>
#include <algorithm>
#include <cmath>
#include "GroupShadow.h"
#include <memory>

namespace gd {
// Presenter-local GPU targets only. The session owns and serializes the device.
// Logical texture bytes exclude pipeline, allocation granularity and driver memory.
class DirectGroups {
    template<typename T> using Ptr=Microsoft::WRL::ComPtr<T>;
    using Surface=GroupSurface;
    struct Slot {Surface color,mask;float opacity{1};bool masked{};D3D11_RECT bounds{};};
    Ptr<ID3D11Device> device;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> pixel;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11BlendState> blend;
    std::vector<Slot> slots;
    uint32_t depth{};
    uint64_t colorBytes{},maskBytes{},allocations{},composites{};
    Surface effectA,effectB;
    std::unique_ptr<GroupShadow> shadow;
    uint64_t effectBytes{},effectAllocations{};
    static constexpr uint32_t MaximumDepth=16;
    static constexpr uint64_t Budget=128ull*1024*1024;

    Surface Create(uint32_t w,uint32_t h) {
        const uint64_t bytes=uint64_t(w)*h*4;
        if(bytes>Budget-colorBytes-maskBytes-effectBytes)throw HRESULT(E_OUTOFMEMORY);
        D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
        desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        Ptr<ID3D11Texture2D> texture;CheckMaterial(device->CreateTexture2D(&desc,nullptr,&texture));
        Surface result;CheckMaterial(device->CreateRenderTargetView(texture.Get(),nullptr,&result.target));
        CheckMaterial(device->CreateShaderResourceView(texture.Get(),nullptr,&result.view));return result;
    }
public:
    explicit DirectGroups(ID3D11Device* source):device(source) {
        constexpr char shader[]=R"hlsl(
Texture2D<float4> Source:register(t0);Texture2D<float4> Mask:register(t1);
cbuffer Scene:register(b0){float Opacity;float HasMask;float2 Reserved;float4 Bounds;};
float4 VS(uint v:SV_VertexID):SV_Position{return float4(v==2?3:-1,v==1?3:-1,0,1);}
float4 PS(float4 position:SV_Position):SV_Target {
    int3 p=int3(int2(position.xy),0);
    if(any(position.xy<Bounds.xy) || any(position.xy>=Bounds.zw))discard;
    float alpha=HasMask>.5?Mask.Load(p).a:1;
    return Source.Load(p)*(Opacity*alpha);
}
)hlsl";
        Ptr<ID3DBlob> vs,ps,error;
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Groups",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Groups",nullptr,nullptr,"PS","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=32;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
        CheckMaterial(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC bd{};auto& b=bd.RenderTarget[0];b.BlendEnable=TRUE;
        b.SrcBlend=b.SrcBlendAlpha=D3D11_BLEND_ONE;b.DestBlend=b.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
        b.BlendOp=b.BlendOpAlpha=D3D11_BLEND_OP_ADD;b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        CheckMaterial(device->CreateBlendState(&bd,&blend));
    }
    uint32_t Depth()const{return depth;}
    const D3D11_RECT* Bounds()const{return depth?&slots[depth-1].bounds:nullptr;}
    ID3D11RenderTargetView* Current()const {
        if(!depth)return nullptr;const auto& slot=slots[depth-1];return slot.masked?slot.mask.target.Get():slot.color.target.Get();
    }
    void Push(ID3D11DeviceContext* context,uint32_t width,uint32_t height,const GD_GroupInfo& info,bool effectInput=false) {
        if(depth>=MaximumDepth)throw HRESULT(E_OUTOFMEMORY);
        if(depth==slots.size()) {
            Slot next;next.color=Create(width,height);slots.push_back(std::move(next));
            colorBytes+=uint64_t(width)*height*4;++allocations;
        }
        const D3D11_RECT parent=depth && !effectInput?slots[depth-1].bounds:D3D11_RECT{0,0,LONG(width),LONG(height)};
        auto& slot=slots[depth];slot.opacity=info.opacity;slot.masked=false;slot.bounds=parent;
        if(info.bounded) {
            slot.bounds.left=std::max(parent.left,LONG(std::floor(info.left)));slot.bounds.top=std::max(parent.top,LONG(std::floor(info.top)));
            slot.bounds.right=std::min(parent.right,LONG(std::ceil(info.right)));slot.bounds.bottom=std::min(parent.bottom,LONG(std::ceil(info.bottom)));
        }
        const float zero[4]{};context->ClearRenderTargetView(slot.color.target.Get(),zero);++depth;
    }
    HRESULT BeginMask(ID3D11DeviceContext* context,uint32_t width,uint32_t height) {
        if(!depth || slots[depth-1].masked)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        auto& slot=slots[depth-1];
        if(!slot.mask.target){slot.mask=Create(width,height);maskBytes+=uint64_t(width)*height*4;++allocations;}
        const float zero[4]{};context->ClearRenderTargetView(slot.mask.target.Get(),zero);slot.masked=true;return S_OK;
    }
    HRESULT Pop(ID3D11DeviceContext* context,ID3D11RenderTargetView* root,uint32_t width,uint32_t height) {
        if(!depth)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        const auto& slot=slots[depth-1];
        auto output=root;
        if(depth>1){const auto& parent=slots[depth-2];output=parent.masked?parent.mask.target.Get():parent.color.target.Get();}
        context->OMSetRenderTargets(0,nullptr,nullptr);
        const float values[8]={slot.opacity,slot.masked?1.f:0.f,0,0,float(slot.bounds.left),float(slot.bounds.top),float(slot.bounds.right),float(slot.bounds.bottom)};context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
        context->RSSetState(raster.Get());const D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);
        const float factors[4]{};context->OMSetBlendState(blend.Get(),factors,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        context->OMSetRenderTargets(1,&output,nullptr);context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);
        ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);
        ID3D11ShaderResourceView* input[2]={slot.color.view.Get(),slot.masked?slot.mask.view.Get():nullptr};context->PSSetShaderResources(0,2,input);
        context->Draw(3,0);ID3D11ShaderResourceView* empty[2]{};context->PSSetShaderResources(0,2,empty);context->OMSetRenderTargets(0,nullptr,nullptr);
        --depth;++composites;return S_OK;
    }
    void Discard(ID3D11DeviceContext* context){
        // An unfinished draw can leave a layer RTV bound to the shared context.
        // Drop that reference before Trim can report/reclaim its idle textures.
        if(depth)context->OMSetRenderTargets(0,nullptr,nullptr);depth=0;
    }
    HRESULT ApplyShadow(ID3D11DeviceContext* context,uint32_t width,uint32_t height,const GD_GroupShadow& info) {
        if(!depth || slots[depth-1].masked)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        if(info.opacity==0 && info.outputX==0 && info.outputY==0)return S_OK;
        if(!shadow)shadow=std::make_unique<GroupShadow>(device.Get());
        if(!effectA.target) {
            const auto bytes=uint64_t(width)*height*8;
            if(bytes>Budget-colorBytes-maskBytes-effectBytes)throw HRESULT(E_OUTOFMEMORY);
            auto a=Create(width,height),b=Create(width,height);effectA=std::move(a);effectB=std::move(b);effectBytes=bytes;effectAllocations+=2;
        }
        context->OMSetRenderTargets(0,nullptr,nullptr);shadow->Render(context,slots[depth-1].color,effectA,effectB,width,height,info);return S_OK;
    }
    GD_GroupEffectStats EffectStatistics()const{return {sizeof(GD_GroupEffectStats),1,effectBytes,effectAllocations,shadow?shadow->Passes():0};}
    HRESULT Trim(){if(depth)return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);slots.clear();effectA={};effectB={};colorBytes=maskBytes=effectBytes=0;return S_OK;}
    GD_GroupStats Statistics()const{return {sizeof(GD_GroupStats),1,depth,uint32_t(slots.size()),colorBytes,maskBytes,allocations,composites};}
};
}
