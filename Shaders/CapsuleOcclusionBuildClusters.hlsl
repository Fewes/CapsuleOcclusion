#ifndef CAPSULE_OCCLUSION_BUILD_CLUSTERS_INCLUDED
#define CAPSULE_OCCLUSION_BUILD_CLUSTERS_INCLUDED

#include "CapsuleOcclusionShared.hlsl"

#define UINT_MAX 0xffffffff
#define FLT_EPS 1e-5

float3 _CameraPosition;
float3 _CameraForward;
float _Range;

float4 _FrustumRays[4];

float3 GetCameraRay(float2 uv)
{
	return normalize(lerp(lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, uv.y), lerp(_FrustumRays[3].xyz, _FrustumRays[2].xyz, uv.y), uv.x));
}

float3 ClusterPosToWorld(uint3 pos)
{
	float3 uvw = ClusterPosToUVW(pos);

	float3 rayStart = _CameraPosition;
	float3 rayDir = GetCameraRay(uvw.xy);

	return rayStart + rayDir * uvw.z * _Range / dot(rayDir, _CameraForward);
}

// https://arrowinmyknee.com/2021/03/15/some-math-about-capsule-collision/
float LineSegmentDistSq(float3 p1, float3 q1, float3 p2, float3 q2)
{
	float  s, t;
	float3 c1, c2;

	float3 d1 = q1 - p1; // Direction vector of segment S1
	float3 d2 = q2 - p2; // Direction vector of segment S2
	float3 r = p1 - p2;
	float a = dot(d1, d1); // Squared length of segment S1, always nonnegative
	float e = dot(d2, d2); // Squared length of segment S2, always nonnegative
	float f = dot(d2, r);
	// Check if either or both segments degenerate into points
	if (a <= FLT_EPS && e <= FLT_EPS)
	{
		// Both segments degenerate into points
		s = t = 0.0f;
		c1 = p1;
		c2 = p2;
		return dot(c1 - c2, c1 - c2);
	}
	if (a <= FLT_EPS)
	{
		// First segment degenerates into a point
		s = 0.0f;
		t = f / e; // s = 0 => t = (b*s + f) / e = f / e
		t = saturate(t);
	}
	else
	{
		float c = dot(d1, r);
		if (e <= FLT_EPS)
		{
			// Second segment degenerates into a point
			t = 0.0f;
			s = saturate(-c / a); // t = 0 => s = (b*t - c) / a = -c / a
		}
		else
		{
			// The general nondegenerate case starts here
			float b = dot(d1, d2);
			float denom = a*e-b*b; // Always nonnegative
			// If segments not parallel, compute closest point on L1 to L2 and
			// clamp to segment S1. Else pick arbitrary s (here 0)
			if (denom != 0.0f)
			{
				s = saturate((b*f - c*e) / denom);
			}
			else
			{
				s = 0.0f;
			}

			// Compute point on L2 closest to S1(s) using
			// t = Dot((P1 + D1*s) - P2,D2) / Dot(D2,D2) = (b*s + f) / e
			t = (b*s + f) / e;
			// If t in [0,1] done. Else clamp t, recompute s for the new value
			// of t using s = Dot((P2 + D2*t) - P1,D1) / Dot(D1,D1)= (t*b - c) / a
			// and clamp s to [0, 1]
			if (t < 0.0f)
			{
				t = 0.0f;
				s = saturate(-c / a);
			}
			else if (t > 1.0f)
			{
				t = 1.0f;
				s = saturate((b - c) / a);
			}
		}
	}
	c1 = p1 + d1 * s;
	c2 = p2 + d2 * t;
	return dot(c1 - c2, c1 - c2);
}

// https://iquilezles.org/articles/intersectors/
float CapsuleIntersection(float3 ro, float3 rd, float3 pa, float3 pb, in float ra)
{
	float3 ba = pb - pa;
	float3 oa = ro - pa;
	float baba = dot(ba, ba);
	float bard = dot(ba, rd);
	float baoa = dot(ba, oa);
	float rdoa = dot(rd, oa);
	float oaoa = dot(oa, oa);
	float a = baba      - bard*bard;
	float b = baba*rdoa - baoa*bard;
	float c = baba*oaoa - baoa*baoa - ra*ra*baba;
	float h = b*b - a*c;
	if (h >= 0.0)
	{
		float t = (-b-sqrt(h))/a;
		float y = baoa + t*bard;
		// body
		if (y > 0.0 && y < baba) return t;
		// caps
		float3 oc = (y <= 0.0) ? oa : ro - pb;
		b = dot(rd,oc);
		c = dot(oc,oc) - ra*ra;
		h = b*b - c;
		if (h > 0.0) return -b - sqrt(h);
	}
	return -1.0;
}

void WriteCapsuleToCluster(uint3 cluster, uint capsuleID)
{
	// Allocate index in data list
	uint dataIndex;
	InterlockedAdd(_Counter[0], 1, dataIndex);

	// Get pointer to previous list item and set start pointer to us
	uint toPrev;
	InterlockedExchange(_ClustersPointer[cluster], dataIndex, toPrev);

	// Write data
	_ClusterData[dataIndex] = uint2(capsuleID, toPrev);
	uint prevCount;
	InterlockedAdd(_ClustersCount[cluster], 1, prevCount);
}

#endif // CAPSULE_OCCLUSION_BUILD_CLUSTERS_INCLUDED