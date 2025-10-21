#ifndef VMESH_FORWARD_LIT_DEPTH_NORMALS_PASS_INCLUDED
#define VMESH_FORWARD_LIT_DEPTH_NORMALS_PASS_INCLUDED

// Based on Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
//#if defined(LOD_FADE_CROSSFADE)
//    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
//#endif

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif

#if defined(_PARALLAXMAP)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

#if defined(_ALPHATEST_ON) || defined(_PARALLAXMAP) || defined(_NORMALMAP) || defined(_DETAIL)
#define REQUIRES_UV_INTERPOLATOR
#endif

struct Attributes
{
    float4 positionOS   : POSITION;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
    float3 normal       : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS  : SV_POSITION;
    #if defined(REQUIRES_UV_INTERPOLATOR)
    float2 uv          : TEXCOORD1;
    #endif
    half3 normalWS     : TEXCOORD2;

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS    : TEXCOORD4;    // xyz: tangent, w: sign
    #endif

    half3 viewDirWS    : TEXCOORD5;

    #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS    : TEXCOORD8;
    #endif

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

//---- Virtual Mesh custom vertex shader starts here
ByteAddressBuffer VertexPositionBuffer;
ByteAddressBuffer VertexAttributeBuffer;

Varyings DepthNormalsVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Varyings output = (Varyings)0;

    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // retrieve cluster data
    uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
    uint4 vdataAttribute = VertexAttributeBuffer.Load4(vertexID * 2 * 4);

    // unpack and process vertex attributes
    float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
    float4 packedData = float4(vdataAttribute.x >> 16, vdataAttribute.x & 0xffff, vdataAttribute.y >> 16, vdataAttribute.y & 0xffff);
    packedData *= rcp(65535.0);
#if defined(REQUIRES_UV_INTERPOLATOR)
    float2 uv0 = f16tof32(vdataAttribute.zw);
    output.uv = TRANSFORM_TEX(uv0, _BaseMap);
#endif
    float2 octNormal = mad(packedData.xy, 2.0, -1.0);
    float3 normal = UnpackNormalOctQuadEncode(octNormal);
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    float2 octTangent = mad(packedData.zw, 2.0, -1.0);
    half4 tangent = half4(UnpackNormalOctQuadEncode(octTangent), (vdata.y >> 16) ? 1.0 : -1.0);
    tangent.w *= GetOddNegativeScale();
#endif

#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    output.tangentWS = tangent;
#endif

    VertexPositionInputs vertexInput = GetVertexPositionInputs(position);

    output.positionCS = TransformObjectToHClip(position);
    output.normalWS = NormalizeNormalPerVertex(normal);

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(tangent, output.normalWS, viewDirWS);
    output.viewDirTS = viewDirTS;
#endif

    return output;
}

//---- Virtual Mesh custom vertex shader ends here

void DepthNormalsFragment(
    Varyings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

//    #if defined(LOD_FADE_CROSSFADE)
//        LODFadeCrossFade(input.positionCS);
//    #endif

    #if defined(_GBUFFER_NORMALS_OCT)
        float3 normalWS = normalize(input.normalWS);
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);           // values between [-1, +1], must use fp32 on some platforms
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);   // values between [ 0,  1]
        half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);      // values between [ 0,  1]
        outNormalWS = half4(packedNormalWS, 0.0);
    #else
        #if defined(_PARALLAXMAP)
            #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = input.viewDirTS;
            #else
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, input.viewDirWS);
            #endif
            ApplyPerPixelDisplacement(viewDirTS, input.uv);
        #endif

        #if defined(_NORMALMAP) || defined(_DETAIL)
            float sgn = input.tangentWS.w;      // should be either +1 or -1
            float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
            float3 normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);

            #if defined(_DETAIL)
                half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, input.uv).a;
                float2 detailUv = input.uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
                normalTS = ApplyDetailNormal(detailUv, normalTS, detailMask);
            #endif

            float3 normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
        #else
            float3 normalWS = input.normalWS;
        #endif

        outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif
