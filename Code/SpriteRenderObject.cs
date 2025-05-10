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

public sealed class SpriteRenderObject : SceneCustomObject
{
	// Render specific
	public ComputeShader computeShader;
	public ComputeShader bitonicShader;
	public Shader shader;
	public Material spriteMat;
	private Mesh spriteMesh;
	private Model spriteModel;

	// Batching specific
	List<BatchedSpriteComponent> sprites = new();
	GpuBuffer gpuBuffer;
	Scene Scene;

	GpuBuffer indirectDrawCount;

	// Bionicsort
	GpuBuffer distancesBuffer;
	GpuBuffer IdBuffer;

	// Cull data
	GpuBuffer CullData;

	public int InstanceCount => sprites.Count;

	public SpriteRenderObject( SceneWorld world, Scene scene ) : base( world )
	{
		Scene = scene;
	}

	public void InitMesh()
	{
		spriteMesh = new Mesh( spriteMat );
		spriteMesh.CreateVertexBuffer<Vertex>( 4, Vertex.Layout, new Vertex[] {
			new Vertex( new Vector3( -200, -200, 0 ), Vector3.Up, Vector3.Forward, new Vector4( 0, 0, 0, 0 ) ),
			new Vertex( new Vector3( 200, -200, 0 ), Vector3.Up, Vector3.Forward, new Vector4( 1, 0, 0, 0 ) ),
			new Vertex( new Vector3( 200, 200, 0 ), Vector3.Up, Vector3.Forward, new Vector4( 1, 1, 0, 0 ) ),
			new Vertex( new Vector3( -200, 200, 0 ), Vector3.Up, Vector3.Forward, new Vector4( 0, 1, 0, 0 ) ),
		} );
		spriteMesh.CreateIndexBuffer( 6, new[] { 0, 1, 2, 0, 2, 3 } );
		spriteMesh.SetIndexRange( 0, 6 );
		spriteMesh.Bounds = BBox.FromPositionAndSize( 0, 100 );
		spriteModel = new ModelBuilder().AddMesh( spriteMesh ).Create(); 

		Flags.CastShadows = true;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		gpuBuffer = new GpuBuffer<SpriteData>( 256 );

		indirectDrawCount = new GpuBuffer<uint>( 5, GpuBuffer.UsageFlags.Structured, "IndirectDrawCount" );

		// Biotonic sort
		distancesBuffer = new GpuBuffer<float>(256, GpuBuffer.UsageFlags.Structured, "DistancesBuffer2" );
		IdBuffer = new GpuBuffer<uint>( 256, GpuBuffer.UsageFlags.Structured, "SortedBuffer" );

		const int planeCount = 6;
		CullData = new GpuBuffer<float>( 4 * planeCount, GpuBuffer.UsageFlags.Structured, "CullingPlanes" );
	}

	public void RegisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.Add( sprite );
	}

	public void UnregisterInstance( BatchedSpriteComponent sprite )
	{
		sprites.RemoveAll( x => x == sprite );
	}

	public bool IsSphereInFront(Sandbox.Plane plane, Vector3 center, float radius )
	{
		// Plane.Normal * center + Plane.Distance > -radius
		// returns true if the sphere is at least partially in front of the plane
		float pointDistance = plane.GetDistance( center );
		if ( pointDistance < -radius )
		{
			return false;
		}
		else if(pointDistance < radius)
		{
			return true;
		}

		return true;
	}

	public bool IsSphereInside(Frustum frustum, in Vector3 center, float radius )
	{
		if ( !IsSphereInFront( frustum.LeftPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.RightPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.TopPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront(frustum.BottomPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.NearPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.FarPlane, center, radius ) )
			return false;

		return true;
	}

	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		List<SpriteData> gpuData = new List<SpriteData>(sprites.Count());

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
				new Vector4( -frustum.LeftPlane.Normal, frustum.LeftPlane.Distance),
				new Vector4( -frustum.RightPlane.Normal, frustum.RightPlane.Distance ),
				new Vector4( -frustum.TopPlane.Normal, frustum.TopPlane.Distance ),
				new Vector4( -frustum.BottomPlane.Normal, frustum.BottomPlane.Distance ),
				new Vector4( -frustum.FarPlane.Normal, frustum.FarPlane.Distance ),
				new Vector4( -frustum.NearPlane.Normal, frustum.NearPlane.Distance ),
			];
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );
			CullData.SetData<Vector4>( planes.ToArray() );

			string planesString = string.Empty;
			foreach(var plane in planes)
			{
				planesString += plane.ToString() + "\n";
			}

			Graphics.DrawText( new Rect( 120, 16 ), planesString, Color.Black );
		}

		var sortedSprites = sprites
			//.Where( s => IsSphereInside( Scene.Camera.GetFrustum( Scene.Camera.ScreenRect ), s.WorldPosition, 5.0f ) )
			.OrderBy( s => Vector3.DistanceBetweenSquared( camPos, s.WorldPosition ) ).Reverse()
			.ToList();

		foreach(var sprite in sprites)
		{
			GameTransform transform = sprite.Transform;
			transform.Scale = 6.0f;

			Frustum frustum = Scene.Camera.GetFrustum( Scene.Camera.ScreenRect );
			frustum.IsInside( sprite.WorldPosition );

			if ( IsSphereInside( frustum, sprite.WorldPosition, 200.0f ))
			{
				Graphics.DrawModel( Model.Sphere, transform.World );
			}
		}

		foreach ( var data in sortedSprites )
		{
			// Billboard rotation matrix: face the camera from sprite's world position
			gpuData.Add( new SpriteData()
			{
				Transform = Matrix4x4.CreateTranslation(data.WorldPosition),
				ColorTextureIndex = data.SpriteTexture.IsValid() ? data.SpriteTexture.Index : -1,
				NormalTextureIndex = data.NormalTexture.IsValid() ? data.NormalTexture.Index : -1,
				TintColor = new Vector4(data.Tinting, data.Alpha),
				BillboardMode = (int)data.Billboard,
			} );
		}


		// GPU Driven Indirect Draw
		{
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess );
			computeShader.Attributes.Set( "IndirectDrawCount", gpuData.Count); 
			computeShader.Attributes.Set( "IndirectDrawCountBuffer", indirectDrawCount );
			computeShader.Dispatch( 1, 1, 1 );
			Graphics.ResourceBarrierTransition( indirectDrawCount, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
		}

		// Draw
		{
			Graphics.ResourceBarrierTransition( gpuBuffer, ResourceState.UnorderedAccess );
			gpuBuffer.SetData<SpriteData>( gpuData.ToArray() );

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
