using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Burst;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine.Jobs;

namespace CapsuleOcclusion
{
	[ExecuteInEditMode, BurstCompile]
	public class OcclusionCapsule : MonoBehaviour
	{
		public const int MAX_CAPSULE_COUNT = 512;

		public static List<OcclusionCapsule> instances = new List<OcclusionCapsule>();
		public static List<Transform> instanceTransforms = new List<Transform>();
		public static int visibleCapsuleCount => s_visibleCapsuleCount;

		[Min(0)]
		public float size = 1f;
		[Min(0)]
		public float radius = 0.1f;

		public Vector3 point1 => transform.TransformPoint(new Vector3(0, 0, -size / 2));
		public Vector3 point2 => transform.TransformPoint(new Vector3(0, 0, size / 2));

		private Transform m_cachedTransform;
		private Vector3 m_cachedPoint1;
		private Vector3 m_cachedPoint2;
		private float m_cachedRadiusSum;
		private Bounds m_cachedBounds;
		private float m_sortKey;

		private static int s_visibleCapsuleCount;
		private static Plane[] s_frustumPlanes = new Plane[6];
		private static List<OcclusionCapsule> s_visibleCapsules = new List<OcclusionCapsule>();
		private static Matrix4x4[] s_matrices = new Matrix4x4[MAX_CAPSULE_COUNT];
		private static Vector4[] s_capsuleParams1 = new Vector4[MAX_CAPSULE_COUNT];
		private static Vector4[] s_capsuleParams2 = new Vector4[MAX_CAPSULE_COUNT];

		public static void UpdateCapsuleDataForCamera(Camera camera, int maxCount, float rangeMultiplier, bool cull, bool sort, bool burst)
		{
			float3 cameraPosition = camera.transform.position;

			int count;

			// Hack bug fix (strange results if maxCount >= 512 for some reason)
			maxCount = Mathf.Min(maxCount, MAX_CAPSULE_COUNT - 1);

			if (burst)
			{
				int instanceCount = instances.Count;
				NativeArray<Matrix4x4> localToWorld = new NativeArray<Matrix4x4>(instanceCount, Allocator.TempJob);
				NativeArray<float> size = new NativeArray<float>(instanceCount, Allocator.TempJob);
				NativeArray<float> radius = new NativeArray<float>(instanceCount, Allocator.TempJob);
				NativeArray<float3> point1 = new NativeArray<float3>(instanceCount, Allocator.TempJob);
				NativeArray<float3> point2 = new NativeArray<float3>(instanceCount, Allocator.TempJob);
				for (int i = 0; i < instanceCount; i++)
				{
					var capsule = instances[i];
					localToWorld[i] = capsule.m_cachedTransform.localToWorldMatrix;
					size[i] = capsule.size;
					radius[i] = capsule.radius;
				}
				CacheJob cacheJob = new CacheJob()
				{
					rangeMultiplier = rangeMultiplier,
					localToWorld = localToWorld,
					size = size,
					point1 = point1,
					point2 = point2
				};
				cacheJob.Schedule(instanceCount, 1).Complete();

				NativeArray<Plane> frustum = new NativeArray<Plane>(6, Allocator.TempJob);
				GeometryUtility.CalculateFrustumPlanes(camera, s_frustumPlanes);
				for (int i = 0; i < 6; i++)
				{
					frustum[i] = s_frustumPlanes[i];
				}

				NativeArray<SortData> sortData = new NativeArray<SortData>(instances.Count, Allocator.TempJob);
				for (int i = 0; i < instances.Count; i++)
				{
					OcclusionCapsule capsule = instances[i];
					sortData[i] = new SortData()
					{
						index = i,
						sortKey = -1
					};
				}
				SortCapsules(ref sortData, ref frustum, ref cameraPosition, ref rangeMultiplier, ref point1, ref point2, ref radius);

				s_visibleCapsuleCount = 0;
				int minMaxCount = Mathf.Min(maxCount, MAX_CAPSULE_COUNT);
				for (int i = 0; i < sortData.Length && s_visibleCapsuleCount < minMaxCount; i++)
				{
					SortData d = sortData[i];
					if (d.sortKey == float.PositiveInfinity)
					{
						break;
					}
					else
					{
						int j = d.index;
						float _radius = radius[j];
						float4 param1 = new float4(point1[j], _radius);
						float4 param2 = new float4(point2[j], _radius * rangeMultiplier);
						// Note that we omit this for performance. Matrices are calculated on the fly in the vertex shader instead.
						//s_matrices[s_visibleCapsuleCount] = Matrix4x4.TRS(capsule.transform.position, capsule.transform.rotation, Vector3.one);
						s_capsuleParams1[s_visibleCapsuleCount] = param1;
						s_capsuleParams2[s_visibleCapsuleCount] = param2;

						s_visibleCapsuleCount++;
					}
				}

				localToWorld.Dispose();
				size.Dispose();
				radius.Dispose();
				point1.Dispose();
				point2.Dispose();
				frustum.Dispose();
				sortData.Dispose();
			}
			else
			{
				CacheDataForCamera(camera, rangeMultiplier);

				s_visibleCapsules.Clear();
				if (cull)
				{
					Profiler.BeginSample("Capsule Occlusion Culling");
					GeometryUtility.CalculateFrustumPlanes(camera, s_frustumPlanes);
					count = instances.Count;
					for (int i = 0; i < count; i++)
					{
						OcclusionCapsule capsule = instances[i];
						if (GeometryUtility.TestPlanesAABB(s_frustumPlanes, capsule.m_cachedBounds))
						{
							s_visibleCapsules.Add(capsule);
						}
					}
					Profiler.EndSample();
				}
				else
				{
					s_visibleCapsules.AddRange(instances);
				}

				if (sort)
				{
					Profiler.BeginSample("Capsule Occlusion Sorting");
					Vector3 cameraPosition2 = cameraPosition * 2;
					count = s_visibleCapsules.Count;
					for (int i = 0; i < count; i++)
					{
						OcclusionCapsule capsule = s_visibleCapsules[i];
						capsule.m_sortKey = (cameraPosition2 - (capsule.m_cachedPoint1 + capsule.m_cachedPoint2)).sqrMagnitude;
					}
					s_visibleCapsules.Sort((a, b) => a.m_sortKey.CompareTo(b.m_sortKey));
					Profiler.EndSample();
				}

				s_visibleCapsuleCount = 0;

				Profiler.BeginSample("Capsule Occlusion Data");
				count = s_visibleCapsules.Count;
				int minMaxCount = Mathf.Min(maxCount, MAX_CAPSULE_COUNT);
				for (int i = 0; i < count && s_visibleCapsuleCount < minMaxCount; i++)
				{
					OcclusionCapsule capsule = s_visibleCapsules[i];
					Vector4 param1 = capsule.m_cachedPoint1;
					param1.w = capsule.radius;
					Vector4 param2 = capsule.m_cachedPoint2;
					param2.w = capsule.radius * rangeMultiplier;
					// Note that we omit this for performance. Matrices are calculated on the fly in the vertex shader instead.
					//s_matrices[s_visibleCapsuleCount] = Matrix4x4.TRS(capsule.transform.position, capsule.transform.rotation, Vector3.one);
					s_capsuleParams1[s_visibleCapsuleCount] = param1;
					s_capsuleParams2[s_visibleCapsuleCount] = param2;

					s_visibleCapsuleCount++;
				}
				Profiler.EndSample();
			}

			Shader.SetGlobalVectorArray(ShaderIDs.CapsuleParams1, s_capsuleParams1);
			Shader.SetGlobalVectorArray(ShaderIDs.CapsuleParams2, s_capsuleParams2);
			Shader.SetGlobalInt(ShaderIDs.CapsuleCount, s_visibleCapsuleCount);
		}

