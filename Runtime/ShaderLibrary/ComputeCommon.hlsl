#define MaxMemoryPageCount (256)
#define MaxMaterialCount (128)
#define MemoryPageMaxInstanceCount (1600.0)
#define LODSwitchThreshold (0.0005)
#define DepthPyramidMaxSamplingLevel (8)
#define DepthPyramidResolution (256)
#define DepthTestOffset (0.01) // only used for per-tri culling to counter precision issues and z-fighting caused by the depth pyramid's first mip

CBUFFER_START(PageStrideConstants)
uint VertexValuePageStride;
uint IndexValuePageStride;
uint GroupDataValuePageStride;
uint LoadableMemoryPageCount;
uint TotalInstanceCount;
uint TotalPageCount;
uint VisibilityFlagVectorCount;
uint CameraLoadDistanceThreshold;
CBUFFER_END

Texture2D _DepthPyramid;

uint MaterialCount;
uint SortLevel;
uint DispatchPassOffset;

RWByteAddressBuffer DrawArgsBufferUAV;
RWByteAddressBuffer ShadowDrawArgsBufferUAV;

ByteAddressBuffer DispatchArgsBufferSRV;
RWByteAddressBuffer DispatchArgsBufferUAV;

ByteAddressBuffer GroupDataBuffer; // float3: packed boundsCenter + boundsExtents, uint: materialID, uint: lodError
ByteAddressBuffer InstanceDataBuffer; // x: triangleIndexStart, y: triangleIndexCount, z: vertexIndexStart, w: lodData
RWByteAddressBuffer CompactedInstanceDataBuffer; // x: triangleIndexStart, y: triangleIndexCount, z: vertexIndexStart, w: lodData

ByteAddressBuffer TriangleDataBufferSRV;
RWByteAddressBuffer TriangleDataBufferUAV;
ByteAddressBuffer ShadowTriangleDataBufferSRV;
RWByteAddressBuffer ShadowTriangleDataBufferUAV;

ByteAddressBuffer PageDataBufferSRV;

ByteAddressBuffer FeedbackBufferSRV;
RWByteAddressBuffer FeedbackBufferUAV;

ByteAddressBuffer PageStatusBufferSRV;

ByteAddressBuffer TriangleBuffer;
RWByteAddressBuffer CompactedTriangleBuffer;

ByteAddressBuffer TriangleVisibilityBufferSRV;
RWByteAddressBuffer TriangleVisibilityBufferUAV;

ByteAddressBuffer VertexPositionBuffer;

groupshared uint FeedbackSortLDS[MaxMemoryPageCount];
groupshared uint TriangleCounterLDS = 0;
groupshared uint MaterialIndexLDS;
groupshared uint OffsetDataLDS;
groupshared uint MaterialScanCounterLDS = 0;

uint ReadTriangleVisibility(uint index)
{
    uint addr = floor(index / 32.0) * 4;
    uint location = index % 32;

    return (TriangleVisibilityBufferSRV.Load(addr) >> location) & 0x1;
}

void StoreTriangleVisibility(uint index)
{
    uint addr = floor(index / 32.0) * 4;
    uint location = index % 32;

	TriangleVisibilityBufferUAV.InterlockedOr(addr, 0x1 << location);
}

void StoreCompactedIndices(uint offset, uint3 indices)
{
    CompactedTriangleBuffer.Store3(offset * 4, indices);
    
    //uint3 addr = uint3(mad(3, offset, 0), mad(3, offset, 2), mad(3, offset, 4));
    
    //if (addr.x % 4 == 0)
    //{
    //    CompactedTriangleBuffer.Store(addr.x, (indices.x << 16) | indices.y);
    //    CompactedTriangleBuffer.InterlockedAnd(addr.z, 0xffff);
    //    CompactedTriangleBuffer.InterlockedOr(addr.z, indices.z << 16);
    //}
    //else
    //{
    //    CompactedTriangleBuffer.InterlockedAnd(addr.x - 2, ~0xffff);
    //    CompactedTriangleBuffer.InterlockedOr(addr.x - 2, indices.x);
    //    CompactedTriangleBuffer.Store(addr.y, (indices.y << 16) | indices.z);
    //}
}

