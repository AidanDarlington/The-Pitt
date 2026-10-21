// URP 17.6 SimpleLitInput.hlsl with the ground grime properties added to the material buffer.
// It claims Simple Lit's include guard, so URP's own pass files reuse these declarations instead of their own.
#ifndef UNIVERSAL_SIMPLE_LIT_INPUT_INCLUDED
#define UNIVERSAL_SIMPLE_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/SurfaceType.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BaseMap_TexelSize;
    half4 _BaseColor;
    half4 _SpecColor;
    half4 _EmissionColor;
    half _Cutoff;
    float _GrimeHeight;
    float _GrimeWidth;
    half _GrimeStrength;
    UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _SpecColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

static float4 unity_DOTS_Sampled_BaseColor;
static float4 unity_DOTS_Sampled_SpecColor;
static float4 unity_DOTS_Sampled_EmissionColor;
static float  unity_DOTS_Sampled_Cutoff;

void SetupDOTSSimpleLitMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor     = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _BaseColor);
    unity_DOTS_Sampled_SpecColor     = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _SpecColor);
    unity_DOTS_Sampled_EmissionColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _EmissionColor);
    unity_DOTS_Sampled_Cutoff        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float  , _Cutoff);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSSimpleLitMaterialPropertyCaches()

#define _BaseColor          unity_DOTS_Sampled_BaseColor
#define _SpecColor          unity_DOTS_Sampled_SpecColor
#define _EmissionColor      unity_DOTS_Sampled_EmissionColor
#define _Cutoff             unity_DOTS_Sampled_Cutoff

#endif

TEXTURE2D(_SpecGlossMap);       SAMPLER(sampler_SpecGlossMap);

// The strip repeats along the wall but never up it: clamping V keeps the ground row from bleeding onto its top edge.
TEXTURE2D(_GrimeMap);           SAMPLER(sampler_trilinear_repeatU_clampV);

half4 SampleSpecularSmoothness(float2 uv, half alpha, half4 specColor, TEXTURE2D_PARAM(specMap, sampler_specMap))
{
    half4 specularSmoothness = half4(0, 0, 0, 1);
#ifdef _SPECGLOSSMAP
    specularSmoothness = SAMPLE_TEXTURE2D(specMap, sampler_specMap, uv) * specColor;
#elif defined(_SPECULAR_COLOR)
    specularSmoothness = specColor;
#endif

#ifdef _GLOSSINESS_FROM_BASE_ALPHA
    specularSmoothness.a = alpha;
#endif

    return specularSmoothness;
}

inline void InitializeSimpleLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    outSurfaceData = (SurfaceData)0;

    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    outSurfaceData.alpha = albedoAlpha.a * _BaseColor.a;
    outSurfaceData.alpha = AlphaDiscard(outSurfaceData.alpha, _Cutoff);

    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

    half4 specularSmoothness = SampleSpecularSmoothness(uv, outSurfaceData.alpha, _SpecColor, TEXTURE2D_ARGS(_SpecGlossMap, sampler_SpecGlossMap));
    outSurfaceData.metallic = 0.0; // unused
    outSurfaceData.specular = specularSmoothness.rgb;
    outSurfaceData.smoothness = specularSmoothness.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
    outSurfaceData.occlusion = 1.0;
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
}

// Multiplies the grime strip into the albedo. alongWall is metres along the wall (UV0.x), height is metres above
// the ground the wall stands on (UV3.x, written by the city generator). The strip's bottom edge sits on the ground
// and it stretches up to _GrimeHeight; its alpha says how much of its colour to multiply in.
void ApplyGroundGrime(float alongWall, float height, inout half3 albedo)
{
    float2 uv = float2(alongWall / _GrimeWidth, height / _GrimeHeight);
    half4 grime = SAMPLE_TEXTURE2D(_GrimeMap, sampler_trilinear_repeatU_clampV, uv);
    half amount = grime.a * _GrimeStrength * step(height, _GrimeHeight);
    albedo = lerp(albedo, albedo * grime.rgb, amount);
}

#endif