		public static void DrawCapsules(CommandBuffer cmd, Mesh mesh, int subMesh, Material material, int shaderPass)
		{
			cmd.DrawMeshInstanced(mesh, subMesh, material, shaderPass, s_matrices, s_visibleCapsuleCount);
		}

		private struct SortData : System.IComparable<SortData>
		{
			public int index;
			public float sortKey;
			public int CompareTo(SortData other) => sortKey.CompareTo(other.sortKey);
		}

		[BurstCompile]
		private static bool TestPlanesAABB(ref NativeArray<Plane> planes, ref float3 boundsCenter, ref float3 boundsSize)
		{
			for (int i = 0; i < planes.Length; i++)
			{
				Plane plane = planes[i];
				float3 normal_sign = math.sign(plane.normal);
				float3 test_point = boundsCenter + (boundsSize * 0.5f * normal_sign);

				float dot = math.dot(test_point, plane.normal);
				if (dot + plane.distance < 0)
					return false;
			}

			return true;
		}

		[BurstCompile]
		private struct SortKeyJob : IJobParallelFor
		{
			[NativeDisableParallelForRestriction]
			public NativeArray<SortData> sortdata;
			[NativeDisableParallelForRestriction]
			public NativeArray<Plane> frustum;
			public float3 cameraPosition;
			public float rangeMultiplier;

			public NativeArray<float3> point1;
			public NativeArray<float3> point2;
			public NativeArray<float> radius;

