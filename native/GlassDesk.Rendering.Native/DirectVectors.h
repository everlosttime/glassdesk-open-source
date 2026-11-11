#pragma once
#include "DirectPresenter.h"
#include "VectorCoverage.h"
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <unordered_map>
#include <cmath>

namespace gd {
inline HRESULT ValidateVector(const GD_VectorInfo* v) {
    if(!v || v->size!=sizeof(GD_VectorInfo))return E_INVALIDARG;
    if(v->version!=1)return E_NOINTERFACE;
    if(v->reserved || v->fillRule>1 || !v->count || v->count>4096 || !v->segments)return E_INVALIDARG;
    for(float n:{v->width,v->height,v->strokeWidth})if(!std::isfinite(n) || n<0 || n>32768)return E_INVALIDARG;
    if(v->width<.001f || v->height<.001f)return E_INVALIDARG;
    for(float n:{v->fillR,v->fillG,v->fillB,v->fillA,v->strokeR,v->strokeG,v->strokeB,v->strokeA})
        if(!std::isfinite(n) || n<0 || n>1)return E_INVALIDARG;
    if(v->strokeA>0 && v->strokeWidth>0)return E_NOTIMPL; // Upload the production device-space stroke outline as a filled vector.
    bool open=false;
    for(uint32_t i=0;i<v->count;i++) {
        const auto& s=v->segments[i];
        if(s.operation>3 || (s.flags && (s.operation!=0 || s.flags!=1)))return E_INVALIDARG;
        for(float n:{s.x1,s.y1,s.x2,s.y2,s.x3,s.y3})if(!std::isfinite(n) || std::abs(n)>131072)return E_INVALIDARG;
        if(s.operation==0)open=true;
        else if(!open)return E_INVALIDARG;
        else if(s.operation==3)open=false;
    }
    return S_OK;
}

class DirectVectors {
    struct Vector {GD_VectorInfo paint{};VectorBrush brush;std::vector<GD_VectorSegment> commands;VectorCoverage::Prepared prepared;};
    std::unordered_map<uint32_t,Vector> vectors;
    VectorCoverage coverage;
    uint64_t segmentBytes{},builds{},draws{},coverageBuilds{};
    static constexpr uint64_t SourceBudget=4*1024*1024,CacheBudget=32*1024*1024;
public:
    explicit DirectVectors(ID3D11Device* device):coverage(device){}
    void ReleaseTarget(){for(auto& entry:vectors)entry.second.prepared={};}
    void Set(uint32_t key,const GD_VectorInfo& info) {
        const auto old=vectors.find(key);const uint64_t previous=old==vectors.end()?0:uint64_t(old->second.paint.count)*sizeof(GD_VectorSegment);
        const uint64_t required=uint64_t(info.count)*sizeof(GD_VectorSegment);
        if(segmentBytes-previous+required>SourceBudget || (old==vectors.end() && vectors.size()>=256))throw HRESULT(E_OUTOFMEMORY);
        Vector next;next.paint=info;next.paint.segments=nullptr;next.commands.assign(info.segments,info.segments+info.count);
        vectors.insert_or_assign(key,std::move(next));segmentBytes=segmentBytes-previous+required;++builds;
    }
    HRESULT Remove(uint32_t key) {
        const auto found=vectors.find(key);if(found==vectors.end())return S_FALSE;
        segmentBytes-=uint64_t(found->second.paint.count)*sizeof(GD_VectorSegment);vectors.erase(found);return S_OK;
    }
    HRESULT SetBrush(uint32_t key,const GD_VectorBrushInfo& info) {
        const auto found=vectors.find(key);if(found==vectors.end())return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        found->second.brush.Set(info);return S_OK;
    }
    void Render(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,const GD_ImageBatch& batch,const D3D11_RECT* bounds=nullptr) {
        for(uint32_t i=0;i<batch.count;i++)if(!vectors.contains(batch.items[i].key))throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        for(uint32_t i=0;i<batch.count;i++) {
            const auto& draw=batch.items[i];if(draw.opacity==0)continue;auto& v=vectors.at(draw.key);
            if(coverage.Prepare(v.prepared,v.commands,v.paint,draw,batch,width,height,bounds))++coverageBuilds;
            uint64_t bytes=0;for(const auto& entry:vectors)bytes+=entry.second.prepared.Bytes();
            if(bytes>CacheBudget){for(auto& entry:vectors)entry.second.prepared={};throw HRESULT(E_OUTOFMEMORY);}
        }
        for(uint32_t i=0;i<batch.count;i++) {
            const auto& draw=batch.items[i];if(draw.opacity==0)continue;auto& v=vectors.at(draw.key);
            if(coverage.Prepare(v.prepared,v.commands,v.paint,draw,batch,width,height,bounds))++coverageBuilds;
            coverage.Render(context,target,width,height,v.prepared,v.paint,v.brush,draw.opacity);++draws;
        }
    }
    GD_VectorStats Statistics()const {
        uint64_t bytes=0;for(const auto& entry:vectors)bytes+=entry.second.prepared.Bytes();
        return {sizeof(GD_VectorStats),1,uint32_t(vectors.size()),0,segmentBytes,builds,draws,coverageBuilds,bytes};
    }
};
}
