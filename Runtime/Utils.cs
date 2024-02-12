using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CapsuleOcclusion
{
		public static class Utils
	{
		public static void GetFrustumRays(Matrix4x4 view, Matrix4x4 projection, Vector4[] rays)
		{
			view.m03 = 0;
			view.m13 = 0;
			view.m23 = 0;

			Matrix4x4 inverseViewProjection = (projection * view).inverse;

			rays[0] = inverseViewProjection.MultiplyPoint(new Vector3(-1,-1, 1)).normalized;
			rays[1] = inverseViewProjection.MultiplyPoint(new Vector3(-1, 1, 1)).normalized;
			rays[2] = inverseViewProjection.MultiplyPoint(new Vector3( 1, 1, 1)).normalized;
			rays[3] = inverseViewProjection.MultiplyPoint(new Vector3( 1,-1, 1)).normalized;
		}

		public static void LazyLoadResource<T>(ref T obj, string path) where T : Object
		{
			if (obj == null)
			{
				obj = Resources.Load<T>(path);
			}

			Debug.Assert(obj != null);
		}

		public static void LazyCreate(ref CommandBuffer cmd, string name)
		{
			if (cmd == null)
			{
				cmd = new CommandBuffer() { name = name };
			}

			Debug.Assert(cmd != null);
		}

		public static void Release(ref RenderTexture rt)
		{
			if (rt != null)
			{
				rt.Release();
				rt = null;
			}
		}

		public static void Release(ref ComputeBuffer buffer)
		{
			if (buffer != null)
			{
				buffer.Release();
				buffer = null;
			}
		}

		public static void LazyCreate(ref ComputeBuffer buffer, string name, int count, int stride, ComputeBufferType type = ComputeBufferType.Default)
		{
			if (buffer == null || buffer.count != count || buffer.stride != stride)
			{
				Release(ref buffer);
				buffer = new ComputeBuffer(count, stride, type)
				{
					name = name
				};
			}

			Debug.Assert(buffer != null);
		}

		public static void LazyCreate(ref RenderTexture rt, string name, int width, int height, int depth, RenderTextureFormat format)
		{
			if (rt == null || rt.width != width || rt.height != height || rt.volumeDepth != depth || rt.format != format)
			{
				Release(ref rt);
				rt = new RenderTexture(width, height, 0, format)
				{
					name = name,
					enableRandomWrite = true,
					dimension = depth > 1 ? TextureDimension.Tex3D : TextureDimension.Tex2D,
					volumeDepth = depth
				};
				rt.Create();
			}

			Debug.Assert(rt != null);
		}

		public static int GetThreadGroupCount(int groupSize, int size)
		{
			return Mathf.CeilToInt((float)size / groupSize);
		}
		public static Vector2Int GetThreadGroupCount(int groupSize, Vector2Int size)
		{
			return new Vector2Int(GetThreadGroupCount(groupSize, size.x), GetThreadGroupCount(groupSize, size.y));
		}
		public static Vector3Int GetThreadGroupCount(int groupSize, Vector3Int size)
		{
			return new Vector3Int(GetThreadGroupCount(groupSize, size.x), GetThreadGroupCount(groupSize, size.y), GetThreadGroupCount(groupSize, size.z));
		}

		public static void DispatchCompute(this CommandBuffer cmd, ComputeShader computeShader, int kernelIndex, Vector2Int threadGroupCount)
		{
			cmd.DispatchCompute(computeShader, kernelIndex, threadGroupCount.x, threadGroupCount.y, 1);
		}
		public static void DispatchCompute(this CommandBuffer cmd, ComputeShader computeShader, int kernelIndex, Vector3Int threadGroupCount)
		{
			cmd.DispatchCompute(computeShader, kernelIndex, threadGroupCount.x, threadGroupCount.y, threadGroupCount.z);
		}

		public static void LazyGetComponent<T>(this GameObject gameObject, ref T component)
		{
			if (component == null)
			{
				gameObject.TryGetComponent<T>(out component);
			}
		}
	}
}