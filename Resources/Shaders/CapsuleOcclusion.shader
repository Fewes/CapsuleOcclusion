Shader "Hidden/CapsuleOcclusion"
{
	SubShader
	{
		Pass
		{
			Name "Clustering"
			
			Cull Front
			ZWrite Off
			ZTest Always
			ColorMask 0
			// Conservative On // Can't enable this or we get multiple data writes per fragment

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_instancing

			#include "UnityCG.cginc"

			RWTexture3D<uint> _ClustersCount : register(u1);
			RWTexture3D<uint> _ClustersPointer : register(u2);
			RWStructuredBuffer<uint2> _ClusterData : register(u3);
			RWStructuredBuffer<uint> _Counter : register(u4);

			#include "../../Shaders/CapsuleOcclusionBuildClusters.hlsl"

			float _ConservativeRasterization;

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float4 color : COLOR;

				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float4 params1 : TEXCOORD1;
				float4 params2 : TEXCOORD2;
				float4 screenPos : TEXCOORD3;
				uint capsuleID : TEXCOORD4;
				float safeDist : TEXCOORD5;
			};

			v2f vert(appdata v, uint instanceID : SV_InstanceID)
			{
				v2f o;

				UNITY_SETUP_INSTANCE_ID(v);

				o.params1 = _CapsuleParams1[instanceID];
				o.params2 = _CapsuleParams2[instanceID];
				float size = length(o.params1.xyz - o.params2.xyz);
				float radius = o.params1.w + o.params2.w;

				float3 N = normalize(o.params2.xyz - o.params1.xyz);
				float3 B = abs(N.y) < 0.707 ? float3(0, 1, 0) : float3(0, 0, 1);
				float3 T = normalize(cross(B, N));
				B = cross(N, T);
				float3x3 rot = float3x3(T, B, N);

				if (_ConservativeRasterization)
				{
					// Software conservative rasterization: calculate froxel size at vertex depth and expand via normal
					float4 cs1 = UnityWorldToClipPos(o.params1.xyz);
					float4 cs2 = UnityWorldToClipPos(o.params2.xyz);
					float linearDepth1 = Linear01Depth(cs1.z / cs1.w);
					float linearDepth2 = Linear01Depth(cs2.z / cs2.w);
					uint3 clusterPos1 = GetClusterPos(float2(0.5, 0.5), linearDepth1);
					uint3 clusterPos2 = GetClusterPos(float2(0.5, 0.5), linearDepth2);
					uint3 clusterPos = clusterPos1.z > clusterPos2.z ? clusterPos1 : clusterPos2;
					float3 froxelPoint = ClusterPosToWorld(clusterPos + float3(0.5, 0.5, 1.0));
					float3 position_RU = ClusterPosToWorld(clusterPos + float3(1.0, 1.0, 1.0));
					float froxelRadius = length(position_RU - froxelPoint);
					radius += froxelRadius;
					o.params2.w += froxelRadius;
				}

				v.vertex.xyz *= radius;
				v.vertex.z += (v.color.r - 0.5) * size;

				o.positionWS = mul(v.vertex.xyz, rot) + (o.params1.xyz + o.params2.xyz) * 0.5;

				o.vertex = mul(UNITY_MATRIX_VP, float4(o.positionWS, 1));
				o.vertex += UnityObjectToClipPos(v.vertex) * 1e-20; // TODO: Some instancing quirk requires us to call UnityObjectToClipPos
				o.screenPos = ComputeScreenPos(o.vertex);

				o.capsuleID = instanceID;

				o.safeDist = (length(o.params1.xyz - o.params2.xyz) + o.params1.w + o.params2.w) * 2;

				return o;
			}

			void frag(v2f i)
			{
				float2 uv = i.screenPos.xy / i.screenPos.w;

				float3 rayStart = _WorldSpaceCameraPos;
				float3 rayDir = i.positionWS - rayStart;
				float t2 = length(rayDir);
				rayDir /= t2;
				// Note stupid hack (safeDist): The capsule intersection function does not return valid negative intersection times (camera inside capsule).
				// To get around this we move the start position back by the max size of the capsule, then subtract the same value from the result.
				float t1 = CapsuleIntersection(rayStart - rayDir * i.safeDist, rayDir, i.params1.xyz, i.params2.xyz, i.params1.w + i.params2.w);
				if (t1 < 0)
				{
					return;
				}
				t1 = max(0, t1 - i.safeDist);

				float3 pos1 = rayStart + rayDir * t1;
				float3 pos2 = rayStart + rayDir * t2;
				float4 cs1 = mul(UNITY_MATRIX_VP, float4(pos1, 1));
				float4 cs2 = mul(UNITY_MATRIX_VP, float4(pos2, 1));

				uint3 clusterPos1 = GetClusterPos(uv, Linear01Depth(cs1.z / cs1.w));
				uint3 clusterPos2 = GetClusterPos(uv, Linear01Depth(cs2.z / cs2.w));
				clusterPos1.z = clamp(clusterPos1.z, 0, _ClusterSize.z - 1);
				clusterPos2.z = clamp(clusterPos2.z, 0, _ClusterSize.z - 1);

				for (uint z = clusterPos1.z; z <= clusterPos2.z; z++)
				{
					WriteCapsuleToCluster(uint3(clusterPos1.xy, z), i.capsuleID);
				}
			}
			ENDHLSL
		}

		Pass
		{
			Name "Visualization"

			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_instancing

			#include "UnityCG.cginc"
			#include "../../Shaders/CapsuleOcclusionShared.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float4 color : COLOR;

				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float4 params1 : TEXCOORD1;
				float4 params2 : TEXCOORD2;
				float3 color : COLOR;
			};

			v2f vert(appdata v, uint instanceID : SV_InstanceID)
			{
				v2f o;

				UNITY_SETUP_INSTANCE_ID(v);

				o.params1 = _CapsuleParams1[instanceID];
				o.params2 = _CapsuleParams2[instanceID];
				float size = length(o.params1.xyz - o.params2.xyz);
				float radius = o.params1.w;

				float3 N = normalize(o.params2.xyz - o.params1.xyz);
				float3 B = abs(N.y) < 0.707 ? float3(0, 1, 0) : float3(0, 0, 1);
				float3 T = normalize(cross(B, N));
				B = cross(N, T);
				float3x3 rot = float3x3(T, B, N);

				v.vertex.xyz *= radius;
				v.vertex.z += (v.color.r - 0.5) * size;

				o.positionWS = mul(v.vertex.xyz, rot) + (o.params1.xyz + o.params2.xyz) * 0.5;

				o.vertex = mul(UNITY_MATRIX_VP, float4(o.positionWS, 1));
				o.vertex += UnityObjectToClipPos(v.vertex) * 1e-20;

				o.color = dot(mul(v.normal, rot) * 0.5 + 0.5, 0.333) * float3(1, 0.5, 0);

				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				return float4(i.color, 1);
			}
			ENDHLSL
		}
	}
}
