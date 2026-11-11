#pragma once
#include "ProductionMaterial.h"
#include "DirectPresenter.h"
#include "WpfLensBody.h"
#include "CompactCorner.h"
#include <string>
#include <vector>
#include <numbers>
#include <algorithm>

namespace gd {
inline HRESULT ValidateGlass(const GD_GlassScene* s) {
    if(!s || s->size!=sizeof(GD_GlassScene))return E_INVALIDARG;
    if(s->version!=1 && s->version!=2)return E_NOINTERFACE;
    for(float v:{s->hostWidth,s->hostHeight,s->panelX,s->panelY,s->panelWidth,s->panelHeight,s->backdropX,s->backdropY,
        s->backdropWidth,s->backdropHeight,s->radius,s->strength,s->dispersion,s->innerGain,s->outerGain})
        if(!std::isfinite(v))return E_INVALIDARG;
    if(s->hostWidth<1 || s->hostHeight<1 || s->hostWidth>32768 || s->hostHeight>32768
        || s->panelWidth<.001f || s->panelHeight<.001f || s->panelWidth>32768 || s->panelHeight>32768
        || s->backdropWidth<1 || s->backdropHeight<1 || s->backdropWidth>32768 || s->backdropHeight>32768
        || std::abs(s->panelX)>131072 || std::abs(s->panelY)>131072 || std::abs(s->backdropX)>131072 || std::abs(s->backdropY)>131072
        || s->radius<0 || s->radius>32768 || s->strength<0 || s->strength>1
        || s->dispersion<0 || s->dispersion>1000 || s->innerGain<0 || s->innerGain>1000 || s->outerGain<0 || s->outerGain>1000
        || s->launchpad>1 || s->outline>(s->version==1?0u:1u) || s->reserved2)return E_INVALIDARG;
    return S_OK;
}
class DirectGlass {
    template<typename T> using Ptr=Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> pixel;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11SamplerState> sampler;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11BlendState> compositeBlend;
    Ptr<ID3D11Buffer> contour;
    Ptr<ID3D11ShaderResourceView> contourView;
    struct Point { float x,y; };
    std::vector<Point> curve,points;
    std::vector<CubicCorner> compactCurve;
    int compactSubdivisions{};
    int curveSegments{};
    GD_GlassScene lastScene{};
    uint32_t lastWidth{},lastHeight{};
    uint32_t leftCount{},rightCount{};
    static constexpr size_t MaxContourPoints=4096;

