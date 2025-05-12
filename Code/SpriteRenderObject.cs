// #define DEBUGCULL

using Sandbox;
using Sandbox.Internal;
using Sandbox.Rendering;
using Sandbox.UI;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml.Linq;
using static Sandbox.VertexLayout;
using static System.Runtime.InteropServices.JavaScript.JSType;

// This is a sprite render object that will hold multiple instances of a sprite
// And batch them
struct SpriteData
{
	public Matrix4x4 Transform;
	public int ColorTextureIndex;
	public int NormalTextureIndex;
	public int BillboardMode;
	public Vector4 TintColor;
}

public sealed class SpriteRenderObject : SceneCustomObject
{
	public enum CullMethod
	{ 
		GPU,
		CPU,
		None
	}

	public enum SortMode
	{
		GPU,
		CPU,
		None
	}

	private Scene Scene;

	private CullMethod RendererCullMethod = CullMethod.GPU;
	private SortMode RendererSortMode = SortMode.GPU;

	// Max amount of rendered sprites
	private const int MaxSprites = 25000;

	// Geometry stuff
	public Material spriteMat;
	private Mesh spriteMesh;
	private Model spriteModel;

	// Shaders & Stages
	public ComputeShader computeShader;
	public ComputeShader bitonicShader;
	public Shader shader;

	// GPU driven stuff
	List<BatchedSpriteComponent> sprites { get; set; } = new();
	private GpuBuffer gpuBuffer;
	private GpuBuffer indirectDrawCount;

	// Sorting
	private GpuBuffer distancesBuffer;
	private GpuBuffer IdBuffer;
	private int SortCount = 0; // Power of 2 size for GPU Sorting

	// Culling
	GpuBuffer CullData;
	public ComputeShader sphereCullShader;
	private GpuBuffer atomicDispatchCounter;
	private GpuBuffer atomicBindlessSprites;
	private GpuBuffer sortedCulledMap;

	private int spritesToRenderCount = 0;
	public int InstanceCount => sprites.Count;

	public SpriteRenderObject( SceneWorld world, Scene scene ) : base( world )
	{
		Scene = scene;
	}

	public void InitMesh()
	{
		Flags.CastShadows = true;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;

		// Create sprite mesh
		{
			spriteMesh = new Mesh( spriteMat );
			const float spriteSize = 50f;
			spriteMesh.CreateVertexBuffer<Vertex>( 4, Vertex.Layout, new Vertex[] {
				new ( new ( -spriteSize, -spriteSize, 0 ), Vector3.Up, Vector3.Forward, new ( 0, 0, 0, 0 ) ),
				new ( new (  spriteSize, -spriteSize, 0 ), Vector3.Up, Vector3.Forward, new ( 1, 0, 0, 0 ) ),
				new ( new (  spriteSize,  spriteSize, 0 ), Vector3.Up, Vector3.Forward, new ( 1, 1, 0, 0 ) ),
				new ( new ( -spriteSize,  spriteSize, 0 ), Vector3.Up, Vector3.Forward, new ( 0, 1, 0, 0 ) ),
			} );
			spriteMesh.CreateIndexBuffer( 6, new[] { 0, 1, 2, 0, 2, 3 } );
			spriteMesh.SetIndexRange( 0, 6 );
			spriteMesh.Bounds = BBox.FromPositionAndSize( 0, spriteSize );
			spriteModel = new ModelBuilder().AddMesh( spriteMesh ).Create();
		}

		// GPU Driven stuff
		{
			gpuBuffer = new GpuBuffer<SpriteData>( MaxSprites );
			indirectDrawCount = new GpuBuffer<uint>( 5, GpuBuffer.UsageFlags.Structured, "IndirectDrawCount" );

			// Frustum data for GPU culling
			const int frustumPlaneCount = 6;
			const int frustumDataSize = 4;
			CullData = new GpuBuffer<float>( frustumDataSize * frustumPlaneCount, GpuBuffer.UsageFlags.Structured, "CullingPlanes" );
		}

		// GPU Sort
		{
			distancesBuffer = new GpuBuffer<float>( MaxSprites, GpuBuffer.UsageFlags.Structured, "DistancesBuffer2" );
			IdBuffer = new GpuBuffer<uint>( MaxSprites, GpuBuffer.UsageFlags.Structured, "SortedBuffer" );
		}

		// Gpu Culling
		{
			atomicDispatchCounter = new GpuBuffer<uint>( 1, GpuBuffer.UsageFlags.Structured, "AtomicDispatchCounter" );
			atomicBindlessSprites = new GpuBuffer<uint>( MaxSprites, GpuBuffer.UsageFlags.Structured, "AtomicBindlessSprites" );
		}

		sortedCulledMap = new GpuBuffer<uint>(MaxSprites, GpuBuffer.UsageFlags.Structured, "SortedCulledMap" );
	}

