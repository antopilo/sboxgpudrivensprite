using Sandbox;
using Sandbox.Internal;
using Sandbox.Rendering;
using System.Collections.Generic;

public sealed class SpriteRendererSystem : GameObjectSystem
{
	SpriteRenderObject spriteRenderObjects;

	public SpriteRendererSystem(Scene scene) : base(scene)
	{
		// Preload stuff
		var computeShader = new ComputeShader( "spriteCompute.shader" );
		var bitonicShader = new ComputeShader( "sort_cs.shader" );
		var sphereCullShader = new ComputeShader( "spherevisibilitytest.shader" );

		var shader = Shader.Load("spritematerial.shader");
		var spriteMat = Material.Load("spritematerial.vmat");

		// Create render object which will batch all sprites
		var renderObject = new SpriteRenderObject( Scene.SceneWorld, Scene );
		renderObject.spriteMat = spriteMat;
		renderObject.shader = shader;
		renderObject.computeShader = computeShader;
		renderObject.bitonicShader = bitonicShader;
		renderObject.sphereCullShader = sphereCullShader;
		renderObject.InitMesh();

		spriteRenderObjects = renderObject; 
	}

	public void RegisterSprite( BatchedSpriteComponent sprite )
	{
		spriteRenderObjects.RegisterInstance( sprite );
	}

	public void UnregisterSprite( BatchedSpriteComponent sprite )
	{
		// Remove the sprite from the list
		spriteRenderObjects.UnregisterInstance( sprite );
	}
}
