MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	RWStructuredBuffer<uint> IndirectDrawCountBuffer < Attribute( "IndirectDrawCountBuffer" ); >;
	uint IndirectDrawCount < Attribute( "IndirectDrawCount" ); >;

	[numthreads( 1, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = IndirectDrawCount;
		IndirectDrawCountBuffer[0] = 6;  // IndexCountPerInstance
		IndirectDrawCountBuffer[1] = count;  // InstanceCount
		IndirectDrawCountBuffer[2] = 0;  // StartIndexLocation
		IndirectDrawCountBuffer[3] = 0;  // BaseVertexLocation
		IndirectDrawCountBuffer[4] = 0;  // StartInstanceLocation
	}	
}