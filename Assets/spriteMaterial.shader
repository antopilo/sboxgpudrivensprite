FEATURES
{
    #include "common/features.hlsl"
}

MODES
{
    Forward();
    Depth(S_MODE_DEPTH);
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
    uint instanceID : SV_InstanceID;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	uint instanceID : TEXCOORD8; 
	uint drawOrder : TEXCOORD9; 
};

VS
{
	#include "common/vertex.hlsl"

	struct SpriteData
	{
		float4x4 Transform;
		int ColorTextureIndex;
		int NormalTextureIndex;
		int BillboardMode;
		float4 TintColor;
	};

	StructuredBuffer<SpriteData> SpriteDatas < Attribute("SpriteDatas"); >; 
	StructuredBuffer<uint> SortedSpriteHandles < Attribute("SortedBuffer"); >; 
	StructuredBuffer<float> SortedSpriteDistances < Attribute("Distances"); >; 
	StructuredBuffer<uint> SortedMapping < Attribute("sortedMapping"); >;

	float3 CamPosition < Attribute( "CamPosition" ); >;
	float4x4 WorldToView < Attribute( "WorldToView" ); >;

	float4x4 LookAtRH(float3 eye, float3 target, float3 up)
	{
		float3 zaxis = normalize(target - eye);        // Forward
		float3 xaxis = normalize(cross(up, zaxis));    // Right
		float3 yaxis = cross(zaxis, xaxis);            // Up

		float4x4 view;

		view[0] = float4(xaxis.x, yaxis.x, zaxis.x, 0.0f);
		view[1] = float4(xaxis.y, yaxis.y, zaxis.y, 0.0f);
		view[2] = float4(xaxis.z, yaxis.z, zaxis.z, 0.0f);
		view[3] = float4(-dot(xaxis, eye), -dot(yaxis, eye), -dot(zaxis, eye), 1.0f);

		return view;
	}

	PixelInput MainVs( VertexInput i)
	{
		PixelInput o = ProcessVertex( i ); 

		// Extract sprite position from world transform
		uint ogDrawCall = i.instanceID;
		uint spriteIndex = SortedMapping[i.instanceID];
		float4x4 finalTransform = SpriteDatas[spriteIndex].Transform;
		
		if(SpriteDatas[spriteIndex].BillboardMode <= 1)
		{
			float3 spritePos = transpose(finalTransform)[3].xyz; 
			float4x4 view = g_matWorldToView; 

			// This is a hack to avoid self-shadowing sprites.
			// Basically we still use a look at view matrix that looks at the camera
			// and not the view matrix of the light. This ensures that shadows and geometry are
			// still in "sync"
			#if S_MODE_DEPTH
			view = WorldToView;
			#endif

			if(SpriteDatas[spriteIndex].BillboardMode == 1)
			{
				// To lock the Z rotation axis: flatten the forward direction on the X & Y axis
				float3 camRight = view[0].xyz;
				float3 camForward = normalize(view[1].xyz);
				camForward.z = 0.0f;
				camForward = normalize(camForward); 
				
				// Rebuild rotation matrix 
				view[0] = float4(camRight, 0.0f);
				view[2] = float4(camForward, 0.0f);
				view[1] = float4(0.0f, 0.0f, 1.0f, 0.0f);
			}

			// Move the sprite to its original position
			view[3] = float4(spritePos.xyz, 1.0);
			view = transpose(view);

			finalTransform = view;
		}

		o.vPositionWs = mul(finalTransform, float4( i.vPositionOs.xyz, 1 ) ).xyz;
    	o.vPositionPs = Position3WsToPs( o.vPositionWs );
		
		// Add your vertex manipulation functions here
		o.instanceID = spriteIndex;
		o.drawOrder = ogDrawCall;
		return FinalizeVertex( o );
	}
}

PS
{
    #include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	RenderState ( BlendEnable, true);
	RenderState ( BlendOp, ADD);

	struct SpriteData
	{
		float4x4 Transform;
		int ColorTextureIndex;
		int NormalTextureIndex;
		int BillboardMode;
		float4 TintColor;
	};

	
	StructuredBuffer<SpriteData> SpriteDatas < Attribute("SpriteDatas"); >; 
	StructuredBuffer<uint> SortedSpriteHandles < Attribute("SortedBuffer"); >; 
	StructuredBuffer<float> SortedSpriteDistances < Attribute("Distances"); >; 
	StructuredBuffer<uint> SortedMapping < Attribute("sortedMapping"); >;


	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i ); 
		m.Metalness = 0.0f; // Forces the object to be metalic
		uint spriteIndex = i.instanceID;
		Texture2D ColorTexture = Bindless::GetTexture2D( NonUniformResourceIndex(SpriteDatas[spriteIndex].ColorTextureIndex), true );
		Texture2D NormalTexture = Bindless::GetTexture2D( NonUniformResourceIndex(SpriteDatas[spriteIndex].NormalTextureIndex), false );
		SamplerState MyPixelySampler < Filter( Point ); >;

		float4 tintColor = SpriteDatas[spriteIndex].TintColor;
		m.Albedo = ColorTexture.Sample( g_sPointWrap, i.vTextureCoords.xy ).rgb * tintColor.rgb;   
		m.Normal = NormalTexture.Sample( g_sPointWrap, i.vTextureCoords.xy).rgb;
		m.Opacity = tintColor.a; 

		float debugDrawNum = i.drawOrder / 4.0f;
		m.Albedo = float3(debugDrawNum, 0, 0);
		m.Transmission = float3(m.Albedo);
		return ShadingModelStandard::Shade( i, m );
	}
}
