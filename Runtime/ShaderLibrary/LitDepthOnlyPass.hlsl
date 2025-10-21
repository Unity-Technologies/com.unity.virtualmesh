#ifndef VMESH_DEPTH_ONLY_PASS_INCLUDED
#define VMESH_DEPTH_ONLY_PASS_INCLUDED

// Based on Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
//#if defined(LOD_FADE_CROSSFADE)
//    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
//#endif

struct Attributes
{
    float4 position     : POSITION;
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
    UNITY_VERTEX_OUTPUT_STEREO
};

//---- Virtual Mesh custom vertex shader starts here
ByteAddressBuffer VertexPositionBuffer;
ByteAddressBuffer VertexAttributeBuffer;

Varyings DepthOnlyVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Varyings output = (Varyings)0;

    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // retrieve cluster data
    uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
#if defined(_ALPHATEST_ON)
    uint2 vdataAttribute = VertexAttributeBuffer.Load2(mad(vertexID, 2, 2) * 4);
#endif

    // unpack and process vertex attributes
    float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
#if defined(_ALPHATEST_ON)
    float2 uv0 = f16tof32(vdataAttribute.xy);
    output.uv = TRANSFORM_TEX(uv0, _BaseMap);
#endif

    output.positionCS = TransformObjectToHClip(position);

    return output;
}

//---- Virtual Mesh custom vertex shader ends here

half DepthOnlyFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

//    #if defined(LOD_FADE_CROSSFADE)
//        LODFadeCrossFade(input.positionCS);
//    #endif

    return input.positionCS.z;
}
#endif
