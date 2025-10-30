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
            Cull Front

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
            Name "ShadowDepthBlit"

            ZTest LEqual
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            sampler2D _SourceDepth;

            struct Attributes
            {
                float3 vertex : POSITION;
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                output.vertex = float4(input.vertex.xy, 0.0, 1.0);
                output.texcoord = (input.vertex.xy + 1.0) * 0.5;
#if UNITY_UV_STARTS_AT_TOP
                output.texcoord = output.texcoord * float2(1.0, -1.0) + float2(0.0, 1.0);
#endif

                return output;
            }

            half frag(Varyings input) : SV_Depth
            {
                return tex2D(_SourceDepth, input.texcoord).r;
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

            struct Attributes
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.position = TransformWorldToHClip(v.position.xyz);
                o.uv = v.uv;

                return o;
            }

            half frag(Varyings IN) : Color
            {
                float2 uv = IN.uv;

                return tex2D(_DepthTexture, uv).r;
            }

            ENDHLSL
        }
    }
}
