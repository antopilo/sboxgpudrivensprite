using Sandbox;
using Sandbox.Internal;
using Sandbox.Rendering;
using System.Collections.Generic;

public sealed class SpriteRendererSystem : GameObjectSystem
{
	private GpuBuffer instancedGPUBuffer;
	private Shader shader;
	private Material spriteMat;
	private ComputeShader computeShader;
	private ComputeShader bitonicShader;
	List<SpriteRenderObject> spriteRenderObjects = new();
	List<Transform> positions;

	public SpriteRendererSystem(Scene scene) : base(scene)
	{
		// Preload stuff
		computeShader = new ComputeShader( "spriteCompute.shader" );
		bitonicShader = new ComputeShader( "sort_cs.shader" );

		shader = Shader.Load("spritematerial.shader");
		spriteMat = Material.Load("spritematerial.vmat");

		//spriteModel = Model.Load( "new model.vmdl" );
		// Allocate stuff
		// 10,000 instances for now
		instancedGPUBuffer = new GpuBuffer<Vector3>(10000, GpuBuffer.UsageFlags.Structured, "SpritePositions");

		

		// Hook up :)
		Listen(Stage.UpdateBones, 0, () => OnUpdate(), "SpriteRendererOnUpdate");

		var renderObject = new SpriteRenderObject( Scene.SceneWorld, Scene );
		renderObject.spriteMat = spriteMat;
		renderObject.shader = shader;
		renderObject.computeShader = computeShader;
		renderObject.bitonicShader = bitonicShader;
		renderObject.InitMesh();
		spriteRenderObjects.Add( renderObject ); 
	}

	public void RegisterSprite( BatchedSpriteComponent sprite )
	{
		foreach(SpriteRenderObject sprRenderObject in spriteRenderObjects )
		{
			spriteRenderObjects[0].RegisterInstance( sprite );
		}
	}

	public void UnregisterSprite( BatchedSpriteComponent sprite )
	{
		// Remove the sprite from the list
		spriteRenderObjects[0].UnregisterInstance( sprite );
	}

	public void OnUpdate()
	{
		// Upload sprite positions to GPU
		{
			var allSprites = Scene.GetAllComponents<BatchedSpriteComponent>();
			//spriteRenderObjects.Clear();

			positions = new(allSprites.Count());
			foreach (var sprite in allSprites)
			{
				positions.Add(sprite.WorldTransform);

				
				//spriteRenderObjects.Add( renderObject );
			}

			// Debug
			Gizmo.Draw.ScreenText("mySpriteCount: " + spriteRenderObjects[0].InstanceCount, new Vector2(50, 50));
		}

		// Build cmd list
		{
			CommandList commandList = new();

			StringToken spriteRendererPositionsToken = new("SpriteRendererPositions");
			commandList.SetGlobal(spriteRendererPositionsToken, instancedGPUBuffer);

			Transform spriteTransform = new Transform(1.0f);
			spriteTransform.Position = new Vector3(0, 0, 0);

			foreach (Transform p in positions)
			{
				var transform = p;
				RenderAttributes renderAttributes = new RenderAttributes();
				//commandList.DrawModel(spriteModel, transform, new RenderAttributes());
			}

			Scene.Camera.ClearCommandLists();
			Scene.Camera.AddCommandList(commandList, Sandbox.Rendering.Stage.AfterDepthPrepass);
		}
	}
}