    void UpdateContour(ID3D11DeviceContext* context,uint32_t width,uint32_t height,const GD_GlassScene& s) {
        if(lastWidth==width && lastHeight==height && lastScene.hostWidth==s.hostWidth && lastScene.hostHeight==s.hostHeight
            && lastScene.panelX==s.panelX && lastScene.panelY==s.panelY && lastScene.panelWidth==s.panelWidth
            && lastScene.panelHeight==s.panelHeight && lastScene.radius==s.radius && lastScene.outline==s.outline)return;
        const double r=std::clamp(double(s.radius),0.0,std::min(double(s.panelWidth),double(s.panelHeight))*.5);
        const int segments=std::clamp(int(std::ceil(r*3)),64,256);
        if(s.outline==0 && curveSegments!=segments) {
            curve.clear();curve.reserve(size_t(segments));
            for(int i=1;i<=segments;i++) {
                const double t=double(i)/segments,smooth=t*t*(3-2*t),angle=smooth*std::numbers::pi/2;
                curve.push_back(i==segments?Point{1,0}:Point{float(std::pow(std::sin(angle),2/2.6)),float(-std::pow(std::cos(angle),2/2.6))});
            }
            curveSegments=segments;
        }
        points.clear();points.reserve(MaxContourPoints);
        const float scaleX=width/s.hostWidth,scaleY=height/s.hostHeight;
        // Match the accepted polyline's device-space fixed point conversion.
        // Only a small vertex buffer is uploaded when geometry changes; no pixel map.
        const auto addPixel=[&](double x,double y) {
            if(points.size()==MaxContourPoints)throw HRESULT(E_INVALIDARG);
            points.push_back({float(x),float(y)});
        };
        const auto add=[&](double x,double y) {
            const float px=float(x)*scaleX,py=float(y)*scaleY;
            addPixel(std::floor(px*16+.5f)/16,std::floor(py*16+.5f)/16);
        };
        const double left=s.panelX,top=s.panelY,right=left+s.panelWidth,bottom=top+s.panelHeight;
        uint32_t nextLeft{},nextRight{};
        if(s.outline==1) {
            int subdivisions=1;
            while(r>256.0*subdivisions*subdivisions && subdivisions<32)subdivisions*=2;
            if(compactSubdivisions!=subdivisions){compactCurve=BuildCompactCorner(subdivisions);compactSubdivisions=subdivisions;}
            const auto corner=[&](double cx,double cy,int orientation) {
                const auto device=[&](CurvePoint p) {
                    const CurvePoint q=orientation==0?CurvePoint{-p.x,p.y}:orientation==1?CurvePoint{p.y,p.x}:
                        orientation==2?CurvePoint{p.x,p.y}:CurvePoint{-p.y,p.x};
                    return CurvePoint{float(cx+q.x*r)*scaleX,float(cy+q.y*r)*scaleY};
                };
                for(const auto& c:compactCurve)FlattenCompact(device(c.start),device(c.c1),device(c.c2),device(c.end),addPixel);
            };
            add(left+r,top);corner(left+r,top+r,0);add(left,bottom-r);corner(left+r,bottom-r,1);
            nextLeft=uint32_t(points.size());
            add(right-r,top);corner(right-r,top+r,2);add(right,bottom-r);corner(right-r,bottom-r,3);
            nextRight=uint32_t(points.size())-nextLeft;
        } else {
            for(int side=0;side<2;side++) {
                if(side==0) {
                    add(left+r,top);
                    for(const auto p:curve)add(left+r-p.x*r,top+r+p.y*r);
                    add(left,bottom-r);
                    for(const auto p:curve)add(left+r+p.y*r,bottom-r+p.x*r);
                } else {
                    add(right-r,top);
                    for(const auto p:curve)add(right-r+p.x*r,top+r+p.y*r);
                    add(right,bottom-r);
                    for(const auto p:curve)add(right-r-p.y*r,bottom-r+p.x*r);
                }
            }
            nextLeft=nextRight=uint32_t(points.size()/2);
        }
        const D3D11_BOX box{0,0,0,UINT(points.size()*sizeof(Point)),1,1};
        context->UpdateSubresource(contour.Get(),0,&box,points.data(),0,0);
        // Commit counts with the uploaded geometry. Capacity rejection must leave
        // the previous GPU contour and its cached counts usable together.
        leftCount=nextLeft;rightCount=nextRight;
        lastScene=s;lastWidth=width;lastHeight=height;
    }
public:
    explicit DirectGlass(ID3D11Device* device,bool shadowMask=false) {
        const std::string shader=std::string(R"hlsl(
Texture2D<float4> Source:register(t0);
Buffer<float2> Contour:register(t1);
SamplerState LinearClamp:register(s0);
cbuffer Scene:register(b0){float4 Panel;float4 Material;float4 Dispersion;float4 Profile;float4 Host;float4 Backdrop;};
struct Vertex {float4 position:SV_Position;float2 uv:TEXCOORD;};
Vertex VS(uint v:SV_VertexID) {
    float2 uv=float2(v==1 || v==4 || v==5?1:0,v==2 || v==3 || v==4?1:0);
    Vertex result;result.uv=uv;
    result.position=float4((Backdrop.xy+uv*Backdrop.zw)/Host.xy*float2(2,-2)+float2(-1,1),0,1);
    return result;
}
)hlsl")+GD_WpfLensBody+R"hlsl(
float4 Edge(float y,uint offset) {
    uint count=uint(offset==0?Profile.y:Profile.z),lo=0,hi=count;
    [loop]while(lo<hi){uint mid=(lo+hi)/2;if(Contour[offset+mid].y<=y)lo=mid+1;else hi=mid;}
    if(lo==0 || lo>=count)return float4(offset==0?1e10:-1e10,0,0,0);
    float2 a=Contour[offset+lo-1],b=Contour[offset+lo];
    float slope=(b.x-a.x)/(b.y-a.y);
    return float4(a.x+(y-a.y)*slope,slope,a.y,b.y);
}
float Coverage(float2 pixel) {
    float4 l=Edge(pixel.y+.5,0),r=Edge(pixel.y+.5,uint(Profile.y));
    // Long edges use the same linear trapezoidal falloff as WPF's hardware
    // rasterizer; rows containing a vertex use its 8x8 complex-scan coverage.
    if(max(ceil(l.z*8)/8,ceil(r.z*8)/8)<=pixel.y && min(ceil(l.w*8)/8,ceil(r.w*8)/8)>=pixel.y+1)
        return saturate(.5+(pixel.x+.5-l.x)/(1+abs(l.y)))*saturate(.5+(r.x-pixel.x-.5)/(1+abs(r.y)));
    float count=0;
    [unroll]for(uint row=0;row<8;row++) {
        float y=pixel.y+row/8.0;
        float left=Edge(y,0).x,right=Edge(y,uint(Profile.y)).x;
        float first=clamp(ceil((left-pixel.x)*8),0,8);
        float last=clamp(ceil((right-pixel.x)*8),0,8);
        count+=max(0,last-first);
    }
    return count/64;
}
float4 PS(float4 position:SV_Position,float2 uv:TEXCOORD):SV_Target {
    float2 p=(uv-Panel.xy)*Material.xy;
    float2 panelSize=Panel.zw*Material.xy;
    float2 side=min(p,panelSize-p);
    float radius=max(.001,min(Material.z,min(panelSize.x,panelSize.y)*.5));
    float guard=2*max(Host.x/Host.z,Host.y/Host.w);
    // Exact rectangular regions: keep the accepted contour scan for all corners
    // and edge pixels, and avoid its implicit-distance work elsewhere.
    [branch]if(min(side.x,side.y)<=-guard)return float4(0,0,0,0);
    [branch]if((side.x>=radius+guard && side.y>=guard) || (side.y>=radius+guard && side.x>=guard))
        return Profile.w>0.5?Source.Sample(LinearClamp,uv):Lens(uv);
    float2 corner=saturate(1-side/radius);
    float2 gradient=2.6*pow(max(corner,.00001),1.6)/radius;
    float depth=min(side.x,side.y);
    if(side.x<radius && side.y<radius)
        depth=(1-pow(corner.x,2.6)-pow(corner.y,2.6))/max(length(gradient),.00001);
    // Outside the rectangle the saturated implicit corner cannot describe the
    // extension of its straight sides; the rectangular distance bounds coverage.
    if(side.x<0 || side.y<0)depth=min(depth,min(side.x,side.y));
    float pixelDip=max(Host.x/Host.z,Host.y/Host.w);
    float coverage=depth>=2*pixelDip?1:depth<=-2*pixelDip?0:Coverage(position.xy-.5);
    return coverage<=0?float4(0,0,0,0):(Profile.w>0.5?Source.Sample(LinearClamp,uv):Lens(uv))*coverage;
}
)hlsl";
        const std::string program=shader+R"hlsl(
float4 ShadowPS(float4 position:SV_Position,float2 uv:TEXCOORD):SV_Target {
    float4 source=Source.Load(int3(position.xy,0))*Material.w;
    float2 p=(uv-Panel.xy)*Material.xy;
    float2 size=Panel.zw*Material.xy;
    float2 side=min(p,size-p);
    float guard=2*max(Host.x/Host.z,Host.y/Host.w);
    float radius=min(Material.z,min(size.x,size.y)*.5);
    // These rectangles are strictly inside/outside every supported contour.
    // Keep the exact WPF coverage scan for the whole rounded-corner region and
    // a two-pixel border; no approximation or reduced sampling at the edge.
    [branch]if(min(side.x,side.y)<=-guard)return source;
    [branch]if((side.x>=radius+guard && side.y>=guard) ||
               (side.y>=radius+guard && side.x>=guard))return float4(0,0,0,0);
    return source*(1-Coverage(position.xy-.5));
}
)hlsl";
        Ptr<ID3DBlob> vs,ps,error;
        CheckMaterial(D3DCompile(shader.data(),shader.size(),"ProductionLens",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));
        error.Reset();
        CheckMaterial(D3DCompile(program.data(),program.size(),"ProductionLens",nullptr,nullptr,shadowMask?"ShadowPS":"PS","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=96;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
        CheckMaterial(device->CreateSamplerState(&sd,&sampler));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
        CheckMaterial(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC blend{};auto& b=blend.RenderTarget[0];b.BlendEnable=TRUE;
        b.SrcBlend=b.SrcBlendAlpha=D3D11_BLEND_ONE;b.DestBlend=b.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
        b.BlendOp=b.BlendOpAlpha=D3D11_BLEND_OP_ADD;b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        CheckMaterial(device->CreateBlendState(&blend,&compositeBlend));
        D3D11_BUFFER_DESC edges{};edges.ByteWidth=UINT(MaxContourPoints*sizeof(Point));edges.Usage=D3D11_USAGE_DEFAULT;edges.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        CheckMaterial(device->CreateBuffer(&edges,nullptr,&contour));
        D3D11_SHADER_RESOURCE_VIEW_DESC view{};view.Format=DXGI_FORMAT_R32G32_FLOAT;view.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;view.Buffer.NumElements=UINT(MaxContourPoints);
        CheckMaterial(device->CreateShaderResourceView(contour.Get(),&view,&contourView));
    }
    void Render(ID3D11DeviceContext* context,ID3D11ShaderResourceView* input,ID3D11RenderTargetView* output,
        uint32_t width,uint32_t height,const GD_GlassScene& s,bool frosted=false,bool composite=false) {
        UpdateContour(context,width,height,s);
        const float values[24]={
            (s.panelX-s.backdropX)/s.backdropWidth,(s.panelY-s.backdropY)/s.backdropHeight,s.panelWidth/s.backdropWidth,s.panelHeight/s.backdropHeight,
            s.backdropWidth,s.backdropHeight,s.radius,s.strength,s.dispersion,s.innerGain,s.outerGain,0,float(s.launchpad),float(leftCount),float(rightCount),frosted?1.f:0.f,
            s.hostWidth,s.hostHeight,float(width),float(height),s.backdropX,s.backdropY,s.backdropWidth,s.backdropHeight};
        context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
        ID3D11Buffer* cb=constants.Get();ID3D11SamplerState* ss=sampler.Get();
        context->VSSetConstantBuffers(0,1,&cb);context->PSSetConstantBuffers(0,1,&cb);context->PSSetSamplers(0,1,&ss);
        ID3D11ShaderResourceView* inputs[2]={input,contourView.Get()};context->PSSetShaderResources(0,2,inputs);context->OMSetRenderTargets(1,&output,nullptr);
        context->OMSetBlendState(composite?compositeBlend.Get():nullptr,nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);
        const float transparent[4]={0,0,0,0};if(!composite)context->ClearRenderTargetView(output,transparent);context->Draw(6,0);
        ID3D11ShaderResourceView* empty[2]={nullptr,nullptr};context->PSSetShaderResources(0,2,empty);context->OMSetRenderTargets(0,nullptr,nullptr);
    }
};
}