bool TestClipPlane(half3 p0, half3 p1, half3 p2, const int index)
{
    half4 plane = unity_CameraWorldClipPlanes[index];
	return dot(half4(p0, 1.0), plane) <= 0.0 && dot(half4(p1, 1.0), plane) <= 0.0 && dot(half4(p2, 1.0), plane) <= 0.0;
}

bool TestClipPlane(half3 center, half radius, const int index)
{
    half4 plane = unity_CameraWorldClipPlanes[index];
	return dot(half4(center, 1.0), plane) <= -radius;
}

bool FrustumCullTriangle(half3 p0, half3 p1, half3 p2)
{
	return TestClipPlane(p0, p1, p2, 0) || TestClipPlane(p0, p1, p2, 1) || TestClipPlane(p0, p1, p2, 2) || TestClipPlane(p0, p1, p2, 3);
}

bool FrustumCullSphere(half3 center, half radius)
{
	return TestClipPlane(center, radius, 0) || TestClipPlane(center, radius, 1) || TestClipPlane(center, radius, 2) || TestClipPlane(center, radius, 3);
}

half4 ProjectAABBToScreenSpace(half3 center, half3 extents, out half depth)
{
	half4x4 viewProj = (half4x4)UNITY_MATRIX_VP;

    half4 sx = mul(viewProj, half4(2.0 * extents.x, 0.0, 0.0, 0.0));
    half4 sy = mul(viewProj, half4(0.0, 2.0 * extents.y, 0.0, 0.0));
    half4 sz = mul(viewProj, half4(0.0, 0.0, 2.0 * extents.z, 0.0));

	// to clip space
	half4 screenCorners[8];
	screenCorners[0] = mul(viewProj, half4(center - extents, 1.0));
	screenCorners[1] = screenCorners[0] + sz;
	screenCorners[2] = screenCorners[0] + sy;
	screenCorners[3] = screenCorners[2] + sz;
	screenCorners[4] = screenCorners[0] + sx;
	screenCorners[5] = screenCorners[4] + sz;
	screenCorners[6] = screenCorners[4] + sy;
	screenCorners[7] = screenCorners[6] + sz;

	{
		[unroll] for (int i = 0; i < 8; i++)
		{
#if UNITY_UV_STARTS_AT_TOP
			screenCorners[i].y = -screenCorners[i].y;
#endif
			// to ndc (projection bound by near z to force accept)
			screenCorners[i].w = max(screenCorners[i].w, (half)_ProjectionParams.y);
			screenCorners[i].xyz *= rcp(screenCorners[i].w);
			// to raster space
			screenCorners[i].xy = mad(screenCorners[i].xy, 0.5, 0.5);
		}
	}

	// compute screen-space rectangle
	half4 screenRect = half4(1.0, 1.0, 0.0, 0.0);
	half closestDepth = 0.0;
	{
		[unroll] for (int i = 0; i < 8; i++)
		{
			screenRect.xy = min(screenRect.xy, screenCorners[i].xy);
			screenRect.zw = max(screenRect.zw, screenCorners[i].xy);
#if defined(UNITY_REVERSED_Z)
			closestDepth = max(closestDepth, screenCorners[i].z);
#else
			closestDepth = min(closestDepth, screenCorners[i].z);
#endif
		}
	}

	depth = closestDepth;

	return screenRect;
}

bool ProjectTriangleToShadowCascade(half3 corner, out float3 screenCorner)
{
	// only frustum bounds in the currently selected quarter tile of the shadow map
	// (false negatives are possible for big triangles whose vertices are all outside)
#if defined(VMESH_SHADOW_CASCADE_0)
    screenCorner = mul(_MainLightWorldToShadow[0], float4(corner, 1.0)).xyz;
	return
		screenCorner.x < 0.0 || screenCorner.x > 0.5 ||
		screenCorner.y < 0.0 || screenCorner.y > 0.5;
#elif defined(VMESH_SHADOW_CASCADE_1)
	screenCorner = mul(_MainLightWorldToShadow[1], float4(corner, 1.0)).xyz;
	return
		screenCorner.x < 0.5 || screenCorner.x > 1.0 ||
		screenCorner.y < 0.0 || screenCorner.y > 0.5;
#elif defined(VMESH_SHADOW_CASCADE_2)
	screenCorner = mul(_MainLightWorldToShadow[2], float4(corner, 1.0)).xyz;
	return
		screenCorner.x < 0.0 || screenCorner.x > 0.5 ||
		screenCorner.y < 0.5 || screenCorner.y > 1.0;
#elif defined(VMESH_SHADOW_CASCADE_3)
	screenCorner = mul(_MainLightWorldToShadow[3], float4(corner, 1.0)).xyz;
	return
		screenCorner.x < 0.5 || screenCorner.x > 1.0 ||
		screenCorner.y < 0.5 || screenCorner.y > 1.0;
#else
    screenCorner = (float3)0;
    return true;
#endif
}

bool CheckCascadeCullingIndex(half3 pos)
{
#if defined(VMESH_SHADOW_CASCADE_0)
	return ComputeCascadeIndex(pos) >= half(0.0);
#elif defined(VMESH_SHADOW_CASCADE_1)
	return ComputeCascadeIndex(pos) >= half(1.0);
#elif defined(VMESH_SHADOW_CASCADE_2)
	return ComputeCascadeIndex(pos) >= half(2.0);
#elif defined(VMESH_SHADOW_CASCADE_3)
	return ComputeCascadeIndex(pos) >= half(3.0);
#else
    return true;
#endif
}

bool BackfaceVisibility(float3 v1, float3 v2, float3 v3)
{
    if (v1.z <= 0.0 || v2.z <= 0.0 || v3.z <= 0.0)
        return true;
	
    float2 v12 = v2.xy - v1.xy;
    float2 v13 = v3.xy - v1.xy;

    float positive = v12.y * v13.x;
    float negative = v12.x * v13.y;

	if (abs(positive) < 16777216.0)
		return negative < positive;
	else
		return negative <= positive;
}

// ref: https://gpuopen.com/geometryfx/
bool SmallTriangleVisibility(float4 rectangle, float2 viewport)
{
	const uint SUBPIXEL_BITS = 8;
	const uint SUBPIXEL_MASK = 0xff;
	const uint SUBPIXEL_SAMPLES = 1u << SUBPIXEL_BITS;

	uint4 screenSizeRect = mad(rectangle, viewport.xyxy, 0.5) * SUBPIXEL_SAMPLES;

	screenSizeRect = screenSizeRect & ~SUBPIXEL_MASK;
	return all(screenSizeRect.zw - screenSizeRect.xy);
}

uint ComputeSampleLevel(float4 rectangle)
{
    float2 size = (rectangle.zw - rectangle.xy) * (DepthPyramidResolution - 1);
	return min(firstbithigh(max(size.x, size.y) - 1) + 1, DepthPyramidMaxSamplingLevel);
}

half SampleDepthPyramid(uint level, float4 rectangle)
{
    uint4 coordinates = rectangle * max(DepthPyramidResolution >> level, 1);
    bool dx = coordinates.x != coordinates.z;
    bool dy = coordinates.y != coordinates.w;

	half depth = _DepthPyramid.mips[level][coordinates.xy].x;

	[branch] if (dx)
#if defined(UNITY_REVERSED_Z)
		depth = min(depth, _DepthPyramid.mips[level][coordinates.zy].x);
#else
		depth = max(depth, _DepthPyramid.mips[level][coordinates.zy].x);
#endif

	[branch] if (dy)
#if defined(UNITY_REVERSED_Z)
		depth = min(depth, _DepthPyramid.mips[level][coordinates.xw].x);
#else
		depth = max(depth, _DepthPyramid.mips[level][coordinates.xw].x);
#endif

	[branch] if (dx && dy)
#if defined(UNITY_REVERSED_Z)
		depth = min(depth, _DepthPyramid.mips[level][coordinates.zw].x);
#else
		depth = max(depth, _DepthPyramid.mips[level][coordinates.zw].x);
#endif

	return depth;
}
