#ifndef VMESH_COMMON_INCLUDED
#define VMESH_COMMON_INCLUDED

// Based on Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/Varyings.hlsl

ByteAddressBuffer VertexPositionBuffer;
ByteAddressBuffer VertexAttributeBuffer;

Varyings BuildVirtualMeshVaryings(uint vertexID, uint instanceID)
{
    Varyings output = (Varyings)0;

    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        
    // retrieve cluster data
    uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
#if defined(ATTRIBUTES_NEED_NORMAL) || defined(ATTRIBUTES_NEED_TANGENT) || defined(ATTRIBUTES_NEED_TEXCOORD0) || defined(ATTRIBUTES_NEED_TEXCOORD1)
    uint4 vdataAttribute = VertexAttributeBuffer.Load4(vertexID * 2 * 4);
#endif
    
    // unpack and process vertex attributes
    float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
    float4 packedData = float4(vdataAttribute.x >> 16, vdataAttribute.x & 0xffff, vdataAttribute.y >> 16, vdataAttribute.y & 0xffff);
    packedData *= rcp(65535.0);
#ifdef ATTRIBUTES_NEED_TEXCOORD0
    float2 uv0 = f16tof32(vdataAttribute.zw);
#endif
#if defined(ATTRIBUTES_NEED_TEXCOORD1) || (SHADERPASS == SHADERPASS_FORWARD) || (SHADERPASS == SHADERPASS_GBUFFER)
    float2 uv1 = f16tof32(vdataAttribute.zw >> 16);
#endif
#ifdef ATTRIBUTES_NEED_NORMAL
    float2 octNormal = mad(packedData.xy, 2.0, -1.0);
    float3 normal = UnpackNormalOctQuadEncode(octNormal);
#endif
#ifdef ATTRIBUTES_NEED_TANGENT
    float2 octTangent = mad(packedData.zw, 2.0, -1.0);
    half4 tangent = half4(UnpackNormalOctQuadEncode(octTangent), (vdata.y >> 16) ? 1.0 : -1.0);
    tangent.w *= GetOddNegativeScale();
#endif
        
    VertexPositionInputs vertexInput = GetVertexPositionInputs(position);

#ifdef VARYINGS_NEED_POSITION_WS
    output.positionWS = position;
#endif

#ifdef VARYINGS_NEED_NORMAL_WS
    output.normalWS = normal;
#endif

#ifdef VARYINGS_NEED_TANGENT_WS
    output.tangentWS = tangent;
#endif
        
#if (SHADERPASS == SHADERPASS_SHADOWCASTER)
// Define shadow pass specific clip position for Universal
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - position);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    output.positionCS = TransformWorldToHClip(ApplyShadowBias(position, normal, lightDirectionWS));
    output.positionCS = ApplyShadowClamping(output.positionCS);
#else
    output.positionCS = TransformWorldToHClip(position);
#endif

#if defined(VARYINGS_NEED_TEXCOORD0) || defined(VARYINGS_DS_NEED_TEXCOORD0)
    output.texCoord0 = float4(uv0.xy, 0.0, 0.0);
#endif

#if defined(VARYINGS_NEED_TEXCOORD1) || defined(VARYINGS_DS_NEED_TEXCOORD1)
    output.texCoord1 = float4(uv1.xy, 0.0, 0.0);
#endif

#ifdef VARYINGS_NEED_SCREENPOSITION
    output.screenPosition = vertexInput.positionNDC;
#endif
    
#if (SHADERPASS == SHADERPASS_FORWARD) || (SHADERPASS == SHADERPASS_GBUFFER)
    OUTPUT_LIGHTMAP_UV(uv1, unity_LightmapST, output.staticLightmapUV);
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.sh, output.probeOcclusion);
#endif

#ifdef VARYINGS_NEED_FOG_AND_VERTEX_LIGHT
    half fogFactor = 0;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(output.positionCS.z);
#endif
    half3 vertexLight = VertexLighting(vertexInput.positionWS, output.normalWS);
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#endif

#if defined(VARYINGS_NEED_SHADOW_COORD) && defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

#if defined(VARYINGS_NEED_SIX_WAY_DIFFUSE_GI_DATA)
    GatherDiffuseGIData(vertexInput.positionWS, output.normalWS.xyz, output.tangentWS, output.diffuseGIData0, output.diffuseGIData1, output.diffuseGIData2);
#endif

    return output;
}

#endif
