#pragma once
#include "DirectPresenter.h"
#include "ProductionMaterial.h"
#include <dwrite.h>
#include <unordered_map>
#include <vector>
#include <array>

namespace gd {
// Adapted WPF grayscale gamma coefficients and contrast rules (MIT, see the
// existing complete WPF notice). Raster data comes from DirectWrite on CPU,
// once per resource scale; this never reads a GPU/window bitmap.
constexpr float GlyphGamma[13][4]={
    {0,0,0,0},{.0166f,-.0807f,.2227f,-.0751f},{.0350f,-.1760f,.4325f,-.1370f},
    {.0543f,-.2821f,.6302f,-.1876f},{.0739f,-.3963f,.8167f,-.2287f},{.0933f,-.5161f,.9926f,-.2616f},
    {.1121f,-.6395f,1.1588f,-.2877f},{.1300f,-.7649f,1.3159f,-.3080f},{.1469f,-.8911f,1.4644f,-.3234f},
    {.1627f,-1.0170f,1.6051f,-.3347f},{.1773f,-1.1420f,1.7385f,-.3426f},{.1908f,-1.2652f,1.8650f,-.3476f},
    {.2031f,-1.3864f,1.9851f,-.3501f}};
inline HRESULT ValidateGlyphRun(const GD_GlyphRunInfo* r) {
    if(!r || r->size!=sizeof(GD_GlyphRunInfo))return E_INVALIDARG;
    if(r->version!=1)return E_NOINTERFACE;
    if(!r->count || r->count>4096 || !r->fontPath || !r->indices || !r->advances || r->reserved || r->sideways>1 || r->simulations>3 || r->bidiLevel>61 || r->measuringMode>2)return E_INVALIDARG;
    const auto length=wcsnlen_s(r->fontPath,32768);if(!length || length==32768)return E_INVALIDARG;
    if(!std::isfinite(r->emSize) || r->emSize<.01f || r->emSize>1024)return E_INVALIDARG;
    for(uint32_t i=0;i<r->count;i++) {
        if(!std::isfinite(r->advances[i]) || std::abs(r->advances[i])>32768)return E_INVALIDARG;
        if(r->offsets)for(float n:{r->offsets[i].x,r->offsets[i].y})if(!std::isfinite(n) || std::abs(n)>32768)return E_INVALIDARG;
    }
    return S_OK;
}
inline HRESULT ValidateGlyphBatch(const GD_GlyphBatch* b) {
    if(!b || b->size!=sizeof(GD_GlyphBatch))return E_INVALIDARG;if(b->version!=1)return E_NOINTERFACE;
    if(b->reserved || b->count>256 || (b->count && !b->items))return E_INVALIDARG;
    for(float n:{b->hostWidth,b->hostHeight})if(!std::isfinite(n) || n<1 || n>32768)return E_INVALIDARG;
    for(uint32_t i=0;i<b->count;i++) {
        const auto& d=b->items[i];if(!d.key || d.reserved || d.reserved2)return E_INVALIDARG;
        for(float n:{d.x,d.y,d.scaleX,d.scaleY,d.opacity,d.red,d.green,d.blue,d.alpha})if(!std::isfinite(n))return E_INVALIDARG;
        if(std::abs(d.x)>131072 || std::abs(d.y)>131072 || d.scaleX<.001f || d.scaleY<.001f || d.scaleX>32 || d.scaleY>32)return E_INVALIDARG;
        for(float n:{d.opacity,d.red,d.green,d.blue,d.alpha})if(n<0 || n>1)return E_INVALIDARG;
    }
    return S_OK;
}
class DirectGlyphs {
    template<class T>using Ptr=Microsoft::WRL::ComPtr<T>;
    struct Raster {Ptr<ID3D11ShaderResourceView> view;RECT bounds{};float scaleX{},scaleY{};uint32_t gammaIndex{};uint64_t bytes{};HMONITOR monitor{};};
    struct Run {Ptr<IDWriteFontFace> face;std::vector<uint16_t> indices;std::vector<float> advances;std::vector<DWRITE_GLYPH_OFFSET> offsets;float em{};uint32_t bidi{},sideways{},measuring{};Raster raster;uint64_t Bytes()const{return indices.size()*sizeof(uint16_t)+advances.size()*sizeof(float)+offsets.size()*sizeof(DWRITE_GLYPH_OFFSET);}};
    Ptr<ID3D11Device> device;Ptr<IDWriteFactory> factory;
    Ptr<ID3D11VertexShader> vertex;Ptr<ID3D11PixelShader> pixel;Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11SamplerState> sampler;Ptr<ID3D11RasterizerState> rasterState;Ptr<ID3D11BlendState> blend;
    std::unordered_map<uint32_t,Run> runs;HWND window;
    uint64_t rasterizations{},draws{};
    static constexpr uint64_t SourceBudget=4*1024*1024,MaskBudget=32*1024*1024,SingleMask=4*1024*1024;
    uint64_t SourceBytes()const{uint64_t n=0;for(const auto& e:runs)n+=e.second.Bytes();return n;}
    uint64_t MaskBytes()const{uint64_t n=0;for(const auto& e:runs)n+=e.second.raster.bytes;return n;}
    void Prepare(Run& run,float sx,float sy) {
        auto monitor=MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST);
        if(run.raster.scaleX==sx && run.raster.scaleY==sy && run.raster.monitor==monitor)return;
        double advanceSpan=0;for(float advance:run.advances)advanceSpan+=std::abs(advance);
        if(!std::isfinite(sx) || !std::isfinite(sy) || run.em*std::max(sx,sy)>4096 || advanceSpan*sx>8192)throw HRESULT(E_OUTOFMEMORY);
        Ptr<IDWriteRenderingParams> parameters;CheckMaterial(factory->CreateMonitorRenderingParams(monitor,&parameters));
        auto mode=DWRITE_RENDERING_MODE_NATURAL;
        CheckMaterial(run.face->GetRecommendedRenderingMode(run.em,std::max(sx,sy),DWRITE_MEASURING_MODE(run.measuring),parameters.Get(),&mode));
        if(mode==DWRITE_RENDERING_MODE_OUTLINE || mode==DWRITE_RENDERING_MODE_DEFAULT)throw HRESULT(E_NOTIMPL);
        DWRITE_GLYPH_RUN glyph{run.face.Get(),run.em,UINT32(run.indices.size()),run.indices.data(),run.advances.data(),run.offsets.data(),BOOL(run.sideways),run.bidi};
        DWRITE_MATRIX matrix{sx,0,0,sy,0,0};Ptr<IDWriteGlyphRunAnalysis> analysis;
        CheckMaterial(factory->CreateGlyphRunAnalysis(&glyph,1,&matrix,mode,DWRITE_MEASURING_MODE(run.measuring),0,0,&analysis));
        // Embedded bitmap glyphs may coexist with antialiased glyphs in one run.
        // Never silently omit them by requesting only the ClearType alpha map.
        RECT bilevel{};CheckMaterial(analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_ALIASED_1x1,&bilevel));
        if(bilevel.left<bilevel.right && bilevel.top<bilevel.bottom)throw HRESULT(E_NOTIMPL);
        Raster next;CheckMaterial(analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_CLEARTYPE_3x1,&next.bounds));
        if(next.bounds.left>=next.bounds.right || next.bounds.top>=next.bounds.bottom){next.scaleX=sx;next.scaleY=sy;next.monitor=monitor;run.raster=std::move(next);++rasterizations;return;}
        const int64_t wide=int64_t(next.bounds.right)-next.bounds.left+2,tall=int64_t(next.bounds.bottom)-next.bounds.top+2;
        if(wide>2048 || tall>2048 || next.bounds.left==LONG_MIN || next.bounds.top==LONG_MIN || next.bounds.right==LONG_MAX || next.bounds.bottom==LONG_MAX)throw HRESULT(E_OUTOFMEMORY);
        --next.bounds.left;--next.bounds.top;++next.bounds.right;++next.bounds.bottom;
        const int width=next.bounds.right-next.bounds.left,height=next.bounds.bottom-next.bounds.top;
        if(width<1 || height<1 || width>2048 || height>2048 || uint64_t(width)*height*3>SingleMask)throw HRESULT(E_OUTOFMEMORY);
        next.bytes=uint64_t(width)*height*3;
        if(MaskBytes()-run.raster.bytes+next.bytes>MaskBudget)throw HRESULT(E_OUTOFMEMORY);
        std::vector<uint8_t> pixels(size_t(next.bytes));CheckMaterial(analysis->CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1,&next.bounds,pixels.data(),UINT32(pixels.size())));
        float gamma{},contrast{},level{};CheckMaterial(analysis->GetAlphaBlendParams(parameters.Get(),&gamma,&contrast,&level));
        next.gammaIndex=gamma<1?0:std::min(12u,UINT32((gamma-1)*10));
        std::array<uint8_t,256> table{};
        for(size_t i=0;i<table.size();i++){const float a=float(i)/255;table[i]=uint8_t(std::clamp(a*(contrast+1)/(a*contrast+1)*255+.5f,0.0f,255.0f));}
        for(auto& value:pixels)value=table[value];
        D3D11_TEXTURE2D_DESC desc{};desc.Width=UINT(width*3);desc.Height=UINT(height);desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_R8_UNORM;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{pixels.data(),UINT(width*3),0};Ptr<ID3D11Texture2D> texture;CheckMaterial(device->CreateTexture2D(&desc,&data,&texture));CheckMaterial(device->CreateShaderResourceView(texture.Get(),nullptr,&next.view));
        next.scaleX=sx;next.scaleY=sy;next.monitor=monitor;run.raster=std::move(next);++rasterizations;
    }
