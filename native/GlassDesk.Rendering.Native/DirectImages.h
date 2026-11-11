#pragma once
#include "ProductionMaterial.h"
#include "DirectPresenter.h"
#include <unordered_map>
#include <algorithm>

namespace gd {
inline HRESULT ValidateImages(const GD_ImageBatch* b) {
    if(!b || b->size!=sizeof(GD_ImageBatch))return E_INVALIDARG;
    if(b->version!=1)return E_NOINTERFACE;
    if(b->reserved || b->count>256 || (b->count && !b->items) || !std::isfinite(b->hostWidth) || !std::isfinite(b->hostHeight)
        || b->hostWidth<1 || b->hostHeight<1 || b->hostWidth>32768 || b->hostHeight>32768)return E_INVALIDARG;
    for(uint32_t i=0;i<b->count;i++) {
        const auto& d=b->items[i];
        if(!d.key || d.reserved || d.reserved2)return E_INVALIDARG;
        for(float v:{d.x,d.y,d.width,d.height,d.opacity})if(!std::isfinite(v))return E_INVALIDARG;
        if(std::abs(d.x)>131072 || std::abs(d.y)>131072 || d.width<.001f || d.height<.001f || d.width>32768 || d.height>32768
            || d.opacity<0 || d.opacity>1)return E_INVALIDARG;
    }
    return S_OK;
}
inline HRESULT ValidateMappedImages(const GD_MappedImageBatch* b) {
    if(!b || b->size!=sizeof(GD_MappedImageBatch))return E_INVALIDARG;
    if(b->version!=1)return E_NOINTERFACE;
    GD_ImageBatch base{sizeof(GD_ImageBatch),1,0,b->reserved,b->hostWidth,b->hostHeight,nullptr};
    const auto header=ValidateImages(&base);if(FAILED(header))return header;
    if(b->count>256 || (b->count && !b->items))return E_INVALIDARG;
    for(uint32_t i=0;i<b->count;i++) {
        const auto& d=b->items[i];
        GD_ImageDraw pair[2]={d.geometry,{d.geometry.key,0,d.mapX,d.mapY,d.mapWidth,d.mapHeight,1,0}};
        base.count=2;base.items=pair;
        const auto valid=ValidateImages(&base);if(FAILED(valid))return valid;
    }
    return S_OK;
}
class DirectImages {
    template<typename T> using Ptr=Microsoft::WRL::ComPtr<T>;
    struct Surface {
        Ptr<ID3D11ShaderResourceView> view;
        Ptr<ID3D11RenderTargetView> target;
    };
    struct ScaleCache {
        uint32_t width{},height{};
        uint64_t used{};
        Surface horizontal,filtered;
        uint64_t Bytes(uint32_t sourceHeight)const{return uint64_t(width)*(sourceHeight+height)*4;}
    };
    struct Image {
        uint32_t width{},height{};
        Surface source;
        std::array<ScaleCache,2> cache;
        uint64_t SourceBytes()const{return uint64_t(width)*height*4;}
        uint64_t CacheBytes()const{return cache[0].Bytes(height)+cache[1].Bytes(height);}
    };
    std::unordered_map<uint32_t,Image> images;
    Ptr<ID3D11Device> device;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> drawPixel,filterPixel;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11SamplerState> sampler;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11BlendState> blend;
    uint64_t prefilterRuns{};
    uint64_t clock{};
    static constexpr uint64_t Budget=32*1024*1024;

