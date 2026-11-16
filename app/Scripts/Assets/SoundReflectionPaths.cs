extern alias GameRuntime;
using OpenScct.Core;
using GameRuntime::OpenScctGame.Audio;

namespace OpenScctEditor.Assets;

public readonly record struct SoundReflectionSegment(FVector Start, FVector End, float Power, float HighFrequencyPower, int Bounce, float Distance);

/// <summary>Bounded geometric estimates using the game room model's material absorption and distance gain.</summary>
public static class SoundReflectionPaths
{
    public const int MaximumBounces = 4;
    public static readonly FVector[] Directions = MakeDirections();

    public static IEnumerable<SoundReflectionSegment> Trace(FVector source, FVector direction, float range, float radius,
        float volume, Func<FVector, FVector, float, AcousticRayHit> trace)
    {
        const float epsilon = .05f;
        FVector start = source, rayOrigin = source;
        direction = direction.SafeNormal();
        float distance = 0, mid = volume * volume, high = mid;
        for (int bounce = 0; bounce <= MaximumBounces && distance < range && mid > .0001f; bounce++)
        {
            float bias = FVector.Dist(rayOrigin, start);
            var hit = trace(rayOrigin, direction, Math.Max(0, range - distance - bias));
            FVector end = hit.Hit ? rayOrigin + direction * hit.Distance : start + direction * (range - distance);
            float step = FVector.Dist(start, end);
            if (!float.IsFinite(step) || step < epsilon) yield break;
            step = Math.Min(step, range - distance);
            if (FVector.Dist(start, end) > step) end = start + (end - start).SafeNormal() * step;
            distance += step;
            float gain = SoundRules.DistanceGain(distance * AudioK.DistanceFactor, SoundRules.MinDistance(radius));
            if (bounce > 0) yield return new(start, end, mid * gain * gain, high * gain * gain, bounce, distance);
            if (!hit.Hit || hit.Normal.SizeSquared() < .5f) yield break;
            var absorption = ReverbGeometryProbe.Absorption(hit.Surface, hit.Occlusion);
            mid *= 1 - absorption.Mid;
            high *= 1 - absorption.HF;
            FVector normal = hit.Normal.SafeNormal();
            if ((direction | normal) > 0) normal = -normal;
            direction = (direction - normal * (2 * (direction | normal))).SafeNormal();
            start = end;
            rayOrigin = end + normal * epsilon;
        }
    }

    private static FVector[] MakeDirections()
    {
        var rays = new List<FVector> { new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0), new(0,0,1), new(0,0,-1) };
        // Equal-area spherical samples include floors, ceilings and oblique walls.
        for (int i = 0; i < 18; i++)
        {
            float z = 1 - 2 * (i + .5f) / 18;
            float r = MathF.Sqrt(1 - z * z), angle = i * 2.39996323f;
            rays.Add(new(r * MathF.Cos(angle), r * MathF.Sin(angle), z));
        }
        return rays.ToArray();
    }
}
