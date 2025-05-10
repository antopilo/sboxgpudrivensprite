using Sandbox;

public static class CullingUtils
{
	private static bool IsSphereInFront( Sandbox.Plane plane, Vector3 center, float radius )
	{
		// Plane.Normal * center + Plane.Distance > -radius
		// returns true if the sphere is at least partially in front of the plane
		float pointDistance = plane.GetDistance( center );
		if ( pointDistance < -radius )
		{
			return false;
		}
		else if ( pointDistance < radius )
		{
			return true;
		}

		return true;
	}

	public static bool IsSphereInsideFrustum( Frustum frustum, in Vector3 center, float radius )
	{
		if ( !IsSphereInFront( frustum.LeftPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.RightPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.TopPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.BottomPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.NearPlane, center, radius ) )
			return false;

		if ( !IsSphereInFront( frustum.FarPlane, center, radius ) )
			return false;

		return true;
	}
}