    Surface CreateSurface(uint32_t w,uint32_t h,const GD_Bitmap* source=nullptr) {
        D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
        desc.Usage=source?D3D11_USAGE_IMMUTABLE:D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|(source?0:D3D11_BIND_RENDER_TARGET);
        D3D11_SUBRESOURCE_DATA data{};if(source){data.pSysMem=source->pixels;data.SysMemPitch=source->stride;}
        Ptr<ID3D11Texture2D> texture;CheckMaterial(device->CreateTexture2D(&desc,source?&data:nullptr,&texture));
        Surface result;CheckMaterial(device->CreateShaderResourceView(texture.Get(),nullptr,&result.view));
        if(!source)CheckMaterial(device->CreateRenderTargetView(texture.Get(),nullptr,&result.target));
        return result;
    }
    // WPF's sqrt(2) prefilter buckets preserve quality while keeping hover
    // animation within a bucket on the same texture. This is not a lower-quality
    // sampler. See WPF BaseMatrix.cpp ComputePrefilteredSize (MIT).
    static uint32_t FilterSize(uint32_t source,float output) {
        const float threshold=1/std::sqrt(2.0f),scale=output/source;
        if(scale>threshold)return source;
        if(output<=1)return 1;
        const auto exponent=std::floor(std::log(scale)/std::log(threshold));
        return std::min(source,uint32_t(std::ceil(source*std::pow(threshold,exponent))));
    }
    void Pipeline(ID3D11DeviceContext* context,ID3D11RenderTargetView* output,uint32_t width,uint32_t height,ID3D11PixelShader* ps) {
        context->OMSetRenderTargets(1,&output,nullptr);context->OMSetDepthStencilState(nullptr,0);
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
        const D3D11_RECT rect{0,0,LONG(width),LONG(height)};context->RSSetScissorRects(1,&rect);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(ps,nullptr,0);
        ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);
        ID3D11SamplerState* ss=sampler.Get();context->PSSetSamplers(0,1,&ss);
    }
    static void Unbind(ID3D11DeviceContext* context) {
        ID3D11ShaderResourceView* empty=nullptr;context->PSSetShaderResources(0,1,&empty);context->OMSetRenderTargets(0,nullptr,nullptr);
    }
    ID3D11ShaderResourceView* Prepare(ID3D11DeviceContext* context,Image& image,float width,float height) {
        const auto fw=FilterSize(image.width,width),fh=FilterSize(image.height,height);
        if(fw==image.width && fh==image.height)return image.source.view.Get();
        for(auto& cached:image.cache)if(cached.width==fw && cached.height==fh){cached.used=++clock;return cached.filtered.view.Get();}
        auto& cached=image.cache[image.cache[0].used<=image.cache[1].used?0:1];
        const uint64_t required=uint64_t(fw)*(image.height+fh)*4;
        auto stats=Statistics();
        if(stats.sourceBytes+stats.cacheBytes-cached.Bytes(image.height)+required>Budget)throw HRESULT(E_OUTOFMEMORY);
        auto horizontal=CreateSurface(fw,image.height),filtered=CreateSurface(fw,fh);
        context->OMSetBlendState(nullptr,nullptr,0xffffffff);
        const auto pass=[&](ID3D11ShaderResourceView* input,Surface& output,uint32_t ow,uint32_t oh,uint32_t sw,uint32_t sh,float axis) {
            const float values[12]={0,0,0,0,float(sw),float(sh),float(ow),float(oh),axis,0,0,0};
            context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
            Pipeline(context,output.target.Get(),ow,oh,filterPixel.Get());context->PSSetShaderResources(0,1,&input);context->Draw(3,0);Unbind(context);
        };
        pass(image.source.view.Get(),horizontal,fw,image.height,image.width,image.height,0);
        pass(horizontal.view.Get(),filtered,fw,fh,fw,image.height,1);
        cached.horizontal=std::move(horizontal);cached.filtered=std::move(filtered);cached.width=fw;cached.height=fh;cached.used=++clock;++prefilterRuns;
        return cached.filtered.view.Get();
    }
public:
    explicit DirectImages(ID3D11Device* source):device(source) {
        constexpr char shader[]=R"hlsl(
Texture2D<float4> Source:register(t0);SamplerState LinearClamp:register(s0);
cbuffer Scene:register(b0){float4 Rect;float4 Image;float4 Output;};
float4 VS(uint v:SV_VertexID):SV_Position{return float4(v==2?3:-1,v==1?3:-1,0,1);}
float4 Filter(float4 position:SV_Position):SV_Target {
    int2 p=int2(position.xy);bool vertical=Output.x>.5;
    float ratio=vertical?Image.y/Image.w:Image.x/Image.z;
    float start=(vertical?p.y:p.x)*ratio,end=start+ratio;
    // WIC Fant retains the first partial source sample with its residual
    // weight, then weights that residual again for the following output.
    // Transfer the difference to its neighbour, preserving constant colors.
    // A continuous box integral agrees only at integer filter ratios.
    int first=int(floor(start));float residual=ceil(start)-start;
    float correction=residual*(1-residual);
    float4 sum=0;
    [loop]for(int i=int(floor(start));i<int(ceil(end));i++) {
        float weight=max(0,min(end,float(i+1))-max(start,float(i)));
        if(i==first)weight-=correction;else if(i==first+1)weight+=correction;
        int2 sample=vertical?int2(p.x,clamp(i,0,int(Image.y)-1)):int2(clamp(i,0,int(Image.x)-1),p.y);
        sum+=Source.Load(int3(sample,0))*weight;
    }
    return sum/ratio;
}
float4 Draw(float4 position:SV_Position):SV_Target {
    float2 uv=(position.xy-Image.xy)/Image.zw;
    // DrawBitmap routes its parallelogram through WPF's hardware path AA.
    // Preserve its 28.4 vertices and 8x8 complex first/last scanlines; a
    // continuous box-area ramp differs visibly at real magnification scales.
    float4 box=floor(float4(Rect.xy,Rect.xy+Rect.zw)*16+.5)/16;
    float2 pixel=floor(position.xy);
    float firstY=clamp(ceil((box.y-pixel.y)*8),0,8),lastY=clamp(ceil((box.w-pixel.y)*8),0,8);
    float coverage;
    if(firstY==0 && lastY==8)
        coverage=saturate(position.x+.5-box.x)*saturate(box.z-position.x+.5);
    else {
        float firstX=clamp(ceil((box.x-pixel.x)*8),0,8),lastX=clamp(ceil((box.z-pixel.x)*8),0,8);
        coverage=max(0,lastX-firstX)*max(0,lastY-firstY)/64;
    }
    return Source.Sample(LinearClamp,uv)*(Output.x*coverage);
}
)hlsl";
        Ptr<ID3DBlob> vs,draw,filter,error;
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Foreground",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Foreground",nullptr,nullptr,"Draw","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&draw,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"Foreground",nullptr,nullptr,"Filter","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&filter,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(draw->GetBufferPointer(),draw->GetBufferSize(),nullptr,&drawPixel));
        CheckMaterial(device->CreatePixelShader(filter->GetBufferPointer(),filter->GetBufferSize(),nullptr,&filterPixel));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=48;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;CheckMaterial(device->CreateSamplerState(&sd,&sampler));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;rs.ScissorEnable=TRUE;CheckMaterial(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC bs{};auto& rt=bs.RenderTarget[0];rt.BlendEnable=TRUE;rt.SrcBlend=rt.SrcBlendAlpha=D3D11_BLEND_ONE;
        rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        CheckMaterial(device->CreateBlendState(&bs,&blend));
    }
    GD_ImageStats Statistics()const {
        GD_ImageStats stats{sizeof(GD_ImageStats),1,uint32_t(images.size()),0,0,0,prefilterRuns};
        for(const auto& entry:images){stats.sourceBytes+=entry.second.SourceBytes();stats.cacheBytes+=entry.second.CacheBytes();}
        return stats;
    }
    void Set(uint32_t key,const GD_Bitmap& bitmap) {
        const auto old=images.find(key);const auto stats=Statistics();const uint64_t bytes=uint64_t(bitmap.width)*bitmap.height*4;
        const uint64_t replaced=old==images.end()?0:old->second.SourceBytes()+old->second.CacheBytes();
        if((old==images.end() && images.size()==256) || stats.sourceBytes+stats.cacheBytes-replaced+bytes>Budget)throw HRESULT(E_OUTOFMEMORY);
        Image fresh;fresh.width=bitmap.width;fresh.height=bitmap.height;fresh.source=CreateSurface(bitmap.width,bitmap.height,&bitmap);
        images.insert_or_assign(key,std::move(fresh));
    }
    HRESULT Remove(uint32_t key){return images.erase(key)?S_OK:S_FALSE;}
