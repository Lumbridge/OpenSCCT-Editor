using System.Numerics;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Fills a model's render bounds and collision hulls (UModel Bounds and LeafHulls, the nodes' iRenderBound and
/// iCollisionBound) the way the stock editor's bspBuildBounds does, so box traces against the world and the volumes
/// collide as in a stock map. The six faces of the half-world box are filtered down the tree; each node adds its own
/// plane, clipped to the region the path to it bounds, as a cap. A node's render bound is the box of the empty space
/// below it; at a solid leaf the hull lists the planes that bound it (flipped when the leaf is in front of one) and
/// their box. Checked against stock maps: every node's indices and every bound agree, hull boxes to float noise.
/// </summary>
internal static class SdcBspBounds
{
    private const float HalfWorld = 262144f;
    private const float OnPlane = 0.25f;
    private const int Flipped = 0x40000000;
    private const byte NotCsg = 0x01;

    private sealed record Face(List<Vector3> Verts, int Plane);

    public static void Build(UModelData m)
    {
        m.Bounds.Clear();
        m.LeafHulls.Clear();
        foreach (UBspNode n in m.Nodes)
        {
            (n.RenderBound, n.CollisionBound) = (-1, -1);
        }

        if (m.Nodes.Count > 0)
        {
            new Filter(m).Node(0, WorldBox(), m.RootOutside, out _);
        }
    }

    private sealed class Filter(UModelData m)
    {
        private readonly List<(Vector4 Plane, bool Front)> _path = new();

        /// <summary>Filters <paramref name="faces"/> through node <paramref name="i"/>; <paramref name="bound"/> is the
        /// box of the empty space they reach below it.</summary>
        public void Node(int i, List<Face> faces, bool outside, out Box bound)
        {
            UBspNode node = m.Nodes[i];
            bool csg = (node.NodeFlags & NotCsg) == 0;
            var front = new List<Face>();
            var back = new List<Face>();
            if (Cap(i, node.Plane) is { } cap)
            {
                faces = [.. faces, cap];
            }

            foreach (Face f in faces)
            {
                switch (Split(f, node.Plane, out Face? f1, out Face? b1))
                {
                    case Side.On:
                        front.Add(f.Plane == i ? f with { Plane = i | Flipped } : f);
                        back.Add(f);
                        break;
                    case Side.Front:
                        front.Add(f);
                        break;
                    case Side.Back:
                        back.Add(f);
                        break;
                    default:
                        front.Add(f1!);
                        back.Add(b1!);
                        break;
                }
            }

            bound = new Box();
            // A front leaf is empty below a CSG node or outside; a back leaf only outside below a non-CSG node.
            Child(i, node.Front, node.Plane, true, front, outside || csg, bound);
            Child(i, node.Back, node.Plane, false, back, outside && !csg, bound);
            if (node.Front >= 0 || node.Back >= 0)
            {
                node.RenderBound = m.Bounds.Count;
                m.Bounds.Add((bound.Min, bound.Max, bound.Valid));
            }
        }

        private void Child(int i, int child, Vector4 plane, bool front, List<Face> faces, bool empty, Box bound)
        {
            if (faces.Count == 0)
            {
                return;
            }

            if (child >= 0)
            {
                _path.Add((plane, front));
                Node(child, faces, empty, out Box below);
                _path.RemoveAt(_path.Count - 1);
                bound.Add(below);
            }
            else if (empty)
            {
                faces.ForEach(f => f.Verts.ForEach(bound.Add));
            }
            else
            {
                Hull(i, faces);
            }
        }

        /// <summary>The planes bounding a solid leaf, once each, then INDEX_NONE and the leaf's box as six floats.</summary>
        private void Hull(int i, List<Face> faces)
        {
            m.Nodes[i].CollisionBound = m.LeafHulls.Count;
            var seen = new HashSet<int>();
            var box = new Box();
            foreach (Face f in faces)
            {
                if (f.Plane >= 0 && seen.Add(f.Plane))
                {
                    m.LeafHulls.Add(f.Plane);
                }

                f.Verts.ForEach(box.Add);
            }

            m.LeafHulls.Add(-1);
            foreach (float v in (float[])[box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z])
            {
                m.LeafHulls.Add(BitConverter.SingleToInt32Bits(v));
            }
        }

        /// <summary>The node's plane inside the half-world box and the half-spaces of the path to it.</summary>
        private Face? Cap(int i, Vector4 plane)
        {
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            Vector3 o = n * plane.W;
            Vector3 u = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            Vector3 v = Vector3.Cross(n, u);
            const float s = HalfWorld * 4;
            Face? cap = new([o - u * s - v * s, o + u * s - v * s, o + u * s + v * s, o - u * s + v * s], i);
            for (int axis = 0; axis < 3 && cap is not null; axis++)
            {
                foreach (float sign in (float[])[-1, 1])
                {
                    Vector3 a = Vector3.Zero;
                    a[axis] = sign;
                    cap = cap is null ? null : Clip(cap, new Vector4(a, HalfWorld), false);
                }
            }

            foreach ((Vector4 p, bool front) in _path)
            {
                cap = cap is null ? null : Clip(cap, p, front);
            }

            return cap;
        }
    }

    private enum Side { On, Front, Back, Split }

    private static Face? Clip(Face f, Vector4 plane, bool keepFront) => Split(f, plane, out Face? front, out Face? back) switch
    {
        Side.On => f,
        Side.Front => keepFront ? f : null,
        Side.Back => keepFront ? null : f,
        _ => keepFront ? front : back,
    };

    /// <summary>FPoly::SplitWithPlane with the editor's quarter-unit tolerance.</summary>
    private static Side Split(Face f, Vector4 plane, out Face? front, out Face? back)
    {
        front = back = null;
        var d = new float[f.Verts.Count];
        bool anyFront = false, anyBack = false;
        for (int i = 0; i < d.Length; i++)
        {
            Vector3 p = f.Verts[i];
            d[i] = plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z - plane.W;
            anyFront |= d[i] > OnPlane;
            anyBack |= d[i] < -OnPlane;
        }

        if (!anyFront || !anyBack)
        {
            return anyFront ? Side.Front : anyBack ? Side.Back : Side.On;
        }

        front = new Face([], f.Plane);
        back = new Face([], f.Plane);
        for (int i = 0; i < d.Length; i++)
        {
            int j = (i + 1) % d.Length;
            Vector3 a = f.Verts[i];
            if (d[i] >= 0)
            {
                front.Verts.Add(a);
            }

            if (d[i] <= 0)
            {
                back.Verts.Add(a);
            }

            if ((d[i] > 0 && d[j] < 0) || (d[i] < 0 && d[j] > 0))
            {
                Vector3 x = a + (f.Verts[j] - a) * (d[i] / (d[i] - d[j]));
                front.Verts.Add(x);
                back.Verts.Add(x);
            }
        }

        return Side.Split;
    }

    private static List<Face> WorldBox()
    {
        static Vector3 C(float x, float y, float z) => new Vector3(x, y, z) * HalfWorld;
        return
        [
            new([C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1)], -1),
            new([C(-1, -1, -1), C(-1, 1, -1), C(1, 1, -1), C(1, -1, -1)], -1),
            new([C(-1, -1, -1), C(1, -1, -1), C(1, -1, 1), C(-1, -1, 1)], -1),
            new([C(-1, 1, -1), C(-1, 1, 1), C(1, 1, 1), C(1, 1, -1)], -1),
            new([C(-1, -1, -1), C(-1, -1, 1), C(-1, 1, 1), C(-1, 1, -1)], -1),
            new([C(1, -1, -1), C(1, 1, -1), C(1, 1, 1), C(1, -1, 1)], -1),
        ];
    }

    private sealed class Box
    {
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public bool Valid;

        public void Add(Vector3 v)
        {
            (Min, Max, Valid) = (Vector3.Min(Min, v), Vector3.Max(Max, v), true);
        }

        public void Add(Box o)
        {
            if (o.Valid)
            {
                Add(o.Min);
                Add(o.Max);
            }
        }
    }
}
