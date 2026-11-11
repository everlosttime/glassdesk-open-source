#pragma once
#include <d3d11.h>
#include <wrl/client.h>
namespace gd {
struct GroupSurface {
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> target;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
};
}
