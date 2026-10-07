using System.Numerics;

namespace XivCloth.Core;

public enum ClothPatternShape { Circle, RoundedRectangle }

/// <summary>Immutable indexed material in its flat XZ rest plane. Its actual boundary
/// is curved: there are no invisible square-corner particles or discarded faces.
/// Vertex IDs are row-major and face/edge IDs are deterministic for a given pattern.
/// The generated topology and physical rest-plane UVs stay fixed during simulation.</summary>
public sealed class ClothRestPattern
{
    public const int MaximumParticles = 512;
    public const float MinimumTriangleQuality = .20f;
    private readonly Vector3[] positions;
    private readonly Vector2[] uv;
    private readonly int[] indices, boundary;
    private readonly DistanceEdge[] edges;
    private readonly float[] masses, inverseMasses;

    private ClothRestPattern(ClothPatternShape shape, float width, float depth, float radius,
        int columns, int rows, float mass, float area, Vector3[] positions, Vector2[] uv,
        int[] indices, int[] boundary, DistanceEdge[] edges, float[] masses, float[] inverseMasses)
    {
        Shape = shape; Width = width; Depth = depth; CornerRadius = radius;
        Columns = columns; Rows = rows; TotalMass = mass; Area = area;
        this.positions = positions; this.uv = uv; this.indices = indices; this.boundary = boundary;
        this.edges = edges; this.masses = masses; this.inverseMasses = inverseMasses;
    }

    public ClothPatternShape Shape { get; }
    public float Width { get; }
    public float Depth { get; }
    public float CornerRadius { get; }
    public int Columns { get; }
    public int Rows { get; }
    public float TotalMass { get; }
    public float Area { get; }
    public float ArealDensity => TotalMass / Area;
    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<Vector2> Uv => uv;
    public ReadOnlySpan<int> Indices => indices;
    /// <summary>Unique ordered perimeter IDs, with the same +Y winding as the faces.</summary>
    public ReadOnlySpan<int> Boundary => boundary;
    public ReadOnlySpan<DistanceEdge> Edges => edges;
    /// <summary>Physical lumped masses, including pinned vertices. Pins set inverse mass
    /// to zero without redistributing or deleting their material mass.</summary>
    public ReadOnlySpan<float> Masses => masses;
    public ReadOnlySpan<float> InverseMasses => inverseMasses;

    public XpbdDefinition ToDefinition() => new(positions, uv, indices, inverseMasses, edges);

    public static ClothRestPattern Circle(float diameter, int resolution = 17,
        float totalMass = 5, float height = 0, ReadOnlySpan<int> pinnedVertices = default)
        => Create(ClothPatternShape.Circle, diameter, diameter, diameter * .5f,
            resolution, resolution, totalMass, height, pinnedVertices);

    public static ClothRestPattern RoundedRectangle(float width, float depth, float cornerRadius,
        int columns = 17, int rows = 17, float totalMass = 5, float height = 0,
        ReadOnlySpan<int> pinnedVertices = default)
        => Create(ClothPatternShape.RoundedRectangle, width, depth, cornerRadius,
            columns, rows, totalMass, height, pinnedVertices);

