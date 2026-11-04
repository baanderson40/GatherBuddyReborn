using System;
using System.Numerics;

namespace GatherBuddy.Crafting;

public static class RetainerBellGeometry
{
    // Distance from bell center: live hitbox radius plus 2.5 metres for every bell type.
    public static float Range(float hitboxRadius) => MathF.Max(0, hitboxRadius) + 2.5f;
    public static bool InRange(Vector3 player, Vector3 bell, float hitboxRadius)
        => Math.Abs(player.Y - bell.Y) <= 2.5f
        && Vector2.Distance(new(player.X, player.Z), new(bell.X, bell.Z)) <= Range(hitboxRadius)
        && Vector3.Distance(player, bell) <= Range(hitboxRadius);

    public static Vector3 Approach(Vector3 player, Vector3 bell, float hitboxRadius, int candidate)
    {
        var direction = new Vector2(player.X - bell.X, player.Z - bell.Z);
        direction = direction.LengthSquared() < 0.01f ? Vector2.UnitX : Vector2.Normalize(direction);
        var angle = candidate * MathF.PI / 4;
        var rotated = new Vector2(direction.X * MathF.Cos(angle) - direction.Y * MathF.Sin(angle),
            direction.X * MathF.Sin(angle) + direction.Y * MathF.Cos(angle));
        return bell + new Vector3(rotated.X, 0, rotated.Y) * (Range(hitboxRadius) - 1);
    }
}
