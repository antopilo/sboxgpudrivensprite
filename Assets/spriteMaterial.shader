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

	bool IsSphereInsideFrustum(float4 planes[6], float3 center, float radius)
	{
		for (int i = 0; i < 6; ++i)
		{
			int planeDebugIndex = i;

			float4 plane = planes[i];
			float3 planeNormal = plane.xyz;
			float d = dot(planeNormal, center) + plane.w - (radius * 2.0);
			if (d > -radius) 
				return false;
		}
		return true;
	}

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
	StructuredBuffer<float4> CullingData < Attribute("CullingData"); >; 

	PixelInput MainVs( VertexInput i)
	{
		PixelInput o = ProcessVertex( i ); 

		// Extract sprite position from world transform
		uint ogDrawCall = i.instanceID;
		uint spriteIndex = SortedSpriteHandles[ogDrawCall];
		float4x4 finalTransform = SpriteDatas[spriteIndex].Transform;
		
		if(SpriteDatas[spriteIndex].BillboardMode <= 1)
		{
			// TODO Lookat shadows
		//#if !S_MODE_DEPTH
			float3 spritePos = transpose(finalTransform)[3].xyz; 
			
		//#endif
			float4x4 view = g_matWorldToView;  

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

			// Create Frustum
			float4 planes[6];
			planes[0] = CullingData[0];
			planes[1] = CullingData[1];
			planes[2] = CullingData[2];
			planes[3] = CullingData[3];
			planes[4] = CullingData[4];
			planes[5] = CullingData[5];

			float3 leftHandedPos = float3(spritePos.x, spritePos.y, spritePos.z);
			bool isCulled = IsSphereInsideFrustum(planes, leftHandedPos.xyz, 200.0f);
			if(!isCulled)
			{
				//ogDrawCall = 1;
			}
			else
			{
				//ogDrawCall = 0;
			}

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
	RenderState( BlendEnable, false );
	RenderState( AlphaToCoverageEnable, true );
	RenderState( AlphaToCoverageEnable, true );
	RenderState( BlendOpAlpha, ADD);

	#define S_TRANSLUCENT 0

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
	StructuredBuffer<float4> CullingData < Attribute("CullingData"); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i ); 
		m.Metalness = 0.0f; // Forces the object to be metalic
		Texture2D ColorTexture = Bindless::GetTexture2D( NonUniformResourceIndex(SpriteDatas[i.instanceID].ColorTextureIndex), true );
		Texture2D NormalTexture = Bindless::GetTexture2D( NonUniformResourceIndex(SpriteDatas[i.instanceID].NormalTextureIndex), false );
		SamplerState MyPixelySampler < Filter( Point ); >;

		float4 tintColor = SpriteDatas[i.instanceID].TintColor;
		m.Albedo = ColorTexture.Sample( g_sPointWrap, i.vTextureCoords.xy ).rgb * tintColor.rgb;   
		m.Normal = NormalTexture.Sample( g_sPointWrap, i.vTextureCoords.xy).rgb;
		m.Opacity = tintColor.a; 

		float debugCol = i.drawOrder / 19.0f;
		m.Albedo.rgb = float3(debugCol, 0, 0);
		m.Transmission = float3(tintColor.a, tintColor.a, tintColor.a);
		return ShadingModelStandard::Shade( i, m );
	}
}
