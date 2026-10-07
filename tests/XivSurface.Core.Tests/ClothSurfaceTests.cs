using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothSurfaceTests
{
    [Theory]
    [InlineData(33)]
    [InlineData(49)]
    public void EveryCellBelongsToOneSharedContinuousSheet(int size)
    {
        var mesh = ClothSurface.Build(Vector2.Zero, new(2), 0, new float[size * size], size, 0, false);
        Assert.Equal(size * size, mesh.Positions.Length);
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.Equal(mesh.Positions.Length, mesh.UV.Length);
        Assert.Equal((size - 1) * (size - 1) * 6, mesh.Indices.Length);
        var edges = new Dictionary<(int, int), int>();
        var parents = Enumerable.Range(0, size * size).ToArray();
        int Root(int i) { while (parents[i] != i) i = parents[i] = parents[parents[i]]; return i; }
        void Edge(int a, int b)
        {
            Assert.InRange(a, 0, mesh.Positions.Length - 1);
            Assert.InRange(b, 0, mesh.Positions.Length - 1);
            Assert.NotEqual(a, b);
            parents[Root(a)] = Root(b);
            var key = a < b ? (a, b) : (b, a);
            edges[key] = edges.GetValueOrDefault(key) + 1;
        }
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
            Edge(a, b); Edge(b, c); Edge(c, a);
            Assert.True(Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]).Y > 0);
        }
        Assert.All(parents.Select((_, i) => Root(i)), component => Assert.Equal(Root(0), component));
        Assert.All(edges.Values, adjacentFaces => Assert.InRange(adjacentFaces, 1, 2));
        Assert.Equal(4 * (size - 1), edges.Values.Count(adjacentFaces => adjacentFaces == 1));
        Assert.All(mesh.Normals, normal => Assert.True(Vector3.Distance(Vector3.UnitY, normal) < 1e-5f));
        Assert.Equal(Vector2.Zero, mesh.UV[0]);
        Assert.Equal(Vector2.One, mesh.UV[^1]);
    }

    [Fact]
    public void AMeasuredRampKeepsItsSlopeAndClearance()
    {
        const int size = 17;
        var center = new Vector2(7, -11);
        var half = new Vector2(2, 1.5f);
        var contacts = Grid(size, center, half, (x, z) => 3 + 0.35f * (x - center.X) - 0.2f * (z - center.Y));
        var mesh = ClothSurface.Build(center, half, 3, contacts, size, 0, false);
        var expectedNormal = Vector3.Normalize(new(-0.35f, 1, 0.2f));
        for (var i = 0; i < contacts.Length; i++)
        {
            Assert.Equal(contacts[i].X, mesh.Positions[i].X);
            Assert.Equal(contacts[i].Z, mesh.Positions[i].Z);
            Assert.InRange(mesh.Positions[i].Y - contacts[i].Y, ClothSurface.Clearance - 2e-5f, ClothSurface.Clearance + 2e-5f);
            Assert.True(Vector3.Dot(expectedNormal, mesh.Normals[i]) > 0.9999f);
        }
    }

    [Fact]
    public void AStepRaisesAndTensionsNearbyClothWithoutDroppingAnyFaces()
    {
        const int size = 33;
        var half = new Vector2(2);
        var contacts = Grid(size, Vector2.Zero, half, (x, _) => x >= 0 ? 0.8f : 0);
        var mesh = ClothSurface.Build(Vector2.Zero, half, 0, contacts, size, 0, false);
        Assert.Equal((size - 1) * (size - 1) * 6, mesh.Indices.Length);
        for (var i = 0; i < contacts.Length; i++) Assert.True(mesh.Positions[i].Y >= contacts[i].Y + ClothSurface.Clearance);
        var justBeforeStep = size / 2 * size + size / 2 - 1;
        Assert.True(mesh.Positions[justBeforeStep].Y > 0.5f); // The sheet climbs toward the step instead of cutting through its lip.
        AssertMaterialSlope(mesh, half);
    }

    [Fact]
    public void CellUpperBoundsProtectTheEntireTriangleInteriorWithoutSpreadingACeilingAcrossTheWholeRug()
    {
        const int size = 9;
        var ceilings = new float[(size - 1) * (size - 1)];
        var cellX = 3; var cellZ = 3;
        ceilings[cellZ * (size - 1) + cellX] = 0.9f;
        var mesh = ClothSurface.Build(Vector2.Zero, new(2), 0, new float[size * size], size, 0, false, ceilings);
        var a = cellZ * size + cellX; var b = a + 1; var c = a + size; var d = c + 1;
        foreach (var i in new[] { a, b, c, d }) Assert.True(mesh.Positions[i].Y >= 0.9f + ClothSurface.Clearance);
        // Any barycentric point on either triangle is above the cell upper bound because all corners are.
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var triangle = mesh.Indices.AsSpan(i, 3).ToArray();
            if (!triangle.All(index => index == a || index == b || index == c || index == d)) continue;
            var interior = mesh.Positions[triangle[0]] * 0.2f + mesh.Positions[triangle[1]] * 0.3f + mesh.Positions[triangle[2]] * 0.5f;
            Assert.True(interior.Y >= 0.9f + ClothSurface.Clearance - 1e-6f);
        }
        Assert.True(mesh.Positions[^1].Y < 0.05f);
        AssertMaterialSlope(mesh, new(2));
    }

    [Fact]
    public void WallCompressionMakesRealFoldsAndPreservesEveryHorizontalCollisionConstraint()
    {
        const int size = 33;
        var contacts = Grid(size, Vector2.Zero, new(2), (_, _) => 0);
        for (var i = 0; i < contacts.Length; i++) contacts[i].X = MathF.Min(contacts[i].X, 0.6f);
        var before = contacts.ToArray();
        var mesh = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 0, false);
        Assert.Equal(before, contacts);
        Assert.True(mesh.Positions.Max(p => p.Y) > ClothSurface.Clearance + 0.08f);
        for (var i = 0; i < contacts.Length; i++)
        {
            Assert.Equal(contacts[i].X, mesh.Positions[i].X);
            Assert.Equal(contacts[i].Z, mesh.Positions[i].Z);
            Assert.True(mesh.Positions[i].Y >= contacts[i].Y + ClothSurface.Clearance);
            Assert.Equal(new Vector2((float)(i % size) / (size - 1), (float)(i / size) / (size - 1)), mesh.UV[i]);
            Assert.True(float.IsFinite(mesh.Normals[i].X) && float.IsFinite(mesh.Normals[i].Y) && float.IsFinite(mesh.Normals[i].Z));
            Assert.InRange(mesh.Normals[i].Length(), 0.9999f, 1.0001f);
            Assert.True(mesh.Normals[i].Y >= 0);
        }
        AssertMaterialSlope(mesh, new(2));
        var flat = ClothSurface.Build(Vector2.Zero, new(2), 0, new float[size * size], size, 0, false);
        Assert.Equal(flat.Indices, mesh.Indices);
    }

    [Fact]
    public void MotionIsGentleAndReducedMotionActuallyStopsIt()
    {
        const int size = 17;
        var contacts = Grid(size, Vector2.Zero, new(2), (_, _) => 0);
        for (var i = 0; i < contacts.Length; i++) contacts[i].X = MathF.Min(contacts[i].X, 0.7f);
        var still = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 0, false);
        var stillLater = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 100, false);
        Assert.Equal(still.Positions, stillLater.Positions);
        var moving = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 0, true);
        var nextFrame = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 1f / 60, true);
        var maximumMovement = moving.Positions.Zip(nextFrame.Positions, Vector3.Distance).Max();
        Assert.InRange(maximumMovement, 1e-6f, 0.01f);
    }

    [Fact]
    public void MissingAndNonFiniteContactsCannotSilentlyCreateAFlatReplacement()
    {
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, new float[8], 3, 0, false));
        var heights = new float[9]; heights[4] = float.NaN;
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, heights, 3, 0, false));
        var contacts = Grid(3, Vector2.Zero, new(2), (_, _) => 0); contacts[2].X = float.PositiveInfinity;
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, 3, 0, false));
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, new float[9], 3, 0, false, new[] { 0f, 0, float.NaN, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, new float[9], 3, float.NaN, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClothSurface.Build(Vector2.Zero, new(2), 0, Array.Empty<float>(), 200, 0, false));
    }

    [Fact]
    public void FootPressureSmoothsOnlyItsLocalFoldPatchAndKeepsMeasuredClearance()
    {
        const int size = 33;
        var contacts = Grid(size, Vector2.Zero, new(2), (_, _) => 0);
        for (var i = 0; i < contacts.Length; i++) contacts[i].X = MathF.Min(contacts[i].X, 0.6f);
        var untouched = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 0, false);
        ClothPressure[] pressures = [new(new(0.6f, 0), 0.5f, 1)];
        var pressed = ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, size, 0, false, pressures: pressures);
        var flattened = 0;
        for (var i = 0; i < contacts.Length; i++)
        {
            Assert.True(pressed.Positions[i].Y >= contacts[i].Y + ClothSurface.Clearance);
            Assert.Equal(contacts[i].X, pressed.Positions[i].X);
            Assert.Equal(contacts[i].Z, pressed.Positions[i].Z);
            if (MathF.Abs(contacts[i].Z) > 1) Assert.Equal(untouched.Positions[i], pressed.Positions[i]);
            if (MathF.Abs(contacts[i].Z) < 0.001f && contacts[i].X == 0.6f && untouched.Positions[i].Y > ClothSurface.Clearance + 0.02f)
            {
                Assert.True(pressed.Positions[i].Y < untouched.Positions[i].Y - 0.02f);
                flattened++;
            }
        }
        Assert.True(flattened > 3);
        Assert.Equal(untouched.Indices, pressed.Indices);
        Assert.Equal(untouched.UV, pressed.UV);
    }

    [Fact]
    public void PressureCannotFlattenThroughACellSurfaceBound()
    {
        const int size = 9;
        var bounds = Enumerable.Repeat(0.4f, (size - 1) * (size - 1)).ToArray();
        ClothPressure[] pressures = [new(Vector2.Zero, 8, 1)];
        var mesh = ClothSurface.Build(Vector2.Zero, new(2), 0, new float[size * size], size, 1, true, bounds, pressures);
        Assert.All(mesh.Positions, position => Assert.True(position.Y >= 0.4f + ClothSurface.Clearance));
    }

    [Fact]
    public void PressureFallsOffSmoothlyAndInvalidContactsHaveNoInfluence()
    {
        var pressure = new ClothPressure(Vector2.Zero, 1, 1);
        Assert.Equal(1, pressure.Influence(Vector2.Zero));
        Assert.Equal(0.5f, pressure.Influence(new(0.5f, 0)));
        Assert.Equal(0, pressure.Influence(Vector2.UnitX));
        Assert.Equal(0, pressure.Influence(new(2, 0)));
        Assert.Equal(1, new ClothPressure(Vector2.Zero, 1, 10).Influence(Vector2.Zero));
        Assert.Equal(0, new ClothPressure(Vector2.Zero, 1, float.NaN).Influence(Vector2.Zero));
        Assert.Equal(0, new ClothPressure(new(float.PositiveInfinity, 0), 1, 1).Influence(Vector2.Zero));
        Assert.Equal(0, new ClothPressure(Vector2.Zero, -1, 1).Influence(Vector2.Zero));
    }

    private static void AssertMaterialSlope(ClothMesh mesh, Vector2 half)
    {
        var step = 2 * half / (mesh.Size - 1);
        for (var z = 0; z < mesh.Size; z++)
        for (var x = 0; x < mesh.Size; x++)
        {
            var i = z * mesh.Size + x;
            if (x + 1 < mesh.Size) Assert.True(MathF.Abs(mesh.Positions[i].Y - mesh.Positions[i + 1].Y) <= ClothSurface.MaximumMaterialSlope * step.X + 1e-5f);
            if (z + 1 < mesh.Size) Assert.True(MathF.Abs(mesh.Positions[i].Y - mesh.Positions[i + mesh.Size].Y) <= ClothSurface.MaximumMaterialSlope * step.Y + 1e-5f);
        }
    }

    private static Vector3[] Grid(int size, Vector2 center, Vector2 half, Func<float, float, float> height)
    {
        var points = new Vector3[size * size];
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var xx = center.X + (2f * x / (size - 1) - 1) * half.X;
            var zz = center.Y + (2f * z / (size - 1) - 1) * half.Y;
            points[z * size + x] = new(xx, height(xx, zz), zz);
        }
        return points;
    }
}
