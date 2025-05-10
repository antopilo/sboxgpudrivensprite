CS
{
	#include "system.fxc"

	RWTexture2D<float4> Result < Attribute( "Result" ); >;

	StructuredBuffer<float4> CullingPlanes < Attribute("CullingPlanes"); >; 

	// Out
	struct VisResult
	{
		int SpriteIndex;
		bool Visible;
	};
	StructuredBuffer<VisResult> VisiblityResults < Attribute("VisiblityResults"); >;
	bool IsSphereInsideFrustum(float4 planes[6], float3 center, float radius)
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

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		Result[ id.xy ] = float4( id.x & id.y, ( id.x & 15 ) / 15.0, ( id.y & 15 ) / 15.0, 0.0 );
	}	
}