# Native rendering prototype provenance

The calibration shader and native transport implementation are original GlassDesk code.
The native DLL uses MSVC 14.44.35207, the Windows SDK 10.0.26100 headers/import libraries,
the statically linked Microsoft C/C++ runtime (/MT), and Windows-provided D3D9Ex,
D3D11, DXGI and D3DCompiler APIs. No third-party rendering engine is bundled.

The compiler signer, ABI, target OS floor and native binary hash are recorded by
build/Build-NativeRendering.ps1. The external pinned toolchain installation has its
own payload receipt; toolchain binaries are not included in this probe package.

CompactCorner.h adapts the fixed-point hybrid forward-differencing portion of
.NET WPF's HfdBasis32 / Bezier32 (bezier.h and bezier.cpp) to retain the accepted
hardware outline. Copyright .NET Foundation and Contributors. MIT license:
[wpf-MIT.txt](wpf-MIT.txt). Upstream source:
https://github.com/dotnet/wpf/tree/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry

DirectImages.h adapts WPF's ComputePrefilteredSize bucket selection from
BaseMatrix.cpp. Copyright .NET Foundation and Contributors, under the same MIT
license above. The separable GPU area filter is original GlassDesk code. Source:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/common/shared/BaseMatrix.cpp

VectorCoverage.h adapts the hardware rasterizer's 28.4 coordinate conversion,
8x8 complex scans and trapezoidal edge-falloff rules to GPU scan-interval shading.
Copyright .NET Foundation and Contributors, under the same MIT license above.
The bounded interval cache and D3D11 pixel shader are GlassDesk code. Source:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/hw/hwrasterizer.cpp
The managed stroke adapter uses the public Geometry.GetWidenedPathGeometry API
in device coordinates, matching WPF's hardware stroke's transform order:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/hw/hwsurfrt.cpp

DirectImages.h also follows WPF DrawBitmap's path coverage: 28.4 rectangle
vertices, trapezoidal interior rows and 8x8 complex boundary rows. ForegroundSnap.cs
adapts CSnappingFrame's float device-space, nearest-guideline offset rules and
lower-guide tie behavior. Copyright .NET Foundation and Contributors, under the
same MIT license above. Sources:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/sw/swlib/aarasterizer.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/common/guidelinecollection.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/targets/ShapeClipperForFEB.cpp

DirectGlyphs.h adapts WPF's grayscale gamma coefficient table, contrast
renormalization and alpha-mask sampling/snap rules. Copyright .NET Foundation
and Contributors, under the same MIT license above. DirectWrite supplies glyph
coverage through Windows APIs; no font files are bundled. Sources:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/common/Gamma.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/common/EnhancedContrastTable.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/glyph/baseglyphpainter.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/glyphrunslave.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/hw/shaders/hlslTextShaders20A.fx

The self-contained managed probe includes .NET/WPF and existing GlassDesk UI/Core
assemblies. Their original notices are preserved in NOTICE.txt and licenses/.
Production optical materials are compiled into the existing UI assembly but are
not selected by the calibration probe.

GroupShadow.h and ForegroundCompiler.Effects.cs adapt WPF's performance-bias
Gaussian sampling weights and correction, scaled/floored kernel radius,
15-sample additive UNORM passes, scRGB shadow tint, source-over composition,
and effect input versus output transform/order semantics. Copyright .NET
Foundation and Contributors, under the same MIT license above. The D3D11
pipeline, bounded shared-device scratch pool and overscan adapter are GlassDesk
code. Sources:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/BlurEffect.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/DropShadowEffect.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/hw/ShaderAssemblies/Source/DropShadow.fx
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/uce/drawingcontext.cpp

ForegroundCompiler.Strokes.cs adapts CRectangle::WidenToShape's simple circular
rounded-rectangle stroke: float outer/inner bounds, radius expansion/contraction,
degenerate-hole rejection and alternate fill before the device transform.
Copyright .NET Foundation and Contributors, under the same MIT license above.
Other strokes continue through the public WPF widening API. Sources:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry/compactshapes.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry/CompactShapes.h

RetainedVisualDrawingSource.cs follows Visual.CalculateSubgraphBoundsInnerSpace's
leaf content-bounds and NaN normalization semantics; non-leaf subgraph bounds
remain public WPF queries. Copyright .NET Foundation and Contributors, under the
same MIT license above. Source:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/Visual.cs

ForegroundSnap.cs follows WPF CSnappingTask's float point/matrix conversion
before closest-guideline selection, including control-point traversal. Bitmap
coverage uses that guideline frame while keeping the brush sampling transform
independent, matching DrawBitmap/FillPath. Copyright .NET Foundation and
Contributors, under the same MIT license above. Sources:
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/targets/ShapeClipperForFEB.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/common/guidelinecollection.cpp
https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/hw/hwsurfrt.cpp

DirectImages.h's noninteger GPU prefilter residual weighting is GlassDesk code
derived from controlled WIC Fant impulse/image comparisons, rather than copied
codec source. It preserves the existing WPF reference output at fractional
prefilter ratios; no codec binary or CPU image prefilter is added to rendering.
