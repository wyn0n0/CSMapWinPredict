using System.Security.Cryptography;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal enum SituationVisibility { Clear, Blocked, Unknown }

internal interface ISituationVisibilityQuery
{
    SituationVisibility Query(SituationVec3 left, SituationVec3 right);
}

// Static world occlusion only. No FOV, smoke, penetration or navigation inference.
internal sealed class SituationVisibilityQuery : ISituationVisibilityQuery
{
    private readonly SituationCollisionMesh? mesh;
    private readonly SituationVisibilityRuleSet rules;

    internal SituationVisibilityQuery(SituationCollisionMesh? mesh, SituationVisibilityRuleSet rules)
    {
        this.mesh = mesh;
        this.rules = rules;
    }

    public SituationVisibility Query(SituationVec3 left, SituationVec3 right)
    {
        if (mesh is null || !Valid(left) || !Valid(right)) return SituationVisibility.Unknown;
        var a = World(left);
        var b = World(right);
        foreach (var ah in rules.SampleHeights)
        foreach (var bh in rules.SampleHeights)
            if (!mesh.IsBlocked(a with { Z = a.Z + ah }, b with { Z = b.Z + bh }, rules.EndpointEpsilon))
                return SituationVisibility.Clear;
        return SituationVisibility.Blocked;
    }

    internal static CollisionPoint World(SituationVec3 p)
    {
        var map = MapFeatureGeometries.Find("de_mirage")!;
        var scale = map.Scale * 1024;
        return new(map.PositionX + p.X * scale, map.PositionY - p.Y * scale, p.Z * scale);
    }

    private static bool Valid(SituationVec3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
        double.IsFinite(p.Z) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1;
}

internal readonly record struct CollisionPoint(double X, double Y, double Z)
{
    public static CollisionPoint operator -(CollisionPoint a, CollisionPoint b) => new(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    internal double Axis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
    internal double Dot(CollisionPoint b) => X*b.X + Y*b.Y + Z*b.Z;
    internal CollisionPoint Cross(CollisionPoint b) => new(Y*b.Z-Z*b.Y, Z*b.X-X*b.Z, X*b.Y-Y*b.X);
    internal bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

internal readonly record struct CollisionTriangle(CollisionPoint A, CollisionPoint B, CollisionPoint C)
{
    internal double Center(int axis) => (A.Axis(axis)+B.Axis(axis)+C.Axis(axis))/3;
}

// AWMH v1 is documented by awpy-data/scripts/process_geometry.py.
// Immutable after construction; one BVH is shared by all queries of an analyzer.
internal sealed class SituationCollisionMesh
{
    private readonly CollisionTriangle[] triangles;
    private readonly Node root;
    internal int TriangleCount => triangles.Length;

    internal SituationCollisionMesh(IEnumerable<CollisionTriangle> source)
    {
        triangles = source.ToArray();
        if (triangles.Length == 0 || triangles.Any(t => !t.A.Finite || !t.B.Finite || !t.C.Finite))
            throw new InvalidDataException("Collision mesh has no valid geometry.");
        root = Build(0, triangles.Length);
    }

    internal static SituationCollisionMesh Load(string path, string expectedSha256)
    {
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedSha256)
            throw new InvalidDataException("Collision mesh SHA-256 mismatch.");
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        if (bytes.Length < 16 || reader.ReadUInt32() != 0x484d5741 || reader.ReadUInt32() != 1)
            throw new InvalidDataException("Unsupported collision mesh format.");
        var vertices = reader.ReadUInt32();
        var faces = reader.ReadUInt32();
        if (vertices == 0 || faces == 0 || vertices > 5_000_000 || faces > 5_000_000 ||
            bytes.LongLength != 16L + 12L * vertices + 12L * faces)
            throw new InvalidDataException("Invalid collision mesh length.");
        var points = new CollisionPoint[vertices];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!points[i].Finite) throw new InvalidDataException("Non-finite collision vertex.");
        }
        var result = new CollisionTriangle[faces];
        for (var i = 0; i < result.Length; i++)
        {
            var a = reader.ReadUInt32(); var b = reader.ReadUInt32(); var c = reader.ReadUInt32();
            if (a >= vertices || b >= vertices || c >= vertices)
                throw new InvalidDataException("Collision index is out of bounds.");
            result[i] = new(points[a], points[b], points[c]);
        }
        return new(result);
    }

