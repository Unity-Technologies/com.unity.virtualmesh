Shader "VirtualMesh/CustomPasses"
{
    Properties
    { }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            //Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            ByteAddressBuffer VertexPositionBuffer;
            ByteAddressBuffer VertexAttributeBuffer;

            half3 _LightDirection;
            half3 _LightPosition;

            struct Varyings
            {
                half4 position : SV_Position;
            };

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings OUT = (Varyings)0;

                // retrieve cluster data
                uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);
                uint vdataAttribute = VertexAttributeBuffer.Load(vertexID * 2 * 4);

                // unpack and process vertex attributes
                half3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));
                float2 packedData = float2(vdataAttribute >> 16, vdataAttribute & 0xffff);
                packedData *= rcp(65535.0);
                half2 octNormal = mad(packedData.xy, 2.0, -1.0);
                half3 normal = UnpackNormalOctQuadEncode(octNormal);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                half3 lightDirectionWS = normalize(_LightPosition - position);
#else
                half3 lightDirectionWS = _LightDirection;
#endif
                half4 positionCS = TransformWorldToHClip(ApplyShadowBias(position, normal, lightDirectionWS));
                OUT.position = ApplyShadowClamping(positionCS);

                return OUT;
            }

            half frag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthPyramidBlit"

            ZWrite Off
            ZTest Always
            Cull Off
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            sampler2D _DepthTexture;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings OUT = (Varyings)0;

                OUT.position = GetFullScreenTriangleVertexPosition(vertexID);
                OUT.uv = GetFullScreenTriangleTexCoord(vertexID);

                return OUT;
            }

            half frag(Varyings IN) : Color
            {
                return tex2D(_DepthTexture, IN.uv).r;
            }

            ENDHLSL
        }
    }
}
