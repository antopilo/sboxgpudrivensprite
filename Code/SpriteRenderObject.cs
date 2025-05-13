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
	private GpuBuffer culledSortedMapping;

	public int InstanceCount => sprites.Count;

	public SpriteRenderObject( SceneWorld world, Scene scene ) : base( world )
	{
		Scene = scene;
	}

	// Forewords
	// I initially started using the Command API because that's what made the most
	// sense to me. Then I realized it wasn't quite where it should be for me to pull 
	// this off(E.g you can only hook after the depth prepass making shadows awkward)
	//
	// I then looked into the assembly of how other features were made, using custom scene object
	// Seemed way more flexible atm rendering wise - so I went with that...
	// This was my first time doing graphics work in s&box and I was suprised by the iteration time.
	// I lost my device a couple times, but i was able to find it back. :)
	// Started on Friday night, and finished on Sunday night. I had a blast doing this.

	// Things I would improve
	// - Only upload the deltas of the bindless sprites when added/removed
	// - Separate transforms into another bindless buffers for easier uploading bandwidth wise
	// - Do separate passes for opaque and transparent sprites(Front to back, back to front sorting)
	// - Generate mesh in vertex shader, no need for an index buffer these days
	// - Hook into the OnRefresh of components and only upload the deltas
	// - Make GPU frustum culling & GPU sorting play nicely together
	// - Move sorting *after* culling stage
	// - Consider shadow views for Frustum culling by extracting frustum planes directly in the shader
	// - Consolidate passes to avoid useless pipelines stall
	// - ALSO using the command API
	// - Dynamic resizing of the buffers, similar to the OG sprite renderer, but this is a codetest afterall.


	public void InitMesh()
	{
		Flags.CastShadows = true;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;

		// Create sprite mesh
		// TODO; Should move directly in vertex shader or compute
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

		culledSortedMapping = new GpuBuffer<uint>(MaxSprites, GpuBuffer.UsageFlags.Structured, "CulledSortedMapping" );
	}

	public void RegisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.Add( sprite );
	}

	public void UnregisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.RemoveAll( x => x == sprite );
	}

	private void GPUSort(List<float> distances, bool opaque)
	{
		// Clear
		SortCount = (1 << (int)Math.Ceiling( Math.Log2( Math.Max( 1, distances.Count ) ) ));
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

		distancesBuffer.SetData<float>( distances.ToArray() );

		// Sort
		if ( !opaque && SortCount > 2 )
		{
			bitonicShader.Attributes.Set( "D_CLEAR", 0 );
			bitonicShader.Attributes.Set( "SortBuffer", IdBuffer );
			bitonicShader.Attributes.Set( "DistanceBuffer", distancesBuffer );
			bitonicShader.Attributes.Set( "Count", SortCount);

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

		Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.GenericRead );

		// Debugging GPU sorting, works well by itself, just need to make it work nicely with culling
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

	private void CulledSolid()
	{
		Vector3 camPos = Vector3.Zero;
		Frustum camFrustum;
		if ( Scene.IsEditor )
		{
			// TODO: Find way of getting editors frustum. Leaving this as is since Gizmo.Camera doesnt work properly
			camFrustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			camPos = Scene.Camera.WorldPosition;
		}
		else
		{
			camFrustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			camPos = Scene.Camera.WorldPosition;
		}

		// Upload frustum planes to GPU for sphere bound culling
		Vector4[] planes =
		{
			new(-camFrustum.LeftPlane.Normal,   camFrustum.LeftPlane.Distance),
			new(-camFrustum.RightPlane.Normal,  camFrustum.RightPlane.Distance),
			new(-camFrustum.TopPlane.Normal,    camFrustum.TopPlane.Distance),
			new(-camFrustum.BottomPlane.Normal, camFrustum.BottomPlane.Distance),
			new(-camFrustum.FarPlane.Normal,    camFrustum.FarPlane.Distance),
			new(-camFrustum.NearPlane.Normal,   camFrustum.NearPlane.Distance),
		};
		Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );
		CullData.SetData<Vector4>( planes.ToArray() );

		// In case we want to do sorting on the CPU since GPU is WIP
		if ( RendererSortMode == SortMode.CPU )
		{
			sprites = sprites.OrderBy( s => Vector3.DistanceBetweenSquared( camPos, s.WorldPosition ) ).ToList();
			sprites.Reverse();
		}

		// This is not ideal, we would want to only push the deltas
		var spriteCount = sprites.Count;
		var bindlessSpriteArray = new SpriteData[spriteCount];
		var sortedSpriteArray = new SpriteData[spriteCount];
		for ( int i = 0; i < spriteCount; i++ )
		{
			var data = sprites[i];
			{
				Matrix4x4 spriteTransform = Matrix4x4.Identity;
				spriteTransform *= Matrix4x4.CreateScale( data.WorldScale );

				spriteTransform *= Matrix4x4.CreateFromYawPitchRoll( 
					MathX.DegreeToRadian(data.WorldRotation.Pitch()),
					MathX.DegreeToRadian( data.WorldRotation.Yaw() ),
					MathX.DegreeToRadian( data.WorldRotation.Roll() )
				);

				spriteTransform *= Matrix4x4.CreateTranslation( data.WorldPosition );
				bindlessSpriteArray[i] = new SpriteData
				{
					Transform = spriteTransform,
					ColorTextureIndex = data.SpriteTexture.IsValid() ? data.SpriteTexture.Index : -1,
					NormalTextureIndex = data.NormalTexture.IsValid() ? data.NormalTexture.Index : -1,
					TintColor = new Vector4( data.Tinting, data.Alpha ),
					BillboardMode = (int)data.Billboard
				};
			}
		}
		gpuBuffer.SetData<SpriteData>( bindlessSpriteArray );
		Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );

		// TODO: Should sort after culling & only transparent materials.
		// GPUSort( sprites.Select( c => Vector3.DistanceBetweenSquared( camPos, c.WorldPosition ) ).ToList(), true );

		// GPU Frustum Culling(Sphere test)
		{
			atomicDispatchCounter.SetData<uint>( new uint[1] { 0 } );
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.UnorderedAccess );
			sphereCullShader.Attributes.Set( "CulledSortedMapping", culledSortedMapping );
			sphereCullShader.Attributes.Set( "AtomicCounter", atomicDispatchCounter );
			sphereCullShader.Attributes.Set( "AtomicBindlessSprites", atomicBindlessSprites );
			sphereCullShader.Attributes.Set( "Sprites", gpuBuffer );
			sphereCullShader.Attributes.Set( "CullingPlanes", CullData );
			sphereCullShader.Attributes.Set( "sortedMapping", IdBuffer );
			sphereCullShader.Attributes.Set( "SpriteCount", sprites.Count );
			sphereCullShader.Attributes.Set( "Opaque", 1);

			int threadGroupSize = 128;
			int groupCount = (sprites.Count + threadGroupSize - 1) / threadGroupSize;
			sphereCullShader.Dispatch( 1, groupCount, 1 );


			// Debug dispatch counter
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

			// Not ideal, should pipe straight from culling stage
			computeShader.Dispatch( 1, 1, 1 );
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
		}

		// Shade
		{
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.GenericRead );
			Graphics.Attributes.Set( "SortedBuffer", IdBuffer );
			Graphics.Attributes.Set( "SpriteDatas", atomicBindlessSprites );
			Graphics.Attributes.Set( "CamPosition", Scene.Camera.WorldPosition );
			Graphics.Attributes.Set( "SortedMapping", culledSortedMapping );
			Graphics.Attributes.Set( "Opaque", 1 );

			// Build look at matrix to fix self-shadowing
			Vector3 pitchYawRoll = Scene.Camera.Transform.Rotation.Angles().AsVector3();
			pitchYawRoll.y = 90 - pitchYawRoll.y;
			pitchYawRoll.x = 90 + pitchYawRoll.x;
			pitchYawRoll.z = pitchYawRoll.z;
			Vector3 anglesRad = ((float)Math.PI / 180.0f) * pitchYawRoll;
			Matrix4x4 view = Matrix4x4.CreateFromYawPitchRoll( anglesRad.z, anglesRad.x, anglesRad.y );
			Graphics.Attributes.Set( "WorldToView", view );

			Graphics.DrawModelInstancedIndirect( spriteModel, indirectDrawCount );
		}
	}

	// TODO: make sorting work nicely with culling
	private void Sorted()
	{
		Vector3 camPos = Vector3.Zero;
		Frustum camFrustum;
		if ( Scene.IsEditor )
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

		if ( RendererSortMode == SortMode.CPU )
		{
			sprites = sprites.OrderBy( s => Vector3.DistanceBetweenSquared( camPos, s.WorldPosition ) ).ToList();
			sprites.Reverse();
		}

		// This is not ideal, we would want to only push the deltas
		var spriteCount = sprites.Count;
		var bindlessSpriteArray = new SpriteData[spriteCount];
		var sortedSpriteArray = new SpriteData[spriteCount];
		for ( int i = 0; i < spriteCount; i++ )
		{
			var data = sprites[i];
			if ( data.Alpha < 1.0f )
			{
				bindlessSpriteArray[i] = new SpriteData
				{
					Transform = Matrix4x4.CreateTranslation( data.WorldPosition ),
					ColorTextureIndex = data.SpriteTexture.IsValid() ? data.SpriteTexture.Index : -1,
					NormalTextureIndex = data.NormalTexture.IsValid() ? data.NormalTexture.Index : -1,
					TintColor = new Vector4( data.Tinting, data.Alpha ),
					BillboardMode = (int)data.Billboard
				};
			}
		}

		// GPU sort
		//UpdateBuffers();

		gpuBuffer.SetData<SpriteData>( bindlessSpriteArray );
		Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );

		GPUSort( sprites.Select( c => Vector3.DistanceBetweenSquared( camPos, c.WorldPosition ) ).ToList(), false );

		// GPU Culling
		{
			atomicDispatchCounter.SetData<uint>( new uint[1] { 0 } );
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( atomicDispatchCounter, ResourceState.UnorderedAccess );
			sphereCullShader.Attributes.Set( "CulledSortedMapping", culledSortedMapping );
			sphereCullShader.Attributes.Set( "AtomicCounter", atomicDispatchCounter );
			sphereCullShader.Attributes.Set( "AtomicBindlessSprites", atomicBindlessSprites );
			sphereCullShader.Attributes.Set( "Sprites", gpuBuffer );
			sphereCullShader.Attributes.Set( "CullingPlanes", CullData );
			sphereCullShader.Attributes.Set( "sortedMapping", IdBuffer );
			sphereCullShader.Attributes.Set( "SpriteCount", sprites.Count );
			sphereCullShader.Attributes.Set( "Opaque", 0 );

			int threadGroupSize = 128;
			int groupCount = (sprites.Count + threadGroupSize - 1) / threadGroupSize;
			sphereCullShader.Dispatch( 1, groupCount, 1 );

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
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
		}

		// Shade
		{
			Graphics.ResourceBarrierTransition( atomicBindlessSprites, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.GenericRead );
			Graphics.Attributes.Set( "SortedBuffer", IdBuffer );
			Graphics.Attributes.Set( "SpriteDatas", gpuBuffer );
			Graphics.Attributes.Set( "CamPosition", Scene.Camera.WorldPosition );
			Graphics.Attributes.Set( "SortedMapping", culledSortedMapping );
			Graphics.Attributes.Set( "Opaque", 0 );

			Vector3 pitchYawRoll = Scene.Camera.Transform.Rotation.Angles().AsVector3();
			pitchYawRoll.y = 90 - pitchYawRoll.y;
			pitchYawRoll.x = 90 + pitchYawRoll.x;
			pitchYawRoll.z = pitchYawRoll.z;
			Vector3 anglesRad = ((float)Math.PI / 180.0f) * pitchYawRoll;
			Matrix4x4 view = Matrix4x4.CreateFromYawPitchRoll( anglesRad.z, anglesRad.x, anglesRad.y );
			Graphics.Attributes.Set( "WorldToView", view );
			Graphics.DrawModelInstancedIndirect( spriteModel, indirectDrawCount );
		}
	}


	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		CulledSolid();

		// TODO: make sorting + culling play nicely
		// Sorted();
	}
}
