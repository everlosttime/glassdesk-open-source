#pragma once
#include "GroupSurface.h"
#include "DirectPresenter.h"
#include "ProductionMaterial.h"
#include <array>
#include <cmath>

namespace gd {
// WPF BlurEffect/DropShadow performance semantics (MIT; existing full notice):
// corrected Gaussian weights, 15 samples/pass, additive BGRA8 intermediates.
// Scratch surfaces are pooled/budgeted by DirectGroups on the existing device.
class GroupShadow {
    template<class T>using Ptr=Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D11VertexShader> vertex;
    Ptr<ID3D11PixelShader> blur,compose;
    Ptr<ID3D11Buffer> constants;
    Ptr<ID3D11RasterizerState> raster;
    Ptr<ID3D11BlendState> add;
    Ptr<ID3D11SamplerState> sampler;
    uint64_t passes{};
    void Pipeline(ID3D11DeviceContext* context,ID3D11RenderTargetView* target,uint32_t w,uint32_t h,ID3D11PixelShader* shader,bool additive) {
        context->OMSetRenderTargets(1,&target,nullptr);context->OMSetDepthStencilState(nullptr,0);
        context->OMSetBlendState(additive?add.Get():nullptr,nullptr,0xffffffff);
        const D3D11_VIEWPORT viewport{0,0,float(w),float(h),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(shader,nullptr,0);
        ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);
    }
    static void Unbind(ID3D11DeviceContext* context){ID3D11ShaderResourceView* none[2]{};context->PSSetShaderResources(0,2,none);context->OMSetRenderTargets(0,nullptr,nullptr);}
public:
    explicit GroupShadow(ID3D11Device* device) {
        constexpr char shader[]=R"hlsl(
Texture2D<float4> Source:register(t0);Texture2D<float4> Blurred:register(t1);SamplerState Linear:register(s0);
cbuffer Data:register(b0){float4 Size;float4 Tint;float4 Shift;float4 Weights[4];};
float4 VS(uint v:SV_VertexID):SV_Position{return float4(v==2?3:-1,v==1?3:-1,0,1);}
float4 Blur(float4 position:SV_Position):SV_Target {
    int2 p=int2(position.xy);float sum=0;
    [unroll]for(int i=0;i<15;i++) {
        int2 q=p+(Size.w>.5?int2(int(Size.z)+i,0):int2(0,int(Size.z)+i));
        sum+=Source.Load(int3(q,0)).a*Weights[i/4][i%4];
    }
    return sum.xxxx;
}
float4 Compose(float4 position:SV_Position):SV_Target {
    float4 source=Source.SampleLevel(Linear,(position.xy-Shift.zw)/Size.xy,0);
    float alpha=Blurred.SampleLevel(Linear,(position.xy-Shift.zw-Shift.xy)/Size.xy,0).a*Tint.a;
    return source+(1-source.a)*float4(Tint.rgb*alpha,alpha);
}
)hlsl";
        Ptr<ID3DBlob> vs,ps,com,error;
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"GroupShadow",nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"GroupShadow",nullptr,nullptr,"Blur","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error));error.Reset();
        CheckMaterial(D3DCompile(shader,sizeof(shader)-1,"GroupShadow",nullptr,nullptr,"Compose","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&com,&error));
        CheckMaterial(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        CheckMaterial(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&blur));CheckMaterial(device->CreatePixelShader(com->GetBufferPointer(),com->GetBufferSize(),nullptr,&compose));
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=112;cb.Usage=D3D11_USAGE_DEFAULT;cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;CheckMaterial(device->CreateBuffer(&cb,nullptr,&constants));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;CheckMaterial(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC bd{};auto& b=bd.RenderTarget[0];b.BlendEnable=TRUE;b.SrcBlend=b.DestBlend=b.SrcBlendAlpha=b.DestBlendAlpha=D3D11_BLEND_ONE;
        b.BlendOp=b.BlendOpAlpha=D3D11_BLEND_OP_ADD;b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;CheckMaterial(device->CreateBlendState(&bd,&add));
        D3D11_SAMPLER_DESC ss{};ss.Filter=D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;ss.AddressU=ss.AddressV=ss.AddressW=D3D11_TEXTURE_ADDRESS_BORDER;
        ss.MaxLOD=D3D11_FLOAT32_MAX;CheckMaterial(device->CreateSamplerState(&ss,&sampler));
    }
    uint64_t Passes()const{return passes;}
    void Render(ID3D11DeviceContext* context,GroupSurface& source,GroupSurface& a,GroupSurface& b,uint32_t width,uint32_t height,const GD_GroupShadow& info) {
        std::array<float,101> weights{};const auto radius=info.radius;
        if(!radius)weights[0]=1;
        else {
            double sum=0;const double sd=radius/3.0;
            for(uint32_t i=0;i<=radius;i++){weights[i]=float(1/(sd*std::sqrt(2*3.14159265358979323846))*std::exp(-double(i*i)/(2*sd*sd)));sum+=weights[i];if(i)sum+=weights[i];}
            const auto correction=float((1-sum)/(2*radius+1));for(uint32_t i=0;i<=radius;i++)weights[i]+=correction;
        }
        const float zero[4]{};
        for(int axis=0;axis<2;axis++) {
            auto& input=axis==0?source:a;auto& target=axis==0?a:b;
            context->ClearRenderTargetView(target.target.Get(),zero);Pipeline(context,target.target.Get(),width,height,blur.Get(),true);
            ID3D11ShaderResourceView* view=input.view.Get();context->PSSetShaderResources(0,1,&view);
            for(int start=-int(radius);start<=int(radius);start+=15) {
                float values[28]={float(width),float(height),float(start),axis==0?1.f:0.f};
                for(int i=0;i<15;i++)if(start+i<=int(radius))values[12+i]=weights[std::abs(start+i)];
                context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);context->Draw(3,0);++passes;
            }
            Unbind(context);
        }
        Pipeline(context,a.target.Get(),width,height,compose.Get(),false);
        const float values[28]={float(width),float(height),0,0,info.red,info.green,info.blue,info.opacity,info.offsetX,info.offsetY,info.outputX,info.outputY};
        context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
        ID3D11ShaderResourceView* views[2]={source.view.Get(),b.view.Get()};context->PSSetShaderResources(0,2,views);
        ID3D11SamplerState* ss=sampler.Get();context->PSSetSamplers(0,1,&ss);context->Draw(3,0);++passes;Unbind(context);
        std::swap(source,a);
    }
};
}
