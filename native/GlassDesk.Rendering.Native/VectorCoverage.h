#pragma once
#include "CompactCorner.h"
#include "ProductionMaterial.h"
#include "DirectPresenter.h"
#include "DirectVectorBrush.h"
#include <vector>
#include <algorithm>

// The contour flattening, 28.4 conversion, 8x8 complex scans and trapezoidal
// falloff follow WPF's hardware rasterizer (MIT; existing full notice applies).
// CPU work emits scan intervals, never pixels. All pixel coverage and blending
// are computed on the presenter's GPU, with no readback or mapped texture.
namespace gd {
class VectorCoverage {
    template<class T>using Ptr=Microsoft::WRL::ComPtr<T>;
    struct V4 {float x,y,z,w;};
    struct Edge {float x,y,endX,endY;int winding;float At(float row)const{return x+(row-y)*(endX-x)/(endY-y);}float Slope()const{return (endX-x)/(endY-y);}};
    struct Crossing {float x;const Edge* edge;};
    Ptr<ID3D11Device> device;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> pixel;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11BlendState> blend;
    static constexpr size_t MaxData=4*1024*1024;
public:
    struct Prepared {
        std::vector<V4> rows,spans;
        Ptr<ID3D11Buffer> rowBuffer,spanBuffer;
        Ptr<ID3D11ShaderResourceView> rowView,spanView;
        GD_ImageDraw draw{};float hostWidth{},hostHeight{};uint32_t width{},height{};
        D3D11_RECT clip{};
        int firstRow{},rowCount{};float left{},right{};
        uint64_t Bytes()const{return uint64_t(rows.capacity()+spans.capacity()+rows.size()+spans.size())*sizeof(V4);}
    };
    explicit VectorCoverage(ID3D11Device* source):device(source) {
        constexpr char shader[]=R"hlsl(
Buffer<float4> Rows:register(t0);Buffer<float4> Spans:register(t1);
cbuffer Scene:register(b0){float4 Color;float4 Size;float4 Bounds;float4 BrushMap;float4 BrushShape;float4 BrushMode;float4 StopColors[8];float4 StopOffsets[2];};
float Offset(uint i){return StopOffsets[i/4][i%4];}
float4 Paint(float2 position) {
    if(BrushMode.x<.5)return Color;
    float2 uv=(position-BrushMap.xy)/BrushMap.zw;
    float2 axis=BrushShape.zw-BrushShape.xy;
    float t=BrushMode.x>1.5?length((uv-BrushShape.xy)/BrushShape.zw):dot(uv-BrushShape.xy,axis)/dot(axis,axis);
    float4 result=StopColors[uint(BrushMode.y)-1];
    [loop]for(uint i=1;i<uint(BrushMode.y);i++) {
        if(t<=Offset(i)){result=lerp(StopColors[i-1],StopColors[i],saturate((t-Offset(i-1))/(Offset(i)-Offset(i-1))));break;}
    }
    return float4(result.rgb*result.a,result.a)*BrushMode.z;
}
float4 VS(uint v:SV_VertexID):SV_Position {
    float2 uv=float2(v==1 || v==4 || v==5?1:0,v==2 || v==3 || v==4?1:0);
    return float4(lerp(Bounds.xy,Bounds.zw,uv)/Size.xy*float2(2,-2)+float2(-1,1),0,1);
}
float4 PS(float4 position:SV_Position):SV_Target {
    int row=int(position.y)-int(Size.z);if(row<0 || row>=int(Size.w))return 0;
    uint base=uint(row)*9;float4 descriptor=Rows[base];float coverage=0;
    if(descriptor.z>0) {
        [loop]for(uint i=0;i<uint(descriptor.y);i++) {
            float4 span=Spans[uint(descriptor.x)+i];
            coverage+=saturate(.5+(position.x-span.x)/(1+abs(span.z)))*saturate(.5+(span.y-position.x)/(1+abs(span.w)));
        }
    } else {
        [unroll]for(uint y=0;y<8;y++) {
            float4 range=Rows[base+1+y];
            [loop]for(uint i=0;i<uint(range.y);i++) {
                float4 span=Spans[uint(range.x)+i];
                float first=clamp(ceil((span.x-position.x+.5)*8),0,8);
                float last=clamp(ceil((span.y-position.x+.5)*8),0,8);
                coverage+=max(0,last-first)/64;
            }
        }
    }
    return Paint(position.xy)*saturate(coverage);
}
)hlsl";
        Ptr<ID3DBlob> vs,ps,error;
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"VectorCoverage",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"VectorCoverage",nullptr,nullptr,"PS","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=256;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;CheckMaterial(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC bs{};auto& rt=bs.RenderTarget[0];rt.BlendEnable=TRUE;rt.SrcBlend=rt.SrcBlendAlpha=D3D11_BLEND_ONE;
        rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        CheckMaterial(device->CreateBlendState(&bs,&blend));
    }
    void Upload(const std::vector<V4>& values,Ptr<ID3D11Buffer>& buffer,Ptr<ID3D11ShaderResourceView>& view) {
        const V4 empty{};const auto count=std::max(size_t(1),values.size());
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=UINT(count*sizeof(V4));desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{values.empty()?&empty:values.data(),0,0};CheckMaterial(device->CreateBuffer(&desc,&data,&buffer));
        D3D11_SHADER_RESOURCE_VIEW_DESC srv{};srv.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;srv.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;srv.Buffer.NumElements=UINT(count);
        CheckMaterial(device->CreateShaderResourceView(buffer.Get(),&srv,&view));
    }
    bool Prepare(Prepared& cached,const std::vector<GD_VectorSegment>& commands,const GD_VectorInfo& paint,
        const GD_ImageDraw& draw,const GD_ImageBatch& batch,uint32_t width,uint32_t height,const D3D11_RECT* bounds=nullptr) {
        const D3D11_RECT clip=bounds?*bounds:D3D11_RECT{0,0,LONG(width),LONG(height)};
        if(cached.width==width && cached.height==height && cached.hostWidth==batch.hostWidth && cached.hostHeight==batch.hostHeight
            && cached.clip.left==clip.left && cached.clip.top==clip.top && cached.clip.right==clip.right && cached.clip.bottom==clip.bottom
            && cached.draw.x==draw.x && cached.draw.y==draw.y && cached.draw.width==draw.width && cached.draw.height==draw.height)return false;
        Prepared next;std::vector<Edge> edges;std::vector<Crossing> active;
        const float sx=width/batch.hostWidth*draw.width/paint.width,sy=height/batch.hostHeight*draw.height/paint.height;
        const float tx=draw.x*width/batch.hostWidth,ty=draw.y*height/batch.hostHeight;
        const auto transform=[&](float x,float y){
            const float px=x*sx+tx,py=y*sy+ty;
            // Keep fixed-point HFD arithmetic bounded even for offscreen or
            // highly magnified untrusted geometry. Reject before any frame draw.
            if(!std::isfinite(px) || !std::isfinite(py) || std::abs(px)>1048576 || std::abs(py)>1048576)throw HRESULT(E_INVALIDARG);
            return CurvePoint{px,py};
        };
        const auto quantize=[](CurvePoint p){return CurvePoint{std::floor(float(p.x)*16+.5f)/16,std::floor(float(p.y)*16+.5f)/16};};
        CurvePoint first{},previous{},rawPrevious{};bool open=false,filled=false;
        std::vector<CurvePoint> contour;
        float top=float(height),bottom=0,left=float(width),right=0;
        const auto emit=[&](CurvePoint point) {
            if(filled && previous.y!=point.y) {
                Edge edge{float(previous.x),float(previous.y),float(point.x),float(point.y),1};
                if(edge.y>edge.endY){std::swap(edge.x,edge.endX);std::swap(edge.y,edge.endY);edge.winding=-1;}
                edges.push_back(edge);
                if(edges.size()>16384)throw HRESULT(E_INVALIDARG);
                top=std::min(top,edge.y);bottom=std::max(bottom,edge.endY);left=std::min({left,edge.x,edge.endX});right=std::max({right,edge.x,edge.endX});
            }
            previous=point;
        };
        const auto add=[&](CurvePoint point){if(contour.size()>=16386)throw HRESULT(E_INVALIDARG);contour.push_back(point);};
        const auto finish=[&]() {
            if(contour.empty())return;
            previous=contour[0];
            // WPF InitializeEdges collapses consecutive segments wholly beyond
            // the layer's horizontal clip. Their otherwise irrelevant endpoints
            // must not turn an interior trapezoid row into a complex 8x8 scan.
            // Match FixedPointPathEnumerate's 32-point batches, carrying its
            // last point into the next batch. Collapsing across a batch boundary
            // changes where trapezoidal strata stop, including visible AA.
            for(size_t base=0;base+1<contour.size();base+=31) {
                const auto end=std::min(base+32,contour.size());
                for(size_t i=base;i+1<end;i++) {
                    if(i+2<end) {
                        const auto a=contour[i],b=contour[i+1],c=contour[i+2];
                        if((a.x<clip.left && b.x<clip.left && c.x<clip.left)
                            || (a.x>clip.right && b.x>clip.right && c.x>clip.right)) {
                            contour[i+1]=a;continue;
                        }
                    }
                    emit(contour[i+1]);
                }
            }
            contour.clear();
        };
        for(const auto& command:commands) {
            if(command.operation==0){if(open){add(first);finish();}first=previous=quantize(transform(command.x1,command.y1));rawPrevious=transform(command.x1,command.y1);open=true;filled=command.flags!=0;add(first);}
            else if(command.operation==1){rawPrevious=transform(command.x1,command.y1);add(quantize(rawPrevious));}
            else if(command.operation==2){auto end=transform(command.x3,command.y3);FlattenCompact(rawPrevious,transform(command.x1,command.y1),transform(command.x2,command.y2),end,[&](double x,double y){add({x,y});});rawPrevious=end;}
            else{add(first);finish();open=false;}
        }
        if(open){add(first);finish();}
        next.firstRow=int(std::clamp(std::floor(top)-1,0.0f,float(height)));const auto last=int(std::clamp(std::ceil(bottom)+1,0.0f,float(height)));
        next.rowCount=std::max(0,last-next.firstRow);next.left=std::clamp(std::floor(left)-2,0.0f,float(width));next.right=std::clamp(std::ceil(right)+2,0.0f,float(width));
        next.rows.resize(size_t(next.rowCount)*9);active.reserve(edges.size());
        const auto intersections=[&](float y) {
            active.clear();for(const auto& edge:edges)if(y>=edge.y && y<edge.endY)active.push_back({edge.At(y),&edge});
            std::sort(active.begin(),active.end(),[](const auto& a,const auto& b){return a.x<b.x;});
        };
        const auto span=[&](float a,float b,float slopeA=0,float slopeB=0){if(next.spans.size()*sizeof(V4)+next.rows.size()*sizeof(V4)>=MaxData)throw HRESULT(E_OUTOFMEMORY);next.spans.push_back({a,b,slopeA,slopeB});};
        for(int row=next.firstRow;row<last;row++) {
            const auto base=size_t(row-next.firstRow)*9;intersections(float(row)+.5f);
            bool simple=!active.empty() && active.size()%2==0;
            for(const auto& edge:edges)if((std::ceil(edge.y*8)/8>row && std::ceil(edge.y*8)/8<row+1)
                || (std::ceil(edge.endY*8)/8>row && std::ceil(edge.endY*8)/8<row+1))simple=false;
            for(size_t i=0;simple && i<active.size();i++) {
                const auto& e=*active[i].edge;
                if(std::ceil(e.y*8)/8>row || std::ceil(e.endY*8)/8<row+1)simple=false;
                if(paint.fillRule && i%2==0 && e.winding==active[i+1].edge->winding)simple=false;
                if(i>0) {
                    const auto& a=*active[i-1].edge;const float gap=1+(std::abs(a.Slope())+std::abs(e.Slope()))*.5f;
                    if(e.At(float(row))-a.At(float(row))<gap || e.At(float(row)+1)-a.At(float(row)+1)<gap)simple=false;
                }
            }
            if(simple) {
                const auto offset=next.spans.size();for(size_t i=0;i<active.size();i+=2)span(active[i].x,active[i+1].x,active[i].edge->Slope(),active[i+1].edge->Slope());
                next.rows[base]={float(offset),float(next.spans.size()-offset),1,0};
            } else for(int sample=0;sample<8;sample++) {
                intersections(float(row)+sample/8.0f);const auto offset=next.spans.size();int winding=0;float start=0;
                for(const auto& crossing:active) {
                    const bool before=paint.fillRule?winding!=0:(winding&1)!=0;winding+=crossing.edge->winding;
                    const bool after=paint.fillRule?winding!=0:(winding&1)!=0;
                    if(!before && after)start=crossing.x;else if(before && !after)span(start,crossing.x);
                }
                next.rows[base+1+sample]={float(offset),float(next.spans.size()-offset),0,0};
            }
        }
        Upload(next.rows,next.rowBuffer,next.rowView);Upload(next.spans,next.spanBuffer,next.spanView);
        next.draw=draw;next.hostWidth=batch.hostWidth;next.hostHeight=batch.hostHeight;next.width=width;next.height=height;next.clip=clip;cached=std::move(next);return true;
    }
    void Render(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t width,uint32_t height,const Prepared& prepared,const GD_VectorInfo& paint,const VectorBrush& brush,float opacity) {
        if(!prepared.rowCount || prepared.left>=prepared.right || opacity==0)return;
        const float a=paint.fillA*opacity;
        float values[64]={paint.fillR*a,paint.fillG*a,paint.fillB*a,a,float(width),float(height),float(prepared.firstRow),float(prepared.rowCount),
            prepared.left,float(prepared.firstRow),prepared.right,float(prepared.firstRow+prepared.rowCount)};
        values[12]=prepared.draw.x*width/prepared.hostWidth;values[13]=prepared.draw.y*height/prepared.hostHeight;
        values[14]=prepared.draw.width*width/prepared.hostWidth;values[15]=prepared.draw.height*height/prepared.hostHeight;
        values[16]=brush.x1;values[17]=brush.y1;values[18]=brush.x2;values[19]=brush.y2;values[20]=float(brush.kind);values[21]=float(brush.count);values[22]=opacity;
        for(uint32_t i=0;i<brush.count;i++) {
            const auto& s=brush.stops[i];values[24+i*4]=s.red;values[25+i*4]=s.green;values[26+i*4]=s.blue;values[27+i*4]=s.alpha;values[56+i]=s.offset;
        }
        context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
        ID3D11Buffer* cb=constants.Get();context->VSSetConstantBuffers(0,1,&cb);context->PSSetConstantBuffers(0,1,&cb);
        ID3D11ShaderResourceView* views[]={prepared.rowView.Get(),prepared.spanView.Get()};context->PSSetShaderResources(0,2,views);
        context->OMSetRenderTargets(1,&target,nullptr);context->OMSetBlendState(blend.Get(),nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);context->Draw(6,0);
        ID3D11ShaderResourceView* empty[2]={nullptr,nullptr};context->PSSetShaderResources(0,2,empty);context->OMSetRenderTargets(0,nullptr,nullptr);
    }
};
}