	public void RegisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.Add( sprite );
	}

	public void UnregisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.RemoveAll( x => x == sprite );
	}

	private void ClearGPUSortBuffers()
	{
		SortCount = (1 << (int)Math.Ceiling( Math.Log2( Math.Max( 1, sprites.Count ) ) ));
		if ( SortCount > 2 )
		{
			bitonicShader.Attributes.Set( "D_CLEAR", 1 );
			bitonicShader.Attributes.Set( "SortBuffer", IdBuffer );
			bitonicShader.Attributes.Set( "DistanceBuffer", distancesBuffer );
			bitonicShader.Attributes.Set( "Count", SortCount );

			bitonicShader.Dispatch( SortCount, 1, 1 );

			Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( distancesBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
		}
	}

	private void GPUSort(List<float> distances)
	{
		// Clear
		distancesBuffer.SetData<float>( distances.ToArray() );

		// Sort
		if ( SortCount > 2 )
		{
			bitonicShader.Attributes.Set( "D_CLEAR", 0 );
			bitonicShader.Attributes.Set( "SortBuffer", IdBuffer );
			bitonicShader.Attributes.Set( "DistanceBuffer", distancesBuffer );
			bitonicShader.Attributes.Set( "Count", sprites.Count );

			int threadsX = Math.Min( SortCount, 262144 );
			int threadsY = (SortCount + 262144 - 1) / 262144;
			for ( int dim = 2; dim <= SortCount; dim *= 2 )
			{
				bitonicShader.Attributes.Set( "Dim", dim );
				for ( int block = dim / 2; block > 0; block /= 2 )
				{
					bitonicShader.Attributes.Set( "Block", block );
					bitonicShader.Dispatch( threadsX, threadsY, 1 );
					Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
				}
			}
		}

		var data = new uint[SortCount];
		IdBuffer.GetData<uint>( data );

		string sortedString = string.Empty;
		foreach ( var id in data )
		{
			sortedString += id.ToString() + "\n";
		}
		float xPos = 50;
		Graphics.DrawText( new Rect( xPos, 80, 100, 50 ), "SortedID", Color.Red );
		Graphics.DrawText( new Rect( xPos, 200, 100, 50 ), sortedString, Color.Red );

		var dists = new float[SortCount];
		distancesBuffer.GetData<float>( dists );

		string distString = string.Empty;
		foreach ( var dist in dists )
		{
			distString += dist.ToString() + "\n";
		}

		xPos = 200;
		Graphics.DrawText( new Rect( xPos, 80, 100, 50 ), "Distances", Color.Blue );
		Graphics.DrawText( new Rect( xPos, 200, 100, 50 ), distString, Color.Blue );
	}

	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		Vector3 camPos = Vector3.Zero;
		Frustum camFrustum;
		if (Scene.IsEditor)
		{
			// TODO: Find way of getting editors frustum...
			camFrustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			camPos = Scene.Camera.WorldPosition;
		}
		else
		{
			camFrustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			camPos = Scene.Camera.WorldPosition;
		}

		// Upload frustum planes to GPU
		Vector4[] planes =
		{
			new(-camFrustum.LeftPlane.Normal,   camFrustum.LeftPlane.Distance),
			new(-camFrustum.RightPlane.Normal,  camFrustum.RightPlane.Distance),
			new(-camFrustum.TopPlane.Normal,    camFrustum.TopPlane.Distance),
			new(-camFrustum.BottomPlane.Normal, camFrustum.BottomPlane.Distance),
			new(-camFrustum.FarPlane.Normal,    camFrustum.FarPlane.Distance),
			new(-camFrustum.NearPlane.Normal,   camFrustum.NearPlane.Distance),
		};
		CullData.SetData<Vector4>( planes.ToArray() );
		Graphics.ResourceBarrierTransition( CullData, ResourceState.CopyDestination );
		Graphics.ResourceBarrierTransition( CullData, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );

		if (RendererSortMode == SortMode.CPU)
		{
			sprites = sprites.OrderBy( s => Vector3.DistanceBetweenSquared( camPos, s.WorldPosition ) ).ToList();
			sprites.Reverse();
		}

		List<SpriteData> spritesToRender = new List<SpriteData>();
		foreach( BatchedSpriteComponent sprite in sprites)
		{
			//Vector3 spritePos = sprite.WorldPosition;
			//if ( RendererCullMethod == CullMethod.CPU &&
			//	!CullingUtils.IsSphereInsideFrustum( camFrustum, spritePos, 50.0f ) )
			//{
			//	//continue;
			//}

			spritesToRender.Add( new SpriteData
			{
				Transform = Matrix4x4.CreateTranslation( sprite.WorldPosition ),
				ColorTextureIndex = sprite.SpriteTexture.IsValid() ? sprite.SpriteTexture.Index : -1,
				NormalTextureIndex = sprite.NormalTexture.IsValid() ? sprite.NormalTexture.Index : -1,
				TintColor = new Vector4( sprite.Tinting, sprite.Alpha ),
				BillboardMode = (int)sprite.Billboard
			} );
		}

		spritesToRenderCount = spritesToRender.Count;

		// GPU sort
		//UpdateBuffers();
		Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.CopyDestination );
		gpuBuffer.SetData<SpriteData>( spritesToRender.ToArray() );
		Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );

		ClearGPUSortBuffers();

		if ( RendererSortMode == SortMode.GPU )
		{
			GPUSort( sprites.Select( c => Vector3.DistanceBetweenSquared( camPos, c.WorldPosition ) ).ToList() );
		}

		// GPU Culling
		{
			atomicDispatchCounter.SetData<uint>( new uint[1] { 0 } );
			Graphics.ResourceBarrierTransition( sortedCulledMap, ResourceState.UnorderedAccess);
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess);
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.UnorderedAccess);

			sphereCullShader.Attributes.Set( "AtomicCounter", atomicDispatchCounter );
			sphereCullShader.Attributes.Set( "SortedIDs", IdBuffer );
			sphereCullShader.Attributes.Set( "AtomicBindlessSprites", atomicBindlessSprites );
			sphereCullShader.Attributes.Set( "Sprites", gpuBuffer );
			sphereCullShader.Attributes.Set( "CullingPlanes", CullData );
			sphereCullShader.Attributes.Set( "SpriteCount", spritesToRenderCount );
			sphereCullShader.Attributes.Set( "SortedCulledMap", sortedCulledMap );

			int threadGroupSize = 128;
			int groupCount = (spritesToRenderCount + threadGroupSize - 1) / threadGroupSize;
			sphereCullShader.Dispatch( 1, groupCount, 1 );

			Graphics.ResourceBarrierTransition( atomicBindlessSprites,ResourceState.GenericRead );
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.GenericRead );
			Graphics.ResourceBarrierTransition( sortedCulledMap, ResourceState.GenericRead );
			
			// Debug dispatch count
			var data = new uint[1];
			atomicDispatchCounter.GetData<uint>( data );
			
			Graphics.DrawText( new Rect( 100, 0, 50, 50 ), "Dispatch Counter: " + data[0].ToString(), Color.Red );
		}

		// GPU Driven Indirect Draw
		{
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			computeShader.Attributes.Set( "IndirectDrawCount", atomicDispatchCounter ); 
			computeShader.Attributes.Set( "IndirectDrawCountBuffer", indirectDrawCount );

			computeShader.Dispatch( 1, 1, 1 );
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.IndirectArgument);
		}

		// Shade
		{
			Graphics.Attributes.Set( "SortedBuffer", IdBuffer );
			Graphics.Attributes.Set( "SpriteDatas", atomicBindlessSprites );
			Graphics.Attributes.Set( "CamPosition", Scene.Camera.WorldPosition );
			Graphics.Attributes.Set( "SortedCulledMap", sortedCulledMap );

			Vector3 pitchYawRoll = Scene.Camera.Transform.Rotation.Angles().AsVector3();
			pitchYawRoll.y = 90 - pitchYawRoll.y;
			pitchYawRoll.x = 90 + pitchYawRoll.x;
			pitchYawRoll.z = pitchYawRoll.z;
			Vector3 anglesRad = ((float)Math.PI / 180.0f) * pitchYawRoll;
			Matrix4x4 view = Matrix4x4.CreateFromYawPitchRoll( anglesRad.z, anglesRad.x, anglesRad.y );
			Graphics.Attributes.Set( "WorldToView", view );

			Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( sortedCulledMap, ResourceState.UnorderedAccess );
			Graphics.DrawModelInstancedIndirect( spriteModel, indirectDrawCount );
		}
	}
}
