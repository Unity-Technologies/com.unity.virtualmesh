Shader "VirtualMesh/PlaceholderLit"
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

			HLSLPROGRAM
			#pragma target 2.0
			#pragma vertex vert
			#pragma fragment frag

			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

			#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

			float3 _LightDirection;
			float3 _LightPosition;

			struct Attributes
			{
				float4 position : POSITION;
				float3 normal : NORMAL;
			};

			struct Varyings
			{
				float4 position : SV_POSITION;
			};

			float4 GetShadowPositionHClip(Attributes input)
			{
				float3 positionWS = TransformObjectToWorld(input.position.xyz);
				float3 normalWS = TransformObjectToWorldNormal(input.normal);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
				float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
				float3 lightDirectionWS = _LightDirection;
#endif

				float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
				positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
				positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

				return positionCS;
			}

			Varyings vert(Attributes v)
			{
				Varyings o;
				o.position = GetShadowPositionHClip(v);
				return o;
			}

			float frag(Varyings i) : SV_Target
			{
				return 0;
			}
			ENDHLSL
		}

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }

			HLSLPROGRAM
			#pragma target 2.0
			#pragma vertex vert
			#pragma fragment frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes
			{
				float4 position : POSITION;
			};

			struct Varyings
			{
				float4 position : SV_POSITION;
			};

			Varyings vert(Attributes v)
			{
				Varyings o;
				o.position = TransformObjectToHClip(v.position.xyz);
				return o;
			}

			float4 frag(Varyings i) : SV_Target
			{
				return float4(1, 1, 1, 1);
			}
			ENDHLSL
		}

		Pass
		{
			Name "GBuffer"
			Tags { "LightMode" = "UniversalGBuffer" }

			HLSLPROGRAM
			#pragma target 2.0
			#pragma vertex vert
			#pragma fragment frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes
			{
				float4 position : POSITION;
			};

			struct Varyings
			{
				float4 position : SV_POSITION;
			};

			Varyings vert(Attributes v)
			{
				Varyings o;
				o.position = TransformObjectToHClip(v.position.xyz);
				return o;
			}

			float4 frag(Varyings i) : SV_Target
			{
				return float4(1, 1, 1, 1);
			}
			ENDHLSL
		}
	}
}
