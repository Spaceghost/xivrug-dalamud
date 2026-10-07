using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothPressureHeightTests
{
    [Fact]
    public void PersistentStompPatchesRemainUnrestrictedWithoutAContactHeight()
    {
        var pressure = new ClothPressure(Vector2.Zero, 1, 1);
        Assert.Equal(1, pressure.Influence(Vector2.Zero));
        Assert.Equal(1, pressure.Influence(Vector2.Zero, 400));
        Assert.Equal(1, pressure.Influence(Vector2.Zero, float.NaN));
    }

    [Fact]
    public void ContactHeightFadesContinuouslyRelativeToTheMeasuredSurface()
    {
        var pressure = new ClothPressure(Vector2.Zero, 1, 1, 3);
        Assert.Equal(1, pressure.Influence(Vector2.Zero, 3));
        Assert.Equal(1, pressure.Influence(Vector2.Zero, 3.05f));
        Assert.Equal(.5f, pressure.Influence(Vector2.Zero, 3.11f), 4);
        Assert.Equal(.5f, pressure.Influence(Vector2.Zero, 2.89f), 4);
        Assert.Equal(0, pressure.Influence(Vector2.Zero, 3.17f));
        Assert.Equal(0, pressure.Influence(Vector2.Zero, 2.83f));
        Assert.Equal(.25f, pressure.Influence(new(.5f, 0), 3.11f), 4);
    }

    [Fact]
    public void HeightAwarePressureWithoutAValidMeasurementFailsClosed()
    {
        var pressure = new ClothPressure(Vector2.Zero, 1, 1, 0);
        Assert.Equal(0, pressure.Influence(Vector2.Zero));
        Assert.Equal(0, pressure.Influence(Vector2.Zero, float.NaN));
        Assert.Equal(0, pressure.Influence(Vector2.Zero, float.PositiveInfinity));
        Assert.Equal(0, new ClothPressure(Vector2.Zero, 1, 1, float.NaN).Influence(Vector2.Zero, 0));
        Assert.Equal(0, new ClothPressure(Vector2.Zero, 1, 1, float.PositiveInfinity).Influence(Vector2.Zero, 0));
    }

    [Fact]
    public void RaisedFeetDoNotFlattenGroundButPlantedFeetStillPressItsFolds()
    {
        var contacts = CompressedGrid(17);
        var baseline = Build(contacts, []);
        var raised = Build(contacts, [new(new(.6f, 0), .6f, 1, .5f)]);
        var planted = Build(contacts, [new(new(.6f, 0), .6f, 1, .004f)]);
        Assert.Equal(baseline.Positions, raised.Positions);
        Assert.True(planted.Positions.Zip(baseline.Positions, (a, b) => b.Y - a.Y).Max() > .02f);
        Assert.All(planted.Positions, p => Assert.True(p.Y >= ClothSurface.Clearance));
        Assert.Equal(baseline.Indices, planted.Indices);
    }

    [Fact]
    public void EligibilityUsesPerVertexCellFloorBoundsNotGlobalAnchorOrRawCorner()
    {
        var contacts = CompressedGrid(17);
        var bounds = Enumerable.Repeat(.4f, 16 * 16).ToArray();
        var baseline = Build(contacts, [], bounds);
        var belowActualSurface = Build(contacts, [new(new(.6f, 0), .6f, 1, .004f)], bounds);
        var onActualSurface = Build(contacts, [new(new(.6f, 0), .6f, 1, .404f)], bounds);
        Assert.Equal(baseline.Positions, belowActualSurface.Positions);
        Assert.True(onActualSurface.Positions.Zip(baseline.Positions, (a, b) => b.Y - a.Y).Max() > .02f);
        Assert.All(onActualSurface.Positions, p => Assert.True(p.Y >= .4f + ClothSurface.Clearance));
    }

    private static ClothMesh Build(Vector3[] contacts, ClothPressure[] pressures, float[]? bounds = null) =>
        ClothSurface.Build(Vector2.Zero, new(2), 0, contacts, 17, 0, false, bounds ?? [], pressures);

    private static Vector3[] CompressedGrid(int size)
    {
        var contacts = new Vector3[size * size];
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
            contacts[z * size + x] = new(Math.Min(4f * x / (size - 1) - 2, .6f), 0, 4f * z / (size - 1) - 2);
        return contacts;
    }
}
