#ifndef VMESH_UNIVERSAL_FALLBACK_2D_INCLUDED
#define VMESH_UNIVERSAL_FALLBACK_2D_INCLUDED

// Based on Packages/com.unity.render-pipelines.universal/Shaders/Utils/Universal2D.hlsl

struct Attributes
{
    float4 positionOS       : POSITION;
    float2 uv               : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv        : TEXCOORD0;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

//---- Virtual Mesh custom vertex shader starts here
ByteAddressBuffer VertexPositionBuffer;
ByteAddressBuffer VertexAttributeBuffer;

Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Varyings output = (Varyings)0;

    // retrieve cluster data
    uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
    uint2 vdataAttribute = VertexAttributeBuffer.Load2(mad(vertexID, 2, 2) * 4);

    // unpack and process vertex attributes
    float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
    float2 uv0 = f16tof32(vdataAttribute.xy);
    output.uv = TRANSFORM_TEX(uv0, _BaseMap);
    output.vertex = TransformObjectToHClip(position);

    return output;
}

//---- Virtual Mesh custom vertex shader ends here

half4 frag(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    half2 uv = input.uv;
    half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 color = texColor.rgb * _BaseColor.rgb;
    half alpha = texColor.a * _BaseColor.a;
    AlphaDiscard(alpha, _Cutoff);

#ifdef _ALPHAPREMULTIPLY_ON
    color *= alpha;
#endif
    return half4(color, alpha);
}

#endif