private:
    void RenderItem(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,
        float sx,float sy,const GD_ImageDraw& d,float mapX,float mapY,float mapWidth,float mapHeight) {
            auto& image=images.at(d.key);
            const D3D11_RECT clip{LONG(std::clamp(std::floor(d.x*sx),0.0f,float(width))),LONG(std::clamp(std::floor(d.y*sy),0.0f,float(height))),
                LONG(std::clamp(std::ceil((d.x+d.width)*sx),0.0f,float(width))),LONG(std::clamp(std::ceil((d.y+d.height)*sy),0.0f,float(height)))};
            if(d.opacity==0 || clip.right<=clip.left || clip.bottom<=clip.top)return;
            auto input=Prepare(context,image,mapWidth*sx,mapHeight*sy);
            Pipeline(context,target,width,height,drawPixel.Get());context->OMSetBlendState(blend.Get(),nullptr,0xffffffff);
            const float values[12]={d.x*sx,d.y*sy,d.width*sx,d.height*sy,mapX*sx,mapY*sy,mapWidth*sx,mapHeight*sy,d.opacity,0,0,0};
            context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
            context->PSSetShaderResources(0,1,&input);
            context->RSSetScissorRects(1,&clip);context->Draw(3,0);
            Unbind(context);
    }
public:
    void Render(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,const GD_ImageBatch& batch) {
        // Resolve every asset before drawing any foreground pixels.
        for(uint32_t i=0;i<batch.count;i++)if(!images.contains(batch.items[i].key))throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        for(uint32_t i=0;i<batch.count;i++) {
            const auto& d=batch.items[i];
            RenderItem(context,target,width,height,width/batch.hostWidth,height/batch.hostHeight,d,d.x,d.y,d.width,d.height);
        }
    }
    void RenderMapped(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,const GD_MappedImageBatch& batch) {
        for(uint32_t i=0;i<batch.count;i++)if(!images.contains(batch.items[i].geometry.key))throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        for(uint32_t i=0;i<batch.count;i++) {
            const auto& d=batch.items[i];
            RenderItem(context,target,width,height,width/batch.hostWidth,height/batch.hostHeight,d.geometry,d.mapX,d.mapY,d.mapWidth,d.mapHeight);
        }
    }
};
}
