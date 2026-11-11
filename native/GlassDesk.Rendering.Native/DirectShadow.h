#pragma once
#include "DirectPresenter.h"
#include "CompactCorner.h"
#include "DirectGlass.h"
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <wrl/client.h>

namespace gd {
inline HRESULT ValidateShadow(const GD_ShadowScene* s) {
    if(!s || s->size!=sizeof(GD_ShadowScene))return E_INVALIDARG;
    if(s->version!=1)return E_NOINTERFACE;
    if(s->reserved || s->outline>1 || (s->steps!=16 && s->steps!=32) || s->count>4 || (s->count && !s->layers))return E_INVALIDARG;
    for(float v:{s->hostWidth,s->hostHeight,s->panelX,s->panelY,s->panelWidth,s->panelHeight,s->radius,s->opacity})
        if(!std::isfinite(v))return E_INVALIDARG;
    if(s->hostWidth<1 || s->hostHeight<1 || s->hostWidth>32768 || s->hostHeight>32768
        || std::abs(s->panelX)>131072 || std::abs(s->panelY)>131072 || s->panelWidth<.001f || s->panelHeight<.001f
        || s->panelWidth>32768 || s->panelHeight>32768 || s->radius<0 || s->radius>32768 || s->opacity<0 || s->opacity>1)return E_INVALIDARG;
    for(uint32_t i=0;i<s->count;i++) {
        const auto& l=s->layers[i];
        for(float v:{l.extent,l.offset,l.opacity,l.red,l.green,l.blue,l.alpha})if(!std::isfinite(v))return E_INVALIDARG;
        if(l.reserved || l.extent<.001f || l.extent>1024 || std::abs(l.offset)>1024 || l.opacity<0 || l.opacity>1
            || l.red<0 || l.red>1 || l.green<0 || l.green>1 || l.blue<0 || l.blue>1 || l.alpha<0 || l.alpha>1)return E_INVALIDARG;
    }
    return S_OK;
}

// Direct2D uses the presenter's existing D3D11 device and swapchain. No second
// adapter, D3D9 surface, full-frame CPU bitmap or retained backbuffer reference.
class DirectShadow {
    template<class T> using Ptr=Microsoft::WRL::ComPtr<T>;
    Ptr<ID2D1Factory1> factory;
    Ptr<ID2D1Device> device;
    Ptr<ID2D1DeviceContext> context;
    Ptr<ID2D1SolidColorBrush> brush;
    Ptr<ID3D11Device> sourceDevice;
    Ptr<ID3D11ShaderResourceView> accumulationView;
    DirectGlass mask;
    Ptr<ID2D1Bitmap1> accumulation;
    uint32_t accumulationWidth{},accumulationHeight{};
    std::vector<Ptr<ID2D1PathGeometry>> contours;
    GD_ShadowScene key{};
    std::vector<GD_ShadowLayer> layerKey;
    uint64_t builds{},draws{},allocations{};
    static void Check(HRESULT h){if(FAILED(h))throw h;}

