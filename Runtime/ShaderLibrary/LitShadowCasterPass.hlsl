#ifndef VMESH_SHADOW_CASTER_PASS_INCLUDED
#define VMESH_SHADOW_CASTER_PASS_INCLUDED

// Based on Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
//#if defined(LOD_FADE_CROSSFADE)
//    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
//#endif

// Shadow Casting Light geometric parameters. These variables are used when applying the shadow Normal Bias and are set by UnityEngine.Rendering.Universal.ShadowUtils.SetupShadowCasterConstantBuffer in com.unity.render-pipelines.universal/Runtime/ShadowUtils.cs
// For Directional lights, _LightDirection is used when applying shadow Normal Bias.
// For Spot lights and Point lights, _LightPosition is used to compute the actual light direction because it is different at each shadow caster geometry vertex.
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float2 texcoord     : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    #if defined(_ALPHATEST_ON)
        float2 uv       : TEXCOORD0;
    #endif
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float4 GetShadowPositionHClip(Attributes input)
{
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    positionCS = ApplyShadowClamping(positionCS);
    return positionCS;
}

//---- Virtual Mesh custom vertex shader starts here
ByteAddressBuffer VertexPositionBuffer;
ByteAddressBuffer VertexAttributeBuffer;

Varyings ShadowPassVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Varyings output;

    // retrieve cluster data
    uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
#if defined(_ALPHATEST_ON)
    uint4 vdataAttribute = VertexAttributeBuffer.Load4(vertexID * 2 * 4);
#else
    uint vdataAttribute = VertexAttributeBuffer.Load(vertexID * 2 * 4);
#endif
    
    // unpack and process vertex attributes
    float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
#if defined(_ALPHATEST_ON)
    float2 packedData = float2(vdataAttribute.x >> 16, vdataAttribute.x & 0xffff);
#else
    float2 packedData = float2(vdataAttribute >> 16, vdataAttribute & 0xffff);
#endif
    packedData *= rcp(65535.0);
    float2 octNormal = mad(packedData.xy, 2.0, -1.0);
    float3 normal = UnpackNormalOctQuadEncode(octNormal);
    
#if defined(_ALPHATEST_ON)
    float2 uv0 = f16tof32(vdataAttribute.zw);
    output.uv = TRANSFORM_TEX(uv0, _BaseMap);
#endif

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - position);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(position, normal, lightDirectionWS));
    output.positionCS = ApplyShadowClamping(positionCS);

    return output;
}

//---- Virtual Mesh custom vertex shader ends here

half4 ShadowPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

//    #if defined(LOD_FADE_CROSSFADE)
//        LODFadeCrossFade(input.positionCS);
//    #endif

    return 0;
}

#endif