public:
    DirectGlyphs(ID3D11Device* source,IDWriteFactory* textFactory,HWND hwnd):device(source),factory(textFactory),window(hwnd) {
        constexpr char shader[]=R"hlsl(
Texture2D<float> Mask:register(t0);SamplerState LinearBorder:register(s0);
cbuffer Scene:register(b0){float4 Rect;float4 Color;float4 Output;float4 Gamma;};
struct Vertex{float4 position:SV_Position;float2 uv:TEXCOORD;};
Vertex VS(uint v:SV_VertexID){float2 uv=float2(v==1 || v==4 || v==5?1:0,v==2 || v==3 || v==4?1:0);Vertex r;r.uv=uv;r.position=float4((Rect.xy+uv*Rect.zw)/Output.xy*float2(2,-2)+float2(-1,1),0,1);return r;}
float4 PS(Vertex v):SV_Target{float a=Mask.Sample(LinearBorder,v.uv)*Color.a;a=saturate(a+a*(1-a)*(Gamma.x*a+Gamma.y));return float4(Color.rgb*a,a)*Output.z;}
)hlsl";
        Ptr<ID3DBlob> vs,ps,error;CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"ProductionGlyph",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));error.Reset();CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"ProductionGlyph",nullptr,nullptr,"PS","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=64;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_SAMPLER_DESC ss{};ss.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;ss.AddressU=ss.AddressV=ss.AddressW=D3D11_TEXTURE_ADDRESS_BORDER;ss.MaxLOD=D3D11_FLOAT32_MAX;CheckMaterial(device->CreateSamplerState(&ss,&sampler));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;CheckMaterial(device->CreateRasterizerState(&rs,&rasterState));
        D3D11_BLEND_DESC bs{};auto& rt=bs.RenderTarget[0];rt.BlendEnable=TRUE;rt.SrcBlend=rt.SrcBlendAlpha=D3D11_BLEND_ONE;rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;CheckMaterial(device->CreateBlendState(&bs,&blend));
    }
    void Set(uint32_t key,const GD_GlyphRunInfo& info) {
        auto found=runs.find(key);const auto previous=found==runs.end()?0:found->second.Bytes();
        if((found==runs.end() && runs.size()>=256) || SourceBytes()-previous+uint64_t(info.count)*14>SourceBudget)throw HRESULT(E_OUTOFMEMORY);
        Run next;Ptr<IDWriteFontFile> file;CheckMaterial(factory->CreateFontFileReference(info.fontPath,nullptr,&file));
        BOOL supported{};DWRITE_FONT_FILE_TYPE fileType{};DWRITE_FONT_FACE_TYPE faceType{};UINT32 faces{};CheckMaterial(file->Analyze(&supported,&fileType,&faceType,&faces));
        if(!supported || info.faceIndex>=faces)throw HRESULT(E_INVALIDARG);
        auto raw=file.Get();CheckMaterial(factory->CreateFontFace(faceType,1,&raw,info.faceIndex,DWRITE_FONT_SIMULATIONS(info.simulations),&next.face));
        for(uint32_t i=0;i<info.count;i++)if(info.indices[i]>=next.face->GetGlyphCount())throw HRESULT(E_INVALIDARG);
        next.indices.assign(info.indices,info.indices+info.count);next.advances.assign(info.advances,info.advances+info.count);next.offsets.resize(info.count);
        for(uint32_t i=0;info.offsets && i<info.count;i++)next.offsets[i]={info.offsets[i].x,info.offsets[i].y};
        next.em=info.emSize;next.bidi=info.bidiLevel;next.sideways=info.sideways;next.measuring=info.measuringMode;runs.insert_or_assign(key,std::move(next));
    }
    void Render(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,const GD_GlyphBatch& batch) {
        for(uint32_t i=0;i<batch.count;i++)if(!runs.contains(batch.items[i].key))throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        for(uint32_t i=0;i<batch.count;i++){const auto& d=batch.items[i];if(d.opacity*d.alpha>0)Prepare(runs.at(d.key),d.scaleX*width/batch.hostWidth,d.scaleY*height/batch.hostHeight);}
        for(uint32_t i=0;i<batch.count;i++) {
            const auto& d=batch.items[i];if(d.opacity*d.alpha==0)continue;auto& run=runs.at(d.key);Prepare(run,d.scaleX*width/batch.hostWidth,d.scaleY*height/batch.hostHeight);const auto& r=run.raster;if(!r.view)continue;
            const auto red=int(d.red*255+.5f),green=int(d.green*255+.5f),blue=int(d.blue*255+.5f);const float intensity=float((red+green+green+blue)>>2)/255;
            // Apply the device scale as one matrix multiplication. Multiplying
            // by pixel width then dividing changes half-pixel snapping in float.
            const float baselineX=d.x*(width/batch.hostWidth),baselineY=d.y*(height/batch.hostHeight);
            const auto& g=GlyphGamma[r.gammaIndex];const float values[16]={(run.measuring?std::floor(baselineX+.5f):baselineX)+r.bounds.left,std::floor(baselineY+.5f)+r.bounds.top,float(r.bounds.right-r.bounds.left),float(r.bounds.bottom-r.bounds.top),
                red/255.f,green/255.f,blue/255.f,float(int(d.alpha*255+.5f))/255,float(width),float(height),d.opacity,0,g[0]*intensity+g[1],g[2]*intensity+g[3],0,0};
            context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);ID3D11Buffer* cb=constants.Get();context->VSSetConstantBuffers(0,1,&cb);context->PSSetConstantBuffers(0,1,&cb);
            ID3D11ShaderResourceView* view=r.view.Get();context->PSSetShaderResources(0,1,&view);ID3D11SamplerState* ss=sampler.Get();context->PSSetSamplers(0,1,&ss);
            context->OMSetRenderTargets(1,&target,nullptr);context->OMSetBlendState(blend.Get(),nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(rasterState.Get());
            context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);context->Draw(6,0);view=nullptr;context->PSSetShaderResources(0,1,&view);context->OMSetRenderTargets(0,nullptr,nullptr);++draws;
        }
    }
    void ReleaseTarget(){for(auto& entry:runs)entry.second.raster={};}
    HRESULT Remove(uint32_t key){return runs.erase(key)?S_OK:S_FALSE;}
    GD_GlyphStats Statistics()const{return {sizeof(GD_GlyphStats),1,uint32_t(runs.size()),0,SourceBytes(),MaskBytes(),rasterizations,draws};}
};
}