    Ptr<ID2D1PathGeometry> Shape(double x,double y,double width,double height,double radius,bool compact) {
        Ptr<ID2D1PathGeometry> path;Check(factory->CreatePathGeometry(&path));
        Ptr<ID2D1GeometrySink> sink;Check(path->Open(&sink));
        const double r=std::clamp(radius,0.,std::min(width,height)/2);
        const auto point=[](double px,double py){return D2D1::Point2F(float(px),float(py));};
        sink->BeginFigure(point(x+r,y),D2D1_FIGURE_BEGIN_FILLED);
        if(r==0) {
            sink->AddLine(point(x+width,y));sink->AddLine(point(x+width,y+height));sink->AddLine(point(x,y+height));
        } else {
            int subdivisions=1;while(r>256.*subdivisions*subdivisions && subdivisions<32)subdivisions*=2;
            const auto curves=compact?BuildCompactCorner(subdivisions):std::vector<CubicCorner>{};
            const double cx[]={x+width-r,x+width-r,x+r,x+r},cy[]={y+r,y+height-r,y+height-r,y+r};
            for(int q=0;q<4;q++) {
                const auto transform=[&](CurvePoint p) {
                    double a=p.x,b=p.y;
                    if(q==1){a=-p.y;b=p.x;}else if(q==2){a=-p.x;b=-p.y;}else if(q==3){a=p.y;b=-p.x;}
                    return point(cx[q]+r*a,cy[q]+r*b);
                };
                sink->AddLine(transform({0,-1}));
                if(compact)for(const auto& c:curves)sink->AddBezier(D2D1::BezierSegment(transform(c.c1),transform(c.c2),transform(c.end)));
                else {
                    const auto count=std::clamp(int(std::ceil(r*3)),64,256);
                    for(int i=1;i<=count;i++) {
                        const double t=double(i)/count,theta=t*t*(3-2*t)*3.14159265358979323846/2;
                        sink->AddLine(transform(i==count?CurvePoint{1,0}:CurvePoint{std::pow(std::sin(theta),2/2.6),-std::pow(std::cos(theta),2/2.6)}));
                    }
                }
            }
        }
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);Check(sink->Close());return path;
    }
    bool Matches(const GD_ShadowScene& s)const {
        if(!builds || key.panelWidth!=s.panelWidth || key.panelHeight!=s.panelHeight || key.radius!=s.radius
            || key.outline!=s.outline || key.steps!=s.steps || key.count!=s.count)return false;
        for(uint32_t i=0;i<s.count;i++)if(layerKey[i].extent!=s.layers[i].extent || layerKey[i].offset!=s.layers[i].offset)return false;
        return true;
    }
    void Build(const GD_ShadowScene& s) {
        if(Matches(s))return;
        std::vector<Ptr<ID2D1PathGeometry>> next;next.reserve(size_t(s.count)*s.steps);
        std::vector<GD_ShadowLayer> nextLayers;nextLayers.reserve(s.count);
        for(uint32_t i=0;i<s.count;i++) {
            const auto& l=s.layers[i];nextLayers.push_back(l);
            for(uint32_t step=s.steps;step;step--) {
                const double d=double(l.extent)*step/s.steps;
                next.push_back(Shape(-d,l.offset-d,s.panelWidth+2*d,s.panelHeight+2*d,s.radius+d,s.outline!=0));
            }
        }
        contours=std::move(next);layerKey=std::move(nextLayers);key=s;key.layers=nullptr;++builds;
    }
public:
    void ReleaseTarget(){accumulation.Reset();accumulationView.Reset();accumulationWidth=accumulationHeight=0;}
    explicit DirectShadow(ID3D11Device* source):sourceDevice(source),mask(source,true) {
        Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,IID_PPV_ARGS(&factory)));
        Ptr<IDXGIDevice> dxgi;Check(source->QueryInterface(IID_PPV_ARGS(&dxgi)));
        Check(factory->CreateDevice(dxgi.Get(),&device));Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&context));
        Check(context->CreateSolidColorBrush(D2D1::ColorF(0,0,0,0),&brush));
    }
    void Render(ID3D11RenderTargetView* output,ID3D11DeviceContext* d3d,const GD_ShadowScene& s,uint32_t width,uint32_t height) {
        Build(s);
        const auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96*width/s.hostWidth,96*height/s.hostHeight);
        d3d->OMSetRenderTargets(0,nullptr,nullptr);
        float oldDpiX{},oldDpiY{};if(accumulation)accumulation->GetDpi(&oldDpiX,&oldDpiY);
        if(!accumulation || accumulationWidth!=width || accumulationHeight!=height || oldDpiX!=properties.dpiX || oldDpiY!=properties.dpiY) {
            Ptr<ID2D1Bitmap1> next;
            auto offscreen=properties;offscreen.bitmapOptions=D2D1_BITMAP_OPTIONS_TARGET;
            D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
            Ptr<ID3D11Texture2D> texture;Check(sourceDevice->CreateTexture2D(&desc,nullptr,&texture));
            Ptr<IDXGISurface> surface;Check(texture.As(&surface));
            Check(context->CreateBitmapFromDxgiSurface(surface.Get(),&offscreen,&next));
            Ptr<ID3D11ShaderResourceView> view;Check(sourceDevice->CreateShaderResourceView(texture.Get(),nullptr,&view));
            accumulation=next;accumulationView=view;accumulationWidth=width;accumulationHeight=height;++allocations;
        }
        context->SetTarget(accumulation.Get());context->SetDpi(properties.dpiX,properties.dpiY);
        context->BeginDraw();context->Clear(D2D1::ColorF(0,0,0,0));
        const float px=s.hostWidth/width,py=s.hostHeight/height;
        const float r=std::min(s.radius,std::min(s.panelWidth,s.panelHeight)*.5f);
        // The final exact mask always removes this strictly interior rectangle.
        // Avoid blending every translucent contour across millions of invisible
        // interior pixels. Partition on physical pixel boundaries, without AA
        // seams; rounded corners and every visible shadow pixel remain untouched.
        const float left=std::clamp(float(std::ceil((s.panelX+r)/px)+2)*px,0.f,s.hostWidth);
        const float right=std::clamp(float(std::floor((s.panelX+s.panelWidth-r)/px)-2)*px,0.f,s.hostWidth);
        const float top=std::clamp(float(std::ceil((s.panelY+r)/py)+2)*py,0.f,s.hostHeight);
        const float bottom=std::clamp(float(std::floor((s.panelY+s.panelHeight-r)/py)-2)*py,0.f,s.hostHeight);
        const D2D1_RECT_F bands[]={D2D1::RectF(0,0,s.hostWidth,top),D2D1::RectF(0,bottom,s.hostWidth,s.hostHeight),
            D2D1::RectF(0,top,left,bottom),D2D1::RectF(right,top,s.hostWidth,bottom)};
        const bool partition=right>left && bottom>top;
        for(uint32_t band=0;band<(partition?4u:1u);band++) {
          context->SetTransform(D2D1::Matrix3x2F::Identity());
          context->PushAxisAlignedClip(partition?bands[band]:D2D1::RectF(0,0,s.hostWidth,s.hostHeight),D2D1_ANTIALIAS_MODE_ALIASED);
          context->SetTransform(D2D1::Matrix3x2F::Translation(s.panelX,s.panelY));
          size_t index=0;
          for(uint32_t i=0;i<s.count;i++) {
            const auto& l=s.layers[i];
            for(uint32_t step=s.steps;step;step--) {
                const double d=double(l.extent)*step/s.steps,previous=double(l.extent)*(step-1)/s.steps;
                const double falloff=std::exp(-4.5*previous*previous/(double(l.extent)*l.extent))-std::exp(-4.5*d*d/(double(l.extent)*l.extent));
                // Match WPF's per-brush alpha quantization and BGRA8 blend
                // target before applying the group's exterior clip/opacity.
                const float alpha=float(std::floor(255*l.alpha*l.opacity*falloff+.5)/255);
                brush->SetColor(D2D1::ColorF(l.red,l.green,l.blue,alpha));
                if(alpha>0)context->FillGeometry(contours[index].Get(),brush.Get());
                ++index;
            }
          }
          context->PopAxisAlignedClip();
        }
        auto result=context->EndDraw();context->SetTarget(nullptr);Check(result);
        // D2D flattens cubic clip masks differently. Reuse the accepted WPF
        // 28.4 contour + hardware coverage adapter for this final cut-out.
        GD_GlassScene clip{};clip.size=sizeof(clip);clip.version=2;clip.hostWidth=s.hostWidth;clip.hostHeight=s.hostHeight;
        clip.panelX=s.panelX;clip.panelY=s.panelY;clip.panelWidth=s.panelWidth;clip.panelHeight=s.panelHeight;
        clip.backdropWidth=s.hostWidth;clip.backdropHeight=s.hostHeight;clip.radius=s.radius;clip.strength=s.opacity;clip.outline=s.outline;
        mask.Render(d3d,accumulationView.Get(),output,width,height,clip);++draws;
    }
    GD_ShadowStats Statistics()const{return {sizeof(GD_ShadowStats),1,uint32_t(contours.size()),0,builds,draws,allocations,uint64_t(accumulationWidth)*accumulationHeight*4};}
};
}