    internal bool IsBlocked(CollisionPoint start, CollisionPoint end, double endpointEpsilon = 0.05)
    {
        if (!start.Finite || !end.Finite || !double.IsFinite(endpointEpsilon) || endpointEpsilon < 0)
            throw new ArgumentException("Invalid ray coordinates or tolerance.");
        var direction = end-start;
        var length = Math.Sqrt(direction.Dot(direction));
        if (length <= 2*endpointEpsilon) return false;
        var epsilon = endpointEpsilon/length;
        return Intersects(root, start, direction, epsilon, 1-epsilon);
    }

    private Node Build(int start, int count)
    {
        var min = new CollisionPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new CollisionPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (var i = start; i < start+count; i++)
        {
            Include(triangles[i].A); Include(triangles[i].B); Include(triangles[i].C);
        }
        var node = new Node(min, max, start, count);
        if (count <= 8) return node;
        var extent = max-min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(triangles, start, count, Comparer<CollisionTriangle>.Create((a,b) => a.Center(axis).CompareTo(b.Center(axis))));
        var half = count/2;
        node.Left = Build(start, half);
        node.Right = Build(start+half, count-half);
        return node;

        void Include(CollisionPoint p)
        {
            min = new(Math.Min(min.X,p.X), Math.Min(min.Y,p.Y), Math.Min(min.Z,p.Z));
            max = new(Math.Max(max.X,p.X), Math.Max(max.Y,p.Y), Math.Max(max.Z,p.Z));
        }
    }

    private bool Intersects(Node node, CollisionPoint start, CollisionPoint direction, double lo, double hi)
    {
        var enter = lo; var exit = hi;
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = start.Axis(axis); var delta = direction.Axis(axis);
            var min = node.Min.Axis(axis); var max = node.Max.Axis(axis);
            if (Math.Abs(delta) < 1e-12)
            {
                if (origin < min || origin > max) return false;
                continue;
            }
            var a = (min-origin)/delta; var b = (max-origin)/delta;
            enter = Math.Max(enter, Math.Min(a,b)); exit = Math.Min(exit, Math.Max(a,b));
            if (enter > exit) return false;
        }
        if (node.Left is not null)
            return Intersects(node.Left,start,direction,lo,hi) || Intersects(node.Right!,start,direction,lo,hi);
        for (var i = node.Start; i < node.Start+node.Count; i++)
        {
            var triangle = triangles[i];
            var e1 = triangle.B-triangle.A; var e2 = triangle.C-triangle.A;
            var h = direction.Cross(e2); var determinant = e1.Dot(h);
            if (Math.Abs(determinant) < 1e-9) continue;
            var s = start-triangle.A;
            var u = s.Dot(h)/determinant;
            if (u < -1e-9 || u > 1+1e-9) continue;
            var q = s.Cross(e1);
            var v = direction.Dot(q)/determinant;
            if (v < -1e-9 || u+v > 1+1e-9) continue;
            var t = e2.Dot(q)/determinant;
            if (t >= lo && t <= hi) return true;
        }
        return false;
    }

    private sealed class Node(CollisionPoint min, CollisionPoint max, int start, int count)
    {
        internal CollisionPoint Min { get; } = min;
        internal CollisionPoint Max { get; } = max;
        internal int Start { get; } = start;
        internal int Count { get; } = count;
        internal Node? Left { get; set; }
        internal Node? Right { get; set; }
    }
}
