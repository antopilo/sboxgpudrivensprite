#define DEBUGCULL

using Sandbox;
using Sandbox.Internal;
using Sandbox.Rendering;
using Sandbox.UI;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

struct SpriteDataVis
{ 
	bool IsVisible;
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

	private CullMethod RendererCullMethod = CullMethod.CPU;
	private SortMode RendererSortMode = SortMode.CPU;

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
			const float spriteSize = 200f;
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
	}

	private void UpdateBuffers()
	{
		var newSortCount = (1 << (int)Math.Ceiling( Math.Log2( Math.Max( 1, sprites.Count ) ) ));
		if( newSortCount == SortCount )
			return;

		SortCount = newSortCount;

		// Resize buffers
		distancesBuffer?.Dispose();
		distancesBuffer = new GpuBuffer<float>( SortCount, GpuBuffer.UsageFlags.Structured, "DistancesBuffer" );

		IdBuffer?.Dispose();
		IdBuffer = new GpuBuffer<uint>( SortCount, GpuBuffer.UsageFlags.Structured, "SortedBuffer" );
	}

	public void RegisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.Add( sprite );
		//UpdateBuffers();
	}

	public void UnregisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.RemoveAll( x => x == sprite );
	}

	private void GPUSort(List<float> distances)
	{
		// Clear
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

		Graphics.DrawText( new Rect( 0, 80, 100, 50 ), SortCount.ToString(), Color.Yellow );
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
				bitonicShader.Attributes.Set( "Dim", 1 );
				for ( int block = dim / 2; block > 0; block /= 2 )
				{
					bitonicShader.Attributes.Set( "Block", 2 );
					bitonicShader.Dispatch( threadsX, threadsY, 1 );
					Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess );
				}
			}
		}

		Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.UnorderedAccess, ResourceState.GenericRead );

#if GPUCULLDEBUG
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
#endif
	}

	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		List<SpriteData> bindlessSprites = new (sprites.Count);

		Vector3 camPos = Vector3.Zero;
		Vector3 camForward;
		Vector3 camUp;
		if (Scene.IsEditor)
		{
			var camera = Scene.Camera;
			camPos = camera.WorldPosition;
			camForward = camera.WorldRotation.Forward;
			camUp = camera.WorldRotation.Up;
		}
		else
		{
			var camera = Scene.Camera;
			camPos = camera.WorldPosition;
			camForward = camera.WorldRotation.Forward;
			camUp = camera.WorldRotation.Up;
		}

		// Push culling data, 6 frustum planes
		{
			var frustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			List<Vector4> planes =
			[
				new ( -frustum.LeftPlane.Normal, frustum.LeftPlane.Distance),
				new ( -frustum.RightPlane.Normal, frustum.RightPlane.Distance ),
				new ( -frustum.TopPlane.Normal, frustum.TopPlane.Distance ),
				new ( -frustum.BottomPlane.Normal, frustum.BottomPlane.Distance ),
				new ( -frustum.FarPlane.Normal, frustum.FarPlane.Distance ),
				new ( -frustum.NearPlane.Normal, frustum.NearPlane.Distance ),
			];
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );
			CullData.SetData<Vector4>( planes.ToArray() );
		}

		switch ( RendererSortMode )
		{
			case SortMode.GPU:
				List<float> gpuDistances = sprites.Select( c => Vector3.DistanceBetweenSquared( camPos, c.WorldPosition ) ).ToList();
				//GPUSort( gpuDistances );
				break;
			case SortMode.CPU:
				sprites = sprites.OrderBy( s => Vector3.DistanceBetweenSquared( camPos, s.WorldPosition ) ).Reverse().ToList();
				break;
			case SortMode.None:
				break; // Nothing
		}

		foreach ( var data in sprites )
		{
			// Billboard rotation matrix: face the camera from sprite's world position
			bindlessSprites.Add( new SpriteData()
			{
				Transform = Matrix4x4.CreateTranslation(data.WorldPosition),
				ColorTextureIndex = data.SpriteTexture.IsValid() ? data.SpriteTexture.Index : -1,
				NormalTextureIndex = data.NormalTexture.IsValid() ? data.NormalTexture.Index : -1,
				TintColor = new Vector4(data.Tinting, data.Alpha),
				BillboardMode = (int)data.Billboard,
			} );
		}



		// GPU sort
		//UpdateBuffers();

#if DEBUGCULL
		foreach(var sprite in sprites)
		{
			GameTransform transform = sprite.Transform;
			transform.Scale = 6.0f;

			Frustum frustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			frustum.IsInside( sprite.WorldPosition );

			if ( CullingUtils.IsSphereInsideFrustum( frustum, sprite.WorldPosition, 200.0f ))
			{
				Graphics.DrawModel( Model.Sphere, transform.World );
			}
		}
#endif

		// GPU Driven Indirect Draw
		{
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess );
			computeShader.Attributes.Set( "IndirectDrawCount", bindlessSprites.Count); 
			computeShader.Attributes.Set( "IndirectDrawCountBuffer", indirectDrawCount );
			computeShader.Dispatch( 1, 1, 1 );
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
		}

		// Shade
		{
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );
			gpuBuffer.SetData<SpriteData>( bindlessSprites.ToArray() );

			Graphics.ResourceBarrierTransition(gpuBuffer, ResourceState.GenericRead );
			Graphics.ResourceBarrierTransition( CullData, ResourceState.GenericRead );
			Graphics.ResourceBarrierTransition( IdBuffer, ResourceState.GenericRead );
			Graphics.Attributes.Set( "CullingData", CullData );
			Graphics.Attributes.Set( "SortedBuffer", IdBuffer );
			Graphics.Attributes.Set( "SpriteDatas", gpuBuffer );
			Graphics.DrawModelInstancedIndirect( spriteModel, indirectDrawCount );
		}
	}
}
