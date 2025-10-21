Shader "VirtualMesh/DebugPasses"
{
    Properties
    { }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            //Blend One One
            Blend One Zero
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 2.0

            //#pragma enable_d3d11_debug_symbols

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

            ByteAddressBuffer VertexPositionBuffer;

            struct Varyings
            {
                float4 position     : SV_Position;
                uint instanceID     : SV_InstanceID;
            };

            float3 hash(float p)
            {
                float3 p3 = frac((float3)p * float3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xxy + p3.yzz) * p3.zyx);
            }

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings OUT = (Varyings)0;

                uint2 vdata = VertexPositionBuffer.Load2(vertexID * 4);

                // unpack and process vertex attributes
                float3 position = f16tof32(uint3(vdata.x >> 16, vdata.x, vdata.y));

                OUT.position = TransformWorldToHClip(position);
                OUT.instanceID = floor(vertexID / 3.0);

                return OUT;
            }

            float4 frag(Varyings IN, uint triangleID : SV_PrimitiveID) : SV_Target
            {
                //float depth = LinearEyeDepth(IN.position.z, _ZBufferParams) * 0.005;
                //return float4(depth, depth, depth, 1);

                return float4(hash(IN.instanceID), 1);
            }
            ENDHLSL
        }
    }
}
