using UnityEngine;

namespace CapsuleOcclusion
{
	public static class ShaderIDs
	{
		public static readonly int CapsuleParams1            = GetID("_CapsuleParams1");
		public static readonly int CapsuleParams2            = GetID("_CapsuleParams2");
		public static readonly int CapsuleCount              = GetID("_CapsuleCount");
		public static readonly int CameraPosition            = GetID("_CameraPosition");
		public static readonly int CameraForward             = GetID("_CameraForward");
		public static readonly int Range                     = GetID("_Range");
		public static readonly int FrustumRays               = GetID("_FrustumRays");
		public static readonly int ClusterSize               = GetID("_ClusterSize");
		public static readonly int Clusters                  = GetID("_Clusters");
		public static readonly int ClusterData               = GetID("_ClusterData");
		public static readonly int Counter                   = GetID("_Counter");
		public static readonly int ClustersPointer           = GetID("_ClustersPointer");
		public static readonly int ClustersCount             = GetID("_ClustersCount");
		public static readonly int ConservativeRasterization = GetID("_ConservativeRasterization");
		public static readonly int CapsuleClusters           = GetID("_CapsuleClusters");
		public static readonly int CapsuleClustersPointer    = GetID("_CapsuleClustersPointer");
		public static readonly int CapsuleClustersCount      = GetID("_CapsuleClustersCount");
		public static readonly int CapsuleClusterData        = GetID("_CapsuleClusterData");
		public static readonly int CapsuleOcclusionIntensity = GetID("_CapsuleOcclusionIntensity");
		public static readonly int DebugClusters             = GetID("_DebugClusters");
		public static readonly int DepthToRange              = GetID("_DepthToRange");

		private static int GetID(string name) => Shader.PropertyToID(name);
	}
}