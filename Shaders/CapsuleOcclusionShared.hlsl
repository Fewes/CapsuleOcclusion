#ifndef CAPSULE_OCCLUSION_SHARED_INCLUDED
#define CAPSULE_OCCLUSION_SHARED_INCLUDED

#define MAX_CAPSULE_COUNT 512

float4 _CapsuleParams1[MAX_CAPSULE_COUNT];
float4 _CapsuleParams2[MAX_CAPSULE_COUNT];
int _CapsuleCount;
uint3 _ClusterSize;
float _DepthToRange;

uint ClusterPosToIndex(uint3 pos)
{
	return (pos.z * _ClusterSize.x * _ClusterSize.y) + (pos.y * _ClusterSize.x) + pos.x;
}

uint3 ClusterIndexToPos(uint id)
{
	uint z = id / (_ClusterSize.x * _ClusterSize.y);
	id -= (z * _ClusterSize.x * _ClusterSize.y);
	uint y = id / _ClusterSize.x;
	uint x = id % _ClusterSize.x;
	return uint3(x, y, z);
}

float3 ClusterPosToUVW(float3 pos)
{
	float3 uvw = (pos + 0.5) / _ClusterSize;
	// uvw.z = sq(uvw.z);
	uvw.z *= uvw.z;
	return uvw;
}

uint3 GetClusterPos(float2 uv, float linearDepth)
{
	float3 uvw = float3(uv, sqrt(linearDepth * _DepthToRange));
	return floor(uvw * _ClusterSize - float3(0, 0, 0.5));
}

#endif // CAPSULE_OCCLUSION_SHARED_INCLUDED