using System.Numerics;
using XivCloth.Core;
using XivSurface.Core;

namespace XivCloth.Rendering.Tests;

public sealed class PhysicalClothUploadTests
{
    [Theory]
    [InlineData(true, 0f)]
    [InlineData(false, 0f)]
    [InlineData(false, .4f)]
    [InlineData(false, 1f)]
    public void ActualSimulatedPatternPreservesAllMaterialPositionsAndUvsThroughUpload(bool circle, float corner)
    {
        var pattern = circle
            ? ClothRestPattern.Circle(2, 9, height: 1, pinnedVertices: [0, 8])
            : ClothRestPattern.RoundedRectangle(3, 2, corner, 13, 9, height: 1, pinnedVertices: [0, 12]);
        var solver = new XpbdCloth(pattern.ToDefinition());
        for (var step = 0; step < 120; step++)
            Assert.Equal(XpbdStatus.Ready, solver.Advance(XpbdCloth.FixedStep, MeasuredTriangleScene.Empty).Status);
        var frame = solver.Capture();
        Assert.Contains(frame.Positions.ToArray(), p => p.Y < .95f);
        Assert.Contains(frame.Positions.ToArray().Zip(pattern.Positions.ToArray()), pair =>
            Math.Abs(pair.First.X - pair.Second.X) + Math.Abs(pair.First.Z - pair.Second.Z) > .01f);
        var pose = new IndexedClothPose(frame.Positions, frame.UV, frame.Indices);
        var upload = new IndexedClothVertex[frame.Indices.Length];
        pose.WriteTriangleList(upload);
        Assert.Equal(pattern.Uv.ToArray(), frame.UV.ToArray());
        for (var i = 0; i < upload.Length; i++)
        {
            var index = frame.Indices[i];
            Assert.Equal(frame.Positions[index], upload[i].Position);
            Assert.Equal(pattern.Uv[index], upload[i].UV);
            Assert.InRange(upload[i].Normal.Length(), .99999f, 1.00001f);
            // The existing woven-map shader reads REST material locations,
            // not the current world XZ or an unmapped square-domain chart.
            var material = (upload[i].UV - new Vector2(.5f)) * new Vector2(pattern.Width, pattern.Depth);
            Assert.InRange(Vector2.Distance(material, new(pattern.Positions[index].X, pattern.Positions[index].Z)), 0, .000001f);
        }
        var before = upload.ToArray();
        for (var step = 0; step < 20; step++)
            Assert.Equal(XpbdStatus.Ready, solver.Advance(XpbdCloth.FixedStep, MeasuredTriangleScene.Empty).Status);
        pose.WriteTriangleList(upload);
        Assert.Equal(before, upload); // later simulation cannot mutate a published upload
    }

    [Fact]
    public void SeveralPhysicsStepsAreNotReplacedWithAnEndpointInterpolatedPose()
    {
        var pattern = ClothRestPattern.Circle(2, 9, height: 1, pinnedVertices: [0, 8]);
        var solver = new XpbdCloth(pattern.ToDefinition());
        var initial = solver.Capture();
        Assert.Equal(4, solver.Advance(4 * XpbdCloth.FixedStep, MeasuredTriangleScene.Empty).Substeps);
        var final = solver.Capture();
        var pose = new IndexedClothPose(final.Positions, final.UV, final.Indices);
        var upload = new IndexedClothVertex[final.Indices.Length];
        pose.WriteTriangleList(upload);
        for (var i = 0; i < upload.Length; i++) Assert.Equal(final.Positions[final.Indices[i]], upload[i].Position);
        Assert.Contains(upload.Select((vertex, i) => (vertex, index: final.Indices[i])), value =>
            value.vertex.Position != Vector3.Lerp(initial.Positions[value.index], final.Positions[value.index], .5f));
    }
}