			public void Execute(int i)
			{
				float3 _point1 = point1[i];
				float3 _point2 = point2[i];
				float _radius = radius[i];
				float radiusSum = _radius + _radius * rangeMultiplier;
				float3 min = math.min(_point1, _point2) - radiusSum;
				float3 max = math.max(_point1, _point2) + radiusSum;
				float3 boundsCenter = (min + max) * 0.5f;
				float3 boundsSize = (max - min) * 0.5f;

				SortData data = sortdata[i];
				if (TestPlanesAABB(ref frustum, ref boundsCenter, ref boundsSize))
				{
					data.sortKey = math.distance(boundsCenter, cameraPosition) + math.EPSILON;
				}
				else
				{
					data.sortKey = float.PositiveInfinity;
				}
				sortdata[i] = data;
			}
		}

		[BurstCompile]
		private struct CacheJob : IJobParallelFor
		{
			public float rangeMultiplier;
			public NativeArray<float> size;
			public NativeArray<Matrix4x4> localToWorld;
			public NativeArray<float3> point1;
			public NativeArray<float3> point2;

			public void Execute(int i)
			{
				float _size = size[i];
				Matrix4x4 m = localToWorld[i];
				point1[i] = m.MultiplyPoint(new Vector3(0, 0, -_size * 0.5f));
				point2[i] = m.MultiplyPoint(new Vector3(0, 0, _size * 0.5f));
			}
		}

		[BurstCompile]
		private static void SortCapsules(ref NativeArray<SortData> sortData, ref NativeArray<Plane> frustum, ref float3 cameraPosition, ref float rangeMultiplier, ref NativeArray<float3> point1, ref NativeArray<float3> point2, ref NativeArray<float> radius)
		{
			SortKeyJob sortKeyJob = new SortKeyJob()
			{
				sortdata = sortData,
				frustum = frustum,
				cameraPosition = cameraPosition,
				rangeMultiplier = rangeMultiplier,
				point1 = point1,
				point2 = point2,
				radius = radius
			};
			sortKeyJob.Schedule(sortData.Length, 1).Complete();
			sortData.Sort();
		}

		private static void CacheDataForCamera(Camera camera, float rangeMultiplier)
		{
			for (int i = 0; i < instances.Count && s_visibleCapsuleCount < MAX_CAPSULE_COUNT; i++)
			{
				instances[i].CacheInstanceData(rangeMultiplier);
			}
		}

		private void CacheInstanceData(float rangeMultiplier)
		{
			var t = transform;
			m_cachedPoint1 = t.TransformPoint(new Vector3(0, 0, -size * 0.5f));
			m_cachedPoint2 = t.TransformPoint(new Vector3(0, 0, size * 0.5f));

			m_cachedRadiusSum = radius + radius * rangeMultiplier;
			float minX = (m_cachedPoint1.x < m_cachedPoint2.x ? m_cachedPoint1.x : m_cachedPoint2.x) - m_cachedRadiusSum;
			float minY = (m_cachedPoint1.y < m_cachedPoint2.y ? m_cachedPoint1.y : m_cachedPoint2.y) - m_cachedRadiusSum;
			float minZ = (m_cachedPoint1.z < m_cachedPoint2.z ? m_cachedPoint1.z : m_cachedPoint2.z) - m_cachedRadiusSum;
			float maxX = (m_cachedPoint1.x > m_cachedPoint2.x ? m_cachedPoint1.x : m_cachedPoint2.x) + m_cachedRadiusSum;
			float maxY = (m_cachedPoint1.y > m_cachedPoint2.y ? m_cachedPoint1.y : m_cachedPoint2.y) + m_cachedRadiusSum;
			float maxZ = (m_cachedPoint1.z > m_cachedPoint2.z ? m_cachedPoint1.z : m_cachedPoint2.z) + m_cachedRadiusSum;

			m_cachedBounds = new Bounds(new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
				new Vector3(maxX - minX, maxY - minY, maxZ - minZ));
		}

		private void OnEnable()
		{
			m_cachedTransform = transform;
			instances.Add(this);
			instanceTransforms.Add(transform);
		}

		private void OnDisable()
		{
			instances.Remove(this);
			instanceTransforms.Remove(transform);
		}

		private void OnDrawGizmosSelected()
		{
			Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
			DrawCapsuleGizmo(point1, point2, radius);
			Gizmos.color = Color.yellow;
			//DrawCapsuleGizmo(point1, point2, radius + OcclusionCapsuleManager.instance.maxDist);
		}

		private static void DrawCapsuleGizmo(Vector3 point1, Vector3 point2, float radius)
		{
			Gizmos.DrawWireSphere(point1, radius);
			Gizmos.DrawWireSphere(point2, radius);
			var N = (point2 - point1).normalized;
			var B = Vector3.up;
			var T = Vector3.Cross(B, N).normalized;
			B = Vector3.Cross(N, T);

			Gizmos.DrawLine(point1 - B * radius, point2 - B * radius);
			Gizmos.DrawLine(point1 + B * radius, point2 + B * radius);
			Gizmos.DrawLine(point1 - T * radius, point2 - T * radius);
			Gizmos.DrawLine(point1 + T * radius, point2 + T * radius);
		}
	}
}