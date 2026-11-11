#pragma once
#include <vector>
#include <cmath>
#include <algorithm>
#include <cstdint>

namespace gd {
struct CurvePoint { double x,y; };
struct CubicCorner { CurvePoint start,c1,c2,end; };

// Same clamped spline, knots and endpoint constraints as CreateCompact in
// SquircleGlassGeometry.cs. Retain double precision until device conversion.
inline std::vector<CubicCorner> BuildCompactCorner(int subdivisions) {
    const double knots[]={0,.12,.45,.78,1};
    const double half=std::pow(.5,1/2.6);
    const size_t count=size_t(4*subdivisions);
    std::vector<double> x(count+1),y(count+1);
    for(size_t i=0;i<count;i++) {
        const auto interval=i/subdivisions;
        const double fraction=double(i%subdivisions)/subdivisions;
        x[i]=half*(knots[interval]+fraction*(knots[interval+1]-knots[interval]));
        y[i]=-std::pow(1-std::pow(x[i],2.6),1/2.6);
    }
    x[count]=half;y[count]=-half;
    const auto solve=[&](const std::vector<double>& ordinate) {
        std::vector<double> diagonal(count+1),upper(count+1),right(count+1),second(count+1);
        const double h=x[1]-x[0];
        diagonal[0]=2*h;upper[0]=h;right[0]=6*(ordinate[1]-ordinate[0])/h;
        for(size_t i=1;i<=count;i++) {
            const double previous=x[i]-x[i-1];
            if(i==count) {
                diagonal[i]=2*previous;right[i]=6*(1-(ordinate[i]-ordinate[i-1])/previous);
            } else {
                const double next=x[i+1]-x[i];
                diagonal[i]=2*(previous+next);upper[i]=next;
                right[i]=6*((ordinate[i+1]-ordinate[i])/next-(ordinate[i]-ordinate[i-1])/previous);
            }
            const double factor=previous/diagonal[i-1];
            diagonal[i]-=factor*upper[i-1];right[i]-=factor*right[i-1];
        }
        second[count]=right[count]/diagonal[count];
        for(size_t i=count;i-->0;)second[i]=(right[i]-upper[i]*second[i+1])/diagonal[i];
        return second;
    };
    auto second=solve(y),perturbed=y;perturbed[1]+=1;
    const auto response=solve(perturbed);
    y[1]-=second[0]/(response[0]-second[0]);second=solve(y);second[0]=0;
    std::vector<CubicCorner> curves(count*2);
    for(size_t i=0;i<count;i++) {
        const double h=x[i+1]-x[i],slope=(y[i+1]-y[i])/h;
        const double startSlope=slope-h*(2*second[i]+second[i+1])/6;
        const double endSlope=slope+h*(second[i]+2*second[i+1])/6;
        CurvePoint c1{x[i]+h/3,y[i]+h*startSlope/3},c2{x[i+1]-h/3,y[i+1]-h*endSlope/3};
        if(i==0)c1.y=c2.y=-1;
        if(i==count-1)c2={half-h/3,-half-h/3};
        curves[i]={{x[i],y[i]},c1,c2,{x[i+1],y[i+1]}};
    }
    const auto reflect=[](CurvePoint p){return CurvePoint{-p.y,-p.x};};
    for(size_t i=0;i<count;i++) {
        const auto c=curves[count-1-i];
        curves[count+i]={reflect(c.end),reflect(c.c2),reflect(c.c1),reflect(c.start)};
    }
    return curves;
}

// Adapted from .NET WPF HfdBasis32 / Bezier32 (MIT): see the full notice in
// docs/third-party/native-rendering/wpf-MIT.txt. The 1/4-pixel tolerance and
// fixed-point rounding are part of the existing hardware outline, not a new
// lower-resolution visual profile. 64-bit storage avoids signed overflow.
struct CurveBasis {
    int64_t e0,e1,e2,e3;
    CurveBasis(int64_t a,int64_t b,int64_t c,int64_t d):
        e0(a*1024),e1((d-a)*1024),e2(6*(b-2*c+d)*1024),e3(6*(a-2*b+c)*1024){}
    int64_t Error()const{return std::max(std::abs(e2),std::abs(e3));}
    int64_t ParentError()const{return std::max(std::abs(e3),std::abs(2*e2-e3));}
    void LazyHalf(unsigned shift){e2=(e2+e3)>>1;e1=(e1-(e2>>shift))>>1;}
    void Steady(unsigned shift){e0*=8;e1*=8;if(shift<3){e2*=int64_t(1)<<(3-shift);e3*=int64_t(1)<<(3-shift);}else{e2>>=shift-3;e3>>=shift-3;}}
    void Half(){e2=(e2+e3)>>3;e1=(e1-e2)>>1;e3>>=2;}
    void Double(){e1=2*e1+e2;e3*=4;e2=8*e2-e3;}
    void Step(){e0+=e1;const auto old=e2;e1+=old;e2+=old-e3;e3=old;}
    double Value()const{return double((e0+4096)>>13)/16;}
};
template<typename Add> void FlattenCompact(CurvePoint a,CurvePoint b,CurvePoint c,CurvePoint d,Add add) {
    const auto fixed=[](double value){return int64_t(std::floor(value*16+.5));};
    CurveBasis x(fixed(a.x),fixed(b.x),fixed(c.x),fixed(d.x));
    CurveBasis y(fixed(a.y),fixed(b.y),fixed(c.y),fixed(d.y));
    unsigned shift=0,steps=1;
    while(std::max(x.Error(),y.Error())>(int64_t(24*1024)<<shift)) {
        shift+=2;x.LazyHalf(shift);y.LazyHalf(shift);steps*=2;
    }
    x.Steady(shift);y.Steady(shift);x.Step();y.Step();--steps;
    for(;;) {
        add(x.Value(),y.Value());
        if(steps==0)break;
        constexpr int64_t tolerance=24*8192;
        if(std::max(x.Error(),y.Error())>tolerance){x.Half();y.Half();steps*=2;}
        while(!(steps&1) && x.ParentError()<=tolerance/4 && y.ParentError()<=tolerance/4) {
            x.Double();y.Double();steps/=2;
        }
        --steps;x.Step();y.Step();
    }
}
}
