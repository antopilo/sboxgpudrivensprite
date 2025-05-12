MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	struct SpriteData
	{
		float4x4 Transform;
		int ColorTextureIndex;
		int NormalTextureIndex;
		int BillboardMode;
		float4 TintColor;
	}; 
	RWStructuredBuffer<SpriteData> AtomicBindlessSprites < Attribute( "AtomicBindlessSprites" ); >; // out
	RWStructuredBuffer<SpriteData> Sprites < Attribute( "Sprites" ); >; // in

	RWStructuredBuffer<uint> AtomicCounter < Attribute( "AtomicCounter" ); >; // push index
	StructuredBuffer<uint> SortedIDs < Attribute( "SortedIDs" ); >;
	StructuredBuffer<float4> CullingPlanes < Attribute("CullingPlanes"); >; // frustum plane
	RWStructuredBuffer<uint> CulledSortedMapping < Attribute("culledSortedMapping"); >;
	RWStructuredBuffer<uint> SortedMapping < Attribute("sortedMapping"); >;

	uint SpriteCount < Attribute( "SpriteCount" ); >;

	bool IsSphereInsideFrustum(float3 center, float radius)
	{
		for (int i = 0; i < 6; ++i)
		{
			float4 plane = CullingPlanes[i];
			float3 planeNormal = plane.xyz;
			float d = dot(planeNormal, center) + plane.w - (radius * 2.0);
			if (d > -radius) 
			{
				return false; 
			}
		}
		
		return true;
	}

	[numthreads( 128, 1, 1 )] 
	void MainCs( uint3 id : SV_DispatchThreadID )
	{	
		uint currentIndex = id.x + id.y * 128;
		if(currentIndex > SpriteCount)
		{
			return; 
		}

		int orderedIndex = currentIndex;
		float3 spritePosition = transpose(Sprites[orderedIndex].Transform)[3].xyz;
		if(IsSphereInsideFrustum(spritePosition, 200.0f))
		{
			uint index;
    		InterlockedAdd(AtomicCounter[0], 1, index);
			AtomicBindlessSprites[index] = Sprites[orderedIndex];
			CulledSortedMapping[index] = index;
		}
	}	
}