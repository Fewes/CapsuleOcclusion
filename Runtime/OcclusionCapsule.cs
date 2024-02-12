using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace CapsuleOcclusion
{
	[ExecuteInEditMode]
	public class OcclusionCapsule : MonoBehaviour
	{
		public const int MAX_CAPSULE_COUNT = 512;

		public static List<OcclusionCapsule> instances = new List<OcclusionCapsule>();
		public static int visibleCapsuleCount => s_visibleCapsuleCount;

		[Min(0)]
		public float size = 1f;
		[Min(0)]
		public float radius = 0.1f;

		public Vector3 point1 => transform.TransformPoint(new Vector3(0, 0, -size / 2));
		public Vector3 point2 => transform.TransformPoint(new Vector3(0, 0, size / 2));

		private Vector3 m_cachedPoint1;
		private Vector3 m_cachedPoint2;
		private float m_cachedRadiusSum;
		private Bounds m_cachedBounds;
		private float m_sortKey;

		private static int s_visibleCapsuleCount;
		private static Plane[] s_frustumPlanes = new Plane[6];
		private static List<OcclusionCapsule> s_visibleCapsules = new List<OcclusionCapsule>();
		// Note that this is just a dummy. Matrices are calculated on the fly in the vertex shader to save CPU time.
		private static Matrix4x4[] s_matrices = new Matrix4x4[MAX_CAPSULE_COUNT];
		private static Vector4[] s_capsuleParams1 = new Vector4[MAX_CAPSULE_COUNT];
		private static Vector4[] s_capsuleParams2 = new Vector4[MAX_CAPSULE_COUNT];

		public static void UpdateCapsuleDataForCamera(Camera camera, int maxCount, float rangeMultiplier, bool cull, bool sort)
		{
			CacheDataForCamera(camera, rangeMultiplier);

			int count;

			// Hack bug fix (strange results if maxCount >= 512 for some reason)
			maxCount = Mathf.Min(maxCount, MAX_CAPSULE_COUNT - 1);

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
				Vector3 cameraPosition = camera.transform.position;
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
				//s_matrices[s_visibleCapsuleCount] = Matrix4x4.TRS(capsule.transform.position, capsule.transform.rotation, Vector3.one);
				s_capsuleParams1[s_visibleCapsuleCount] = param1;
				s_capsuleParams2[s_visibleCapsuleCount] = param2;

				s_visibleCapsuleCount++;
			}
			Profiler.EndSample();

			Shader.SetGlobalVectorArray(ShaderIDs.CapsuleParams1, s_capsuleParams1);
			Shader.SetGlobalVectorArray(ShaderIDs.CapsuleParams2, s_capsuleParams2);
			Shader.SetGlobalInt(ShaderIDs.CapsuleCount, s_visibleCapsuleCount);
		}

		public static void DrawCapsules(CommandBuffer cmd, Mesh mesh, int subMesh, Material material, int shaderPass)
		{
			cmd.DrawMeshInstanced(mesh, subMesh, material, shaderPass, s_matrices, s_visibleCapsuleCount);
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
			instances.Add(this);
		}

		private void OnDisable()
		{
			instances.Remove(this);
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

		public void DrawBoundsGizmo()
		{
			Gizmos.color = new Color(1, 0, 0, 0.1f);
			Gizmos.DrawCube(m_cachedBounds.center, m_cachedBounds.size);
			Gizmos.color = new Color(1, 0, 0, 1);
			Gizmos.DrawWireCube(m_cachedBounds.center, m_cachedBounds.size);
		}
	}
}