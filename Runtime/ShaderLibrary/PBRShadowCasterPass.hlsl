#ifndef VMESH_SG_SHADOW_PASS_INCLUDED
#define VMESH_SG_SHADOW_PASS_INCLUDED

#include "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/Common.hlsl"

PackedVaryings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Varyings output = BuildVirtualMeshVaryings(vertexID, instanceID);
    PackedVaryings packedOutput = (PackedVaryings)0;
    packedOutput = PackVaryings(output);
    return packedOutput;
}

half4 frag(PackedVaryings packedInput) : SV_TARGET
{
    Varyings unpacked = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(unpacked);
    SurfaceDescription surfaceDescription = BuildSurfaceDescription(unpacked);

    #if defined(_ALPHATEST_ON)
        clip(surfaceDescription.Alpha - surfaceDescription.AlphaClipThreshold);
    #endif

    #if defined(LOD_FADE_CROSSFADE) && USE_UNITY_CROSSFADE
        LODFadeCrossFade(unpacked.positionCS);
    #endif

    return 0;
}

#endif
