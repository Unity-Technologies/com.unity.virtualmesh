#ifndef VERTEXCOLOR_FORWARD_LIT_PASS_INCLUDED
#define VERTEXCOLOR_FORWARD_LIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// keep this file in sync with LitGBufferPass.hlsl

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float3 vertexColor  : COLOR;

    /* [VERTEX COLOR LIT] do we need to care lightmap???
    float2 staticLightmapUV   : TEXCOORD1;
    float2 dynamicLightmapUV  : TEXCOORD2;
    */

    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

#if !defined(_VERTEX_LIGHTING_ONLY)
    float3 normalWS                 : TEXCOORD2;
#endif

    half4 fogFactorAndVertexLight   : TEXCOORD5; // x: fogFactor, yzw: vertex light

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR) && !defined(_VERTEX_LIGHTING_ONLY)
    float4 shadowCoord              : TEXCOORD6;
#endif

    /* [VERTEX COLOR LIT] do we need to care lightmap???
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
#ifdef DYNAMICLIGHTMAP_ON
    float2  dynamicLightmapUV : TEXCOORD9; // Dynamic lightmap UVs
#endif
    */

    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

#if !defined(_VERTEX_LIGHTING_ONLY)
    inputData.normalWS = input.normalWS;
    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR) && !defined(_VERTEX_LIGHTING_ONLY)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;

    /* [VERTEX COLOR LIT] do we need to care lightmap???
#if defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
#else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
#endif
    */

    /* [VERTEX COLOR LIT] this isn't used in current desgin
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    */

    /* [VERTEX COLOR LIT] do we need to care lightmap???
#if !defined(_VERTEX_LIGHTING_ONLY)
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
    */
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

// Used in Standard (Physically Based) shader
Varyings LitPassVertex(Attributes input) 
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    // normalWS and tangentWS already normalize.
    // this is required to avoid skewing the direction during interpolation
    // also required for per-vertex lighting and SH evaluation
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

    half3 vertexLight = half3(0.0, 0.0, 0.0);

#if defined(_VERTEX_LIGHTING_ONLY)
    Light mainLight = GetMainLight(GetShadowCoord(vertexInput), vertexInput.positionWS, half4(0.0, 0.0, 0.0, 0.0)); //SAMPLE_SHADOWMASK(input.staticLightmapUV);
    vertexLight += LightingLambert(mainLight.color * mainLight.distanceAttenuation, mainLight.direction, normalInput.normalWS);

    // [VERTEX COLOR LIT] below codes are copied from VertexLighting in URP's Lighting.hlsl
    uint lightsCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(lightsCount)
        Light light = GetAdditionalLight(lightIndex, vertexInput.positionWS);
        half3 lightColor = light.color * light.distanceAttenuation;
        vertexLight += LightingLambert(lightColor, light.direction, normalInput.normalWS);
    LIGHT_LOOP_END

    vertexLight += SampleSH(normalInput.normalWS);
    vertexLight *= input.vertexColor;
#else
    vertexLight = input.vertexColor;

    output.normalWS = normalInput.normalWS;    
#endif

    half fogFactor = 0;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
#endif

    /* [VERTEX COLOR LIT] do we need to care lightmap???
    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
#if defined(DYNAMICLIGHTMAP_ON)
    output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
    OUTPUT_SH(output.normalWS.xyz, output.vertexSH);
    */
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR) && !defined(_VERTEX_LIGHTING_ONLY)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;

    return output;
}

// Used in Standard (Physically Based) shader
half4 LitPassFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    InputData inputData;
    InitializeInputData(input, inputData);

    /* [VERTEX COLOR LIT] do we need to care decal???
#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif
    */

    half4 color = half4(0, 0, 0, 0);

#if defined(_VERTEX_LIGHTING_ONLY)
    color.xyz = inputData.vertexLighting;
#else
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, half4(0.0, 0.0, 0.0, 0.0)); // inputData.shadowMask
    color.xyz += LightingLambert(mainLight.color * mainLight.distanceAttenuation, mainLight.direction, inputData.normalWS);

    // [VERTEX COLOR LIT] below codes are copied from VertexLighting in URP's Lighting.hlsl
    uint lightsCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(lightsCount)
        Light light = GetAdditionalLight(lightIndex, inputData.positionWS);
        half3 lightColor = light.color * light.distanceAttenuation;
        color.xyz += LightingLambert(lightColor, light.direction, inputData.normalWS);
    LIGHT_LOOP_END

    color.xyz += SampleSH(inputData.normalWS);
    color.xyz *= inputData.vertexLighting;
#endif

    BRDFData brdfData;
    half alpha = 1;
    InitializeBRDFData(inputData.vertexLighting, 0, 0, 1, alpha, brdfData);

    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, _Surface);

    return color;
}
#endif
