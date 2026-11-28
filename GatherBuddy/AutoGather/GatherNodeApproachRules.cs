using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.AutoGather;

internal static class GatherNodeApproachRules
{
    public const float HorizontalInteractionLimit = 3.5f;
    public const float VerticalInteractionLimit = 3f;

    public static bool IsWithinInteractionRange(Vector3 node, Vector3 player)
        => Vector2.DistanceSquared(new Vector2(node.X, node.Z), new Vector2(player.X, player.Z))
            < HorizontalInteractionLimit * HorizontalInteractionLimit
            && System.Math.Abs(node.Y - player.Y) < VerticalInteractionLimit;

    public static bool IsPathEndpointReachable(Vector3 node, Vector3 endpoint)
        => IsWithinInteractionRange(node, endpoint);

    public static bool TryGetCombinedPathGroundEndpoint(IReadOnlyList<Vector3> path, out Vector3 endpoint)
    {
        if (path.Count < 2)
        {
            endpoint = default;
            return false;
        }

        endpoint = path[^2]; // FindCombinedPath appends landingWP as metadata.
        return true;
    }
}
