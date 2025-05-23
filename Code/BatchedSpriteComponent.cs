using Sandbox;
using Sandbox.Internal;
using Sandbox.Rendering;

[Title( "GPU Sprite Renderer" )]
[Category( "Rendering" )]
[Icon( "favorite" )]
[Description( "GPU Driven Sprite Rendering" )]
public sealed class BatchedSpriteComponent : Renderer, Component.ExecuteInEditor
{
	public enum BillboardMode
	{ 
		Always,
		YOnly,
		None
	}

	[Description( "Sprite Texture" )]
	public Texture SpriteTexture { get; set; }

	[Property]
	[Description( "Normal Texture" )]
	public Texture NormalTexture { get; set; }

	[Property]
	[Description( "Tint the sprite" )]
	public Color Tinting { get; set; } = Color.White;

	[Property]
	[DefaultValue( 1.0f )]
	[Description( "Alpha value of the sprite" )]
	public float Alpha { get; set; } = 1.0f;

	[Property]
	[Description( "Changes billboard behaviour" )]
	public BillboardMode Billboard { get; set; }

	protected override void OnPreRender()
	{
	}

	protected override void OnEnabled()
	{
		Scene.GetSystem<SpriteRendererSystem>().RegisterSprite( this );
	}

	protected override void OnDisabled()
	{
		Scene.GetSystem<SpriteRendererSystem>().UnregisterSprite( this );
	}

	protected override void OnRefresh()
	{
	}

	protected override void OnUpdate()
	{
	}
}