    private static ClothRestPattern Create(ClothPatternShape shape, float width, float depth, float radius,
        int columns, int rows, float totalMass, float height, ReadOnlySpan<int> pinnedVertices)
    {
        if (!float.IsFinite(width) || !float.IsFinite(depth) || width is < .05f or > 32 || depth is < .05f or > 32
            || !float.IsFinite(radius) || radius < 0 || radius > Math.Min(width, depth) * .5f
            || !float.IsFinite(totalMass) || totalMass is <= 0 or > 10000
            || !float.IsFinite(height) || Math.Abs(height) > 1000
            || columns is < 3 or > MaximumParticles || rows is < 3 or > MaximumParticles
            || (long)columns * rows > MaximumParticles)
            throw new ArgumentException("Invalid bounded rest-pattern dimensions, resolution, mass, or height.");
        var count = columns * rows;
        var pinned = new bool[count];
        if (pinnedVertices.Length > count) throw new ArgumentException("Too many pins.");
        foreach (var id in pinnedVertices)
        {
            if ((uint)id >= count || pinned[id]) throw new ArgumentException("Pin IDs must be unique valid particles.");
            pinned[id] = true;
        }
        var positions = new Vector3[count]; var uv = new Vector2[count];
        var faces = new List<int>(6 * (columns - 1) * (rows - 1));
        var edges = new List<DistanceEdge>(4 * count);
        for (var z = 0; z < rows; z++) for (var x = 0; x < columns; x++)
        {
            var id = z * columns + x;
            var p = Map(2d * x / (columns - 1) - 1, 2d * z / (rows - 1) - 1, width, depth, radius);
            positions[id] = new((float)p.X, height, (float)p.Y);
            uv[id] = new((float)(p.X / width + .5), (float)(p.Y / depth + .5));
        }
        for (var z = 0; z < rows; z++) for (var x = 0; x < columns; x++)
        {
            var id = z * columns + x;
            if (x + 1 < columns) edges.Add(new(id, id + 1, MaterialEdge.Stretch));
            if (z + 1 < rows) edges.Add(new(id, id + columns, MaterialEdge.Stretch));
            if (x + 1 >= columns || z + 1 >= rows) continue;
            var b = id + 1; var c = id + columns; var d = c + 1;
            // Respect the radial chart's sector seams: a checkerboard alone can
            // create skinny three-point ears along every square diagonal, not
            // just the boundary corners. Choose the better minimum face quality;
            // numerically equivalent choices alternate to limit directional bias.
            // Both shear constraints remain, regardless of the rendered diagonal.
            var ad = Math.Min(Quality(positions[id], positions[c], positions[d]), Quality(positions[id], positions[d], positions[b]));
            var bc = Math.Min(Quality(positions[id], positions[c], positions[b]), Quality(positions[b], positions[c], positions[d]));
            if (bc > ad + 1e-6 || (Math.Abs(ad - bc) <= 1e-6 && ((x + z) & 1) == 0)) faces.AddRange([id, c, b, b, c, d]);
            else faces.AddRange([id, c, d, id, d, b]);
            edges.Add(new(id, d, MaterialEdge.Shear)); edges.Add(new(b, c, MaterialEdge.Shear));
        }
        var indices = faces.ToArray(); var areaByVertex = new double[count]; double area = 0;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = positions[indices[i]]; var b = positions[indices[i + 1]]; var c = positions[indices[i + 2]];
            var ab = b - a; var ac = c - a; var bc = c - b;
            var twiceArea = (double)Vector3.Cross(ab, ac).Y;
            var lengths = (double)ab.LengthSquared() + ac.LengthSquared() + bc.LengthSquared();
            var quality = 2 * Math.Sqrt(3) * twiceArea / lengths;
            if (!double.IsFinite(quality) || quality < MinimumTriangleQuality || twiceArea <= 1e-6)
                throw new ArgumentException("Aspect/resolution creates ill-conditioned cloth triangles; use proportionate columns and rows.");
            var triangleArea = .5 * twiceArea; area += triangleArea;
            areaByVertex[indices[i]] += triangleArea / 3;
            areaByVertex[indices[i + 1]] += triangleArea / 3;
            areaByVertex[indices[i + 2]] += triangleArea / 3;
        }
        var masses = new float[count]; var inverseMasses = new float[count];
        for (var i = 0; i < count; i++)
        {
            masses[i] = (float)(totalMass * areaByVertex[i] / area);
            var inverse = 1f / masses[i];
            // Match the solver's documented mass domain. Do not clamp individual
            // masses and silently distort the requested uniform material density.
            if (!float.IsFinite(inverse) || inverse > 1000)
                throw new ArgumentException("Total mass is too small for this particle resolution.");
            inverseMasses[i] = pinned[i] ? 0 : inverse;
        }
        var boundary = new int[2 * columns + 2 * rows - 4]; var at = 0;
        for (var z = 0; z < rows; z++) boundary[at++] = z * columns;
        for (var x = 1; x < columns; x++) boundary[at++] = (rows - 1) * columns + x;
        for (var z = rows - 2; z >= 0; z--) boundary[at++] = z * columns + columns - 1;
        for (var x = columns - 2; x > 0; x--) boundary[at++] = x;
        return new(shape, width, depth, radius, columns, rows, totalMass, (float)area,
            positions, uv, indices, boundary, edges.ToArray(), masses, inverseMasses);
    }

    private static double Quality(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var bc = c - b;
        return 2 * Math.Sqrt(3) * Vector3.Cross(ab, ac).Y
            / ((double)ab.LengthSquared() + ac.LengthSquared() + bc.LengthSquared());
    }

    // Rays from the origin map the rectangular chart boundary onto the exact
    // convex rounded boundary. The scalar max(|u|,|v|) preserves ordered interior
    // rings; no boundary vertices are collapsed onto the same corner/tangent.
    private static (double X, double Y) Map(double u, double v, double width, double depth, double radius)
    {
        var ring = Math.Max(Math.Abs(u), Math.Abs(v));
        if (ring == 0) return (0, 0);
        var x = u * width * .5; var y = v * depth * .5;
        var bx = Math.Abs(x) / ring; var by = Math.Abs(y) / ring;
        var cx = width * .5 - radius; var cy = depth * .5 - radius;
        if (radius == 0 || bx <= cx || by <= cy) return (x, y);
        var length = Math.Sqrt(bx * bx + by * by); var dx = bx / length; var dy = by / length;
        var projection = cx * dx + cy * dy;
        var discriminant = Math.Max(0, radius * radius - cx * cx - cy * cy + projection * projection);
        var distance = projection + Math.Sqrt(discriminant);
        var factor = distance / length;
        return (x * factor, y * factor);
    }
}
