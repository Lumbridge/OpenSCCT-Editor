# Shadow cache checks

Run `dotnet run --project tools/shadow-cache-checks -c Release`.

A narrow blocker overlaps a broad blocker farther from the receiving surface. Reordering those same triangles used to change the first blocker encountered and the penumbra width, despite an identical cache key. The regression compares float texels and final RGBA for cold, reordered, cached, serialized and unrelated-geometry builds. It covers point, cylinder and sun lights in quality and faithful modes, and checks segment bounds, sky and alpha-mask behavior separately.

Soft-shadow centre rays use the farthest blocker to conservatively bound the sampling band. Hard-shadow and visibility-only queries retain the early-out any-hit path. Old shadow caches are invalidated. No map files are edited; the test removes its own unique temporary cache folder.

Coincident nearest hits prefer a front face, then stable geometry/source keys. This prevents BVH order from changing burial and bounce-source selection; a genuinely nearer back face still wins. Both direct intersections and complete cold/cached bakes cover the tie case.
