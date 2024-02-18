using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using System.Globalization;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CapsuleOcclusion
{
	[ExecuteInEditMode, ImageEffectAllowedInSceneView, RequireComponent(typeof(Camera))]
	public class CapsuleOcclusionCamera : MonoBehaviour
	{
		[System.Serializable]
		public enum ClusteringMethod
		{
			Rasterizer,
			Compute
		}

		[Tooltip("The resolution of the 3D acceleration texture (clusters). It is recommended to use multiples of 8 for all dimensions.")]
		public Vector3Int clusterSize = new Vector3Int(64, 64, 32);
		[Range(4, 32)] [Tooltip("Size multiplier of the cluster linked list on the GPU. Setting this too low will result in visual bugs if many capsules overlap.")]
		public int clusterDataHeadroom = 16;
		[Min(0)] [Tooltip("The maximum distance from the camera that occlusion will be applied. A shorter range makes clustering more effective.")]
		public float maxCameraRange = 50;
		[Range(16, OcclusionCapsule.MAX_CAPSULE_COUNT)]
		[Tooltip("The maximum number of active occlusion capsules for a single camera.")]
		public int maxCapsuleCount = 256;
		[Range(1, 8)] [Tooltip("The maximum range of occlusion influence of all capsules, scaled with their radii.")]
		public float capsuleRangeMultiplier = 4;
		[Range(0, 100)] [Tooltip("The strength of the occlusion effect.")]
		public float intensity = 10;

		[Space(10)]
		[Tooltip("Frustum cull capsules. This ensures only visible capsules are processed.")]
		public bool cullCapsules = true;
		[Tooltip("Sort capsules by distance from camera. This ensures capsules closer to the camera are prioritized.")]
		public bool sortCapsules = true;
		[Tooltip("Use Burst-compiled code path for culling and sorting.")]
		public bool burst = true;

		[Space(10)]

		[Tooltip("The method used when updating clusters. Rasterization is the preferred method as it is both faster and more accurate.")]
		public ClusteringMethod clusteringMethod = ClusteringMethod.Rasterizer;
		[Tooltip("Use (software) conservative rasterization. This improves clustering accuracy and should always be enabled.")]
		public bool conservativeRasterization = true;
		[Tooltip("Can be used to toggle cluster updates on/off for testing purposes.")]
		public bool updateClusters = true;

		[Space(10)]

		public bool showCapsules = false;
		public bool debugClusters = false;

		private Camera m_camera;
		private Vector4[] m_frustumRays = new Vector4[4];
		private CommandBuffer m_cmd;
		private ComputeShader m_compute;
		private RenderTexture m_rasterTarget;
		private RenderTexture m_clusterCount;
		private RenderTexture m_clusterPointer;
		private RenderTexture m_clusters;
		private ComputeBuffer m_clusterData;
		private ComputeBuffer m_counter;
		private uint[] m_counterData = new uint[1];
		private Material m_material;
		private Mesh m_clusterMesh;

		private const int CS_KERNEL_RESET = 0;
		private const int CS_KERNEL_UPDATE = 1;
		private const int CS_KERNEL_MERGE = 2;

		private void OnValidate()
		{
			clusterSize.x = Mathf.Clamp(clusterSize.x, 8, 128);
			clusterSize.y = Mathf.Clamp(clusterSize.y, 8, 128);
			clusterSize.z = Mathf.Clamp(clusterSize.z, 8, 128);
		}

		private void OnDestroy()
		{
			Release();
		}

		private void Release()
		{
			Utils.Release(ref m_rasterTarget);
			Utils.Release(ref m_clusterCount);
			Utils.Release(ref m_clusterPointer);
			Utils.Release(ref m_clusters);
			Utils.Release(ref m_clusterData);
			Utils.Release(ref m_counter);
		}

		private void LazyInitialize()
		{
			gameObject.LazyGetComponent(ref m_camera);
			Utils.LazyCreate(ref m_cmd, "Capsule Occlusion");
			Utils.LazyLoadResource(ref m_compute, "Shaders/CapsuleOcclusion");
			Utils.LazyLoadResource(ref m_material, "Materials/CapsuleOcclusion");
			Utils.LazyLoadResource(ref m_clusterMesh, "Meshes/CapsuleClusterMesh");
		}

		private void OnPreRender()
		{
			if (!enabled)
			{
				return;
			}

			LazyInitialize();

			OcclusionCapsule.UpdateCapsuleDataForCamera(m_camera, maxCapsuleCount, capsuleRangeMultiplier, cullCapsules, sortCapsules, burst);

			if (updateClusters)
			{
				UpdateClusters(m_camera);
			}
		}

		private void OnPostRender()
		{
			if (showCapsules)
			{
				m_cmd.Clear();
				m_cmd.ClearRenderTarget(true, false, Color.clear);
				OcclusionCapsule.DrawCapsules(m_cmd, m_clusterMesh, 0, m_material, 1);
				Graphics.ExecuteCommandBuffer(m_cmd);
			}
		}

		private void UpdateClusters(Camera camera)
		{
			Utils.LazyCreate(ref m_rasterTarget, "Raster Target", clusterSize.x, clusterSize.y, 1, RenderTextureFormat.R8);
			Utils.LazyCreate(ref m_clusterCount, "Clusters (Count)", clusterSize.x, clusterSize.y, clusterSize.z, RenderTextureFormat.RInt);
			Utils.LazyCreate(ref m_clusterPointer, "Clusters (Pointer)", clusterSize.x, clusterSize.y, clusterSize.z, RenderTextureFormat.RInt);
			Utils.LazyCreate(ref m_clusters, "Clusters", clusterSize.x, clusterSize.y, clusterSize.z, RenderTextureFormat.RGInt);
			Utils.LazyCreate(ref m_clusterData, "Cluster Data", clusterSize.x * clusterSize.y * clusterSize.z * clusterDataHeadroom, sizeof(uint) * 2);
			Utils.LazyCreate(ref m_counter, "Counter", 1, sizeof(uint));

			// Reset
			m_counterData[0] = 0;
			m_counter.SetData(m_counterData);
			m_cmd.Clear();

			// Camera etc
			Utils.GetFrustumRays(camera.worldToCameraMatrix, camera.projectionMatrix, m_frustumRays);
			m_cmd.SetGlobalVector(ShaderIDs.CameraPosition, camera.transform.position);
			m_cmd.SetGlobalVector(ShaderIDs.CameraForward, camera.transform.forward);
			m_cmd.SetGlobalFloat(ShaderIDs.Range, Mathf.Min(maxCameraRange, camera.farClipPlane));
			m_cmd.SetGlobalVectorArray(ShaderIDs.FrustumRays, m_frustumRays);
			m_cmd.SetGlobalVector(ShaderIDs.ClusterSize, (Vector3)clusterSize);

			// Reset clusters
			m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_RESET, ShaderIDs.ClustersPointer, m_clusterPointer);
			m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_RESET, ShaderIDs.ClustersCount, m_clusterCount);
			m_cmd.DispatchCompute(m_compute, CS_KERNEL_RESET, Utils.GetThreadGroupCount(8, clusterSize));

			if (clusteringMethod == ClusteringMethod.Compute)
			{
				m_cmd.SetComputeBufferParam(m_compute, CS_KERNEL_UPDATE, ShaderIDs.ClusterData, m_clusterData);
				m_cmd.SetComputeBufferParam(m_compute, CS_KERNEL_UPDATE, ShaderIDs.Counter, m_counter);
				m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_UPDATE, ShaderIDs.ClustersPointer, m_clusterPointer);
				m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_UPDATE, ShaderIDs.ClustersCount, m_clusterCount);
				m_cmd.DispatchCompute(m_compute, CS_KERNEL_UPDATE, Utils.GetThreadGroupCount(8, clusterSize));
			}
			else // Rasterizer
			{
				m_cmd.SetGlobalFloat(ShaderIDs.ConservativeRasterization, conservativeRasterization ? 1 : 0);
				m_cmd.SetRenderTarget(m_rasterTarget);
				m_cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
				m_cmd.SetRandomWriteTarget(1, m_clusterCount);
				m_cmd.SetRandomWriteTarget(2, m_clusterPointer);
				m_cmd.SetRandomWriteTarget(3, m_clusterData);
				m_cmd.SetRandomWriteTarget(4, m_counter);
				OcclusionCapsule.DrawCapsules(m_cmd, m_clusterMesh, 0, m_material, 0);
				m_cmd.ClearRandomWriteTargets();
			}

			// Merge to single texture
			m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_MERGE, ShaderIDs.ClustersPointer, m_clusterPointer);
			m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_MERGE, ShaderIDs.ClustersCount, m_clusterCount);
			m_cmd.SetComputeTextureParam(m_compute, CS_KERNEL_MERGE, ShaderIDs.Clusters, m_clusters);
			m_cmd.DispatchCompute(m_compute, CS_KERNEL_MERGE, Utils.GetThreadGroupCount(8, clusterSize));

			// Upload
			m_cmd.SetGlobalTexture(ShaderIDs.CapsuleClusters, m_clusters);
			m_cmd.SetGlobalTexture(ShaderIDs.CapsuleClustersPointer, m_clusterPointer);
			m_cmd.SetGlobalTexture(ShaderIDs.CapsuleClustersCount, m_clusterCount);
			m_cmd.SetGlobalBuffer(ShaderIDs.CapsuleClusterData, m_clusterData);
			m_cmd.SetGlobalFloat(ShaderIDs.CapsuleOcclusionIntensity, intensity);
			m_cmd.SetGlobalFloat(ShaderIDs.DebugClusters, debugClusters ? 1 : 0);
			m_cmd.SetGlobalFloat(ShaderIDs.DepthToRange, Mathf.Max(1, camera.farClipPlane / maxCameraRange));

			Graphics.ExecuteCommandBuffer(m_cmd);
		}

		public int GetByteCount()
		{
			int byteCount = 0;
			int pixelCount = clusterSize.x * clusterSize.y;
			int voxelCount = clusterSize.x * clusterSize.y * clusterSize.z;
			byteCount += pixelCount * 8; // m_rasterTarget
			byteCount += voxelCount * sizeof(uint) * 2; // m_clusters
			byteCount += voxelCount * sizeof(uint); // m_clusterPointer
			byteCount += voxelCount * sizeof(uint); // m_clusterCount
			byteCount += voxelCount * clusterDataHeadroom * sizeof(uint) * 2; // m_clusterData
			byteCount += sizeof(uint); // m_counter
			return byteCount;
		}

		private void Update()
		{
			// Dummy
		}
	}

#if UNITY_EDITOR
	[CustomEditor(typeof(CapsuleOcclusionCamera))]
	public class OcclusionCapsuleManagerEditor : Editor
	{
		private NumberFormatInfo m_nfi;

		public override bool RequiresConstantRepaint() => true;

		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			CapsuleOcclusionCamera manager = target as CapsuleOcclusionCamera;

			EditorGUILayout.Space();

			GUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Cluster Count", GUILayout.Width(EditorGUIUtility.labelWidth));
			if (m_nfi == null)
			{
				m_nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
				m_nfi.NumberGroupSeparator = " ";
			}
			EditorGUILayout.LabelField((manager.clusterSize.x * manager.clusterSize.y * manager.clusterSize.z).ToString("#,0", m_nfi));
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("VRAM Allocated", GUILayout.Width(EditorGUIUtility.labelWidth));
			EditorGUILayout.LabelField(((manager.GetByteCount() / 1024) / 1024).ToString("#,0", m_nfi) + " MB");
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Active Capsules", GUILayout.Width(EditorGUIUtility.labelWidth));
			EditorGUILayout.LabelField(OcclusionCapsule.instances.Count.ToString());
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Visible Capsules", GUILayout.Width(EditorGUIUtility.labelWidth));
			EditorGUILayout.LabelField(OcclusionCapsule.visibleCapsuleCount.ToString());
			GUILayout.EndHorizontal();
		}
	}
#endif
}