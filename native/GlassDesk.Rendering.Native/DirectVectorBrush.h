#pragma once
#include "DirectPresenter.h"
#include <array>
#include <cmath>

namespace gd {
inline HRESULT ValidateVectorBrush(const GD_VectorBrushInfo* b) {
    if(!b || b->size!=sizeof(GD_VectorBrushInfo))return E_INVALIDARG;
    if(b->version!=1)return E_NOINTERFACE;
    if((b->kind!=1 && b->kind!=2) || b->count<2 || b->count>8 || !b->stops)return E_INVALIDARG;
    for(float v:{b->x1,b->y1,b->x2,b->y2})if(!std::isfinite(v) || std::abs(v)>32768)return E_INVALIDARG;
    if(b->kind==2 && (b->x2<.0001f || b->y2<.0001f))return E_INVALIDARG;
    const float dx=b->x2-b->x1,dy=b->y2-b->y1;
    if(b->kind==1 && dx*dx+dy*dy<1e-12f)return E_INVALIDARG;
    float previous=-1;
    for(uint32_t i=0;i<b->count;i++) {
        const auto& s=b->stops[i];if(s.reserved || !std::isfinite(s.offset) || s.offset<0 || s.offset>1 || s.offset<=previous)return E_INVALIDARG;
        for(float v:{s.red,s.green,s.blue,s.alpha})if(!std::isfinite(v) || v<0 || v>1)return E_INVALIDARG;
        previous=s.offset;
    }
    return S_OK;
}
struct VectorBrush {
    uint32_t kind{},count{};
    float x1{},y1{},x2{},y2{};
    std::array<GD_GradientStop,8> stops{};
    void Set(const GD_VectorBrushInfo& info) {
        kind=info.kind;count=info.count;x1=info.x1;y1=info.y1;x2=info.x2;y2=info.y2;
        for(uint32_t i=0;i<count;i++)stops[i]=info.stops[i];
    }
};
}
