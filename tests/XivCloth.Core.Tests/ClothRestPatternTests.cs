using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class ClothRestPatternTests
{
    [Theory]
    [InlineData(3)][InlineData(8)][InlineData(17)][InlineData(22)]
    public void CircleUsesActualCircularBoundaryWithoutOutsideParticles(int resolution)
    {
        var p = ClothRestPattern.Circle(2, resolution);
        Assert.Equal(resolution * resolution, p.Positions.Length);
        Assert.Equal(4 * resolution - 4, p.Boundary.Length);
        foreach (var id in p.Boundary) Assert.InRange(Math.Abs(p.Positions[id].Length() - 1), 0, 2e-6f);
        foreach (var point in p.Positions) Assert.InRange(point.Length(), 0, 1.000002f);
        VerifyTopology(p);
    }

    [Theory]
    [InlineData(0f)][InlineData(.1f)][InlineData(.5f)][InlineData(1f)]
    public void RoundedRectangleBoundaryHonorsCornerRadiusIncludingCapsule(float radius)
    {
        var p = ClothRestPattern.RoundedRectangle(3, 2, radius, 19, 13);
        foreach (var id in p.Boundary) Assert.InRange(Math.Abs(Distance(p.Positions[id], 3, 2, radius)), 0, 2e-6f);
        foreach (var point in p.Positions) Assert.True(Distance(point, 3, 2, radius) <= 2e-6f);
        Assert.Equal(p.Boundary.Length, p.Boundary.ToArray().Select(i => p.Positions[i]).Distinct().Count());
        VerifyTopology(p);
    }

    [Fact]
    public void CircleAndFullyRoundedSquareHaveSameMaterialPattern()
    {
        var circle = ClothRestPattern.Circle(2);
        var round = ClothRestPattern.RoundedRectangle(2, 2, 1);
        Assert.Equal(circle.Positions.ToArray(), round.Positions.ToArray());
        Assert.Equal(circle.Indices.ToArray(), round.Indices.ToArray());
        Assert.Equal(circle.Masses.ToArray(), round.Masses.ToArray());
    }

    [Fact]
    public void ZeroRadiusIsRectangularMaterialNotAClippedRoundApproximation()
    {
        var p = ClothRestPattern.RoundedRectangle(3, 2, 0, 7, 5);
        Assert.Equal(new(-1.5f, 0, -1), p.Positions[0]);
        Assert.Equal(new(1.5f, 0, 1), p.Positions[^1]);
        Assert.InRange(Math.Abs(p.Area - 6), 0, 1e-6f);
    }

    [Fact]
    public void CurvedMaterialAreaApproximatesItsBoundaryWithoutHiddenSquareMass()
    {
        var circle = ClothRestPattern.Circle(2);
        Assert.InRange(circle.Area, MathF.PI * .99f, MathF.PI);
        Assert.True(circle.Area < 3.5f); // not the enclosing square's area4
        var rounded = ClothRestPattern.RoundedRectangle(3, 2, .5f, 19, 13);
        var exact = 6 - (4 - MathF.PI) * .25f;
        Assert.InRange(rounded.Area, exact * .99f, exact + 1e-6f);
    }

    [Fact]
    public void MassIsOneThirdIncidentTriangleAreaWithUniformDensity()
    {
        var p = ClothRestPattern.Circle(2, 17, totalMass: 7);
        var areas = new double[p.Positions.Length];
        for (var at = 0; at < p.Indices.Length; at += 3)
        {
            var a = p.Indices[at]; var b = p.Indices[at + 1]; var c = p.Indices[at + 2];
            var share = Vector3.Cross(p.Positions[b] - p.Positions[a], p.Positions[c] - p.Positions[a]).Length() / 6d;
            areas[a] += share; areas[b] += share; areas[c] += share;
        }
        Assert.InRange(Math.Abs(p.Masses.ToArray().Sum(x => (double)x) - 7), 0, 1e-5);
        for (var i = 0; i < areas.Length; i++)
        {
            Assert.InRange(Math.Abs(p.Masses[i] / areas[i] - p.ArealDensity), 0, 1e-5);
            Assert.InRange(Math.Abs(p.Masses[i] * p.InverseMasses[i] - 1), 0, 1e-6f);
        }
    }

    [Fact]
    public void ScalingWithDensityPreservesUvAndTopologyWhileMassScalesByArea()
    {
        var a = ClothRestPattern.RoundedRectangle(3, 2, .4f, 19, 13, totalMass: 5);
        var b = ClothRestPattern.RoundedRectangle(6, 4, .8f, 19, 13, totalMass: 20);
        Assert.Equal(a.Indices.ToArray(), b.Indices.ToArray());
        Assert.Equal(a.Edges.ToArray(), b.Edges.ToArray());
        Assert.Equal(a.Boundary.ToArray(), b.Boundary.ToArray());
        for (var i = 0; i < a.Positions.Length; i++)
        {
            Assert.InRange(Vector3.Distance(a.Positions[i] * 2, b.Positions[i]), 0, 1e-6f);
            Assert.Equal(a.Uv[i], b.Uv[i]);
            Assert.InRange(Math.Abs(a.Masses[i] * 4 - b.Masses[i]), 0, 1e-6f);
        }
        Assert.InRange(Math.Abs(a.ArealDensity - b.ArealDensity), 0, 1e-6f);
    }

    [Fact]
    public void UvIsPhysicalRestPlaneAndDoesNotStretchSquareImageIntoCircle()
    {
        var p = ClothRestPattern.Circle(3);
        for (var i = 0; i < p.Positions.Length; i++)
        {
            Assert.InRange(p.Uv[i].X, 0, 1); Assert.InRange(p.Uv[i].Y, 0, 1);
            Assert.InRange(Math.Abs((p.Uv[i].X - .5f) * 3 - p.Positions[i].X), 0, 2e-6f);
            Assert.InRange(Math.Abs((p.Uv[i].Y - .5f) * 3 - p.Positions[i].Z), 0, 2e-6f);
        }
        Assert.True(p.Uv[0].X > 0 && p.Uv[0].Y > 0); // circle doesn't retain square UV corner(0,0)
    }

    [Fact]
    public void PinsKeepMassAndAreOwnedWithoutCallerAliasing()
    {
        int[] pins = [0, 16]; var a = ClothRestPattern.Circle(2, pinnedVertices: pins);
        pins[0] = 1; var b = ClothRestPattern.Circle(2);
        Assert.Equal(0, a.InverseMasses[0]); Assert.Equal(0, a.InverseMasses[16]); Assert.True(a.InverseMasses[1] > 0);
        Assert.Equal(a.Masses.ToArray(), b.Masses.ToArray());
        Assert.Equal(a.Positions.ToArray(), b.Positions.ToArray());
    }

    [Fact]
    public void MaxBudgetAspectResolutionIsAdmittedWithoutCoarsening()
    {
        var p = ClothRestPattern.RoundedRectangle(4, 2, .5f, 32, 16, totalMass: 10);
        Assert.Equal(512, p.Positions.Length); VerifyTopology(p);
        Assert.Equal(512, p.ToDefinition().VertexCount);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void PatternAdmitsAndAdvancesActualXpbdWithExactPins(bool circle)
    {
        var p = circle ? ClothRestPattern.Circle(2, 9, height: 1, pinnedVertices: [0, 8])
            : ClothRestPattern.RoundedRectangle(3, 2, .5f, 13, 9, height: 1, pinnedVertices: [0, 12]);
        var definition = p.ToDefinition();
        Assert.True(definition.StretchCount > 0 && definition.ShearCount > 0 && definition.BendingCount > 0);
        var solver = new XpbdCloth(definition);
        for (var frame = 0; frame < 60; frame++)
            Assert.Equal(XpbdStatus.Ready, solver.Advance(1d / 120, MeasuredTriangleScene.Empty).Status);
        var actual = solver.Capture();
        Assert.Equal(p.Positions[0], actual.Positions[0]);
        Assert.Equal(p.Positions[p.Columns - 1], actual.Positions[p.Columns - 1]);
        Assert.True(actual.Positions[p.Positions.Length / 2].Y < .99f);
        Assert.Equal(p.Uv.ToArray(), actual.UV.ToArray());
        foreach (var v in actual.Positions) Assert.True(float.IsFinite(v.X + v.Y + v.Z));
    }

    [Fact]
    public void RejectsInvalidBoundsBudgetsMassAndPinsBeforeSolverAdmission()
    {
        foreach (var diameter in new[] { float.NaN, float.PositiveInfinity, 0, -.1f, 33f })
            Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(diameter));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, 23));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, 2));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, totalMass: .001f));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, totalMass: float.NaN));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, height: float.NaN));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, pinnedVertices: [0, 0]));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, pinnedVertices: [-1]));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.Circle(2, pinnedVertices: [289]));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.RoundedRectangle(3, 2, 1.01f));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.RoundedRectangle(3, 2, -.1f));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.RoundedRectangle(32, .05f, 0, 3, 3));
        Assert.Throws<ArgumentException>(() => ClothRestPattern.RoundedRectangle(4, 2, .5f, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void GenerationIsDeterministicAndDefinitionsDoNotAliasSimulation()
    {
        var a = ClothRestPattern.Circle(2); var b = ClothRestPattern.Circle(2);
        Assert.Equal(a.Positions.ToArray(), b.Positions.ToArray()); Assert.Equal(a.Indices.ToArray(), b.Indices.ToArray());
        Assert.Equal(a.Masses.ToArray(), b.Masses.ToArray()); Assert.Equal(a.Edges.ToArray(), b.Edges.ToArray());
        var before = a.Positions.ToArray(); var solver = new XpbdCloth(a.ToDefinition());
        Assert.Equal(XpbdStatus.Ready, solver.Advance(1d / 60, MeasuredTriangleScene.Empty).Status);
        Assert.Equal(before, a.Positions.ToArray());
    }

    [Fact]
    public void CurvedSectorSeamsRemainWellConditionedForEverySquareResolution()
    {
        // A checkerboard cut created skinny ears on interior chart diagonals;
        // the defect was not confined to the four outer corner cells.
        for (var resolution = 3; resolution <= 22; resolution++)
        {
            VerifyTopology(ClothRestPattern.Circle(2, resolution, totalMass: 10));
            foreach (var radius in new[] { .01f, .25f, .75f, 1f })
                VerifyTopology(ClothRestPattern.RoundedRectangle(2, 2, radius, resolution, resolution, totalMass: 10));
        }
    }

    [Theory]
    [InlineData(4f, 1f, 32, 8)]
    [InlineData(1f, 4f, 8, 32)]
    [InlineData(4f, 2f, 32, 16)]
    [InlineData(2f, 4f, 16, 32)]
    public void ProportionateAspectChartsKeepRealRoundedMaterialAndBothShears(float width, float depth, int columns, int rows)
    {
        foreach (var fraction in new[] { .1f, .5f, 1f })
        {
            var radius = Math.Min(width, depth) * .5f * fraction;
            var p = ClothRestPattern.RoundedRectangle(width, depth, radius, columns, rows, totalMass: 10);
            VerifyTopology(p);
            Assert.Equal((columns - 1) * rows + (rows - 1) * columns, p.Edges.ToArray().Count(e => e.Kind == MaterialEdge.Stretch));
            Assert.Equal(2 * (columns - 1) * (rows - 1), p.Edges.ToArray().Count(e => e.Kind == MaterialEdge.Shear));
            for (var i = 0; i < p.Positions.Length; i++)
            {
                Assert.True(Distance(p.Positions[i], width, depth, radius) <= 2e-6f);
                Assert.InRange(Math.Abs((p.Uv[i].X - .5f) * width - p.Positions[i].X), 0, 2e-6f);
                Assert.InRange(Math.Abs((p.Uv[i].Y - .5f) * depth - p.Positions[i].Z), 0, 2e-6f);
            }
        }
    }

    private static float Distance(Vector3 p, float width, float depth, float radius)
    {
        var q = new Vector2(Math.Abs(p.X), Math.Abs(p.Z)) - new Vector2(width * .5f - radius, depth * .5f - radius);
        return Vector2.Max(q, Vector2.Zero).Length() + Math.Min(Math.Max(q.X, q.Y), 0) - radius;
    }

    private static void VerifyTopology(ClothRestPattern p)
    {
        Assert.InRange(p.Positions.Length, 3, XpbdDefinition.MaximumVertices);
        Assert.InRange(p.Indices.Length, 3, XpbdDefinition.MaximumIndices);
        Assert.InRange(p.Edges.Length, 1, XpbdDefinition.MaximumEdges);
        Assert.Equal(p.Positions.Length, p.Positions.ToArray().Distinct().Count());
        var adjacency = new Dictionary<(int, int), int>(); var used = new HashSet<int>();
        for (var i = 0; i < p.Indices.Length; i += 3)
        {
            var a = p.Indices[i]; var b = p.Indices[i + 1]; var c = p.Indices[i + 2];
            foreach (var id in new[] { a, b, c }) { Assert.InRange(id, 0, p.Positions.Length - 1); used.Add(id); }
            var ab = p.Positions[b] - p.Positions[a]; var ac = p.Positions[c] - p.Positions[a]; var bc = p.Positions[c] - p.Positions[b];
            var area2 = Vector3.Cross(ab, ac).Y; Assert.True(area2 > 0);
            Assert.True(2 * Math.Sqrt(3) * area2 / (ab.LengthSquared() + ac.LengthSquared() + bc.LengthSquared()) >= ClothRestPattern.MinimumTriangleQuality - 1e-6);
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            { var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2)); adjacency[key] = adjacency.GetValueOrDefault(key) + 1; }
        }
        Assert.Equal(p.Positions.Length, used.Count);
        Assert.All(adjacency.Values, count => Assert.InRange(count, 1, 2));
        var boundaryEdges = p.Boundary.ToArray().Select((v, i) => (v, p.Boundary[(i + 1) % p.Boundary.Length]))
            .Select(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))).ToHashSet();
        Assert.Equal(boundaryEdges.OrderBy(e => e), adjacency.Where(e => e.Value == 1).Select(e => e.Key).OrderBy(e => e));
        Assert.Equal(1, p.Positions.Length - adjacency.Count + p.Indices.Length / 3); // connected disk Euler characteristic
        var graph = new Dictionary<int, List<int>>(); var pairs = new HashSet<(int, int)>();
        foreach (var e in p.Edges)
        {
            Assert.True(pairs.Add((Math.Min(e.A, e.B), Math.Max(e.A, e.B))));
            if (!graph.TryGetValue(e.A, out var a)) graph[e.A] = a = []; a.Add(e.B);
            if (!graph.TryGetValue(e.B, out var b)) graph[e.B] = b = []; b.Add(e.A);
        }
        var reachable = new HashSet<int> { 0 }; var queue = new Queue<int>(); queue.Enqueue(0);
        while (queue.TryDequeue(out var id)) foreach (var next in graph[id]) if (reachable.Add(next)) queue.Enqueue(next);
        Assert.Equal(p.Positions.Length, reachable.Count);
        Assert.Equal(p.Positions.Length, p.ToDefinition().VertexCount); // actual production admission, including consistent hinge winding
    }
}
