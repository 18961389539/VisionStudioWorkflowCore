using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch5ContourRegressionTests
{
    [Fact]
    public async Task Extract_ReindexesParentsAfterAreaAndCountFiltering_AndReturnsOwnedImage()
    {
        using var source = new Mat(240, 320, MatType.CV_8UC1, Scalar.All(0));
        // Two independent rings plus a solid object make multiple parent groups and area rejection.
        Cv2.Rectangle(source, new Rect(15, 20, 100, 100), Scalar.All(255), -1);
        Cv2.Rectangle(source, new Rect(40, 45, 30, 30), Scalar.All(0), -1);
        Cv2.Rectangle(source, new Rect(150, 25, 80, 80), Scalar.All(255), -1);
        Cv2.Rectangle(source, new Rect(170, 45, 20, 20), Scalar.All(0), -1);
        Cv2.Rectangle(source, new Rect(270, 170, 8, 8), Scalar.All(255), -1);

        using var inputImage = VisionImage.Own(source.Clone());
        var input = VisionValue.Image(inputImage);
        var result = await new ContourExtractNode().ExecuteAsync(
            Context(("image", input)), Node("contour.extract", """{"mode":"all","binarize":"direct","minArea":500,"maxCount":3}"""), CancellationToken.None);

        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(3, contours.Count);
        Assert.Equal(contours.Count, contours.Select(c => c.Index).Distinct().Count());
        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal(3, Convert.ToInt32(result.Summary["count"]));
        foreach (var contour in contours)
        {
            if (contour.ParentIndex >= 0)
            {
                Assert.NotEqual(contour.Index, contour.ParentIndex);
                Assert.Contains(contours, parent => parent.Index == contour.ParentIndex && !parent.IsHole);
            }
        }

        var returnedImage = result.Outputs["image"].Require<VisionImage>("image");
        Assert.NotSame(source, returnedImage.Mat);
        Assert.False(source.Empty()); // The node neither disposes nor mutates its input.
        returnedImage.Dispose();
    }

    [Fact]
    public async Task Extract_HolesOnlyKeepsHoleIdentityWithoutSelfParent()
    {
        using var source = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(source, new Rect(20, 20, 120, 120), Scalar.All(255), -1);
        Cv2.Rectangle(source, new Rect(50, 50, 60, 60), Scalar.All(0), -1);
        using var inputImage = VisionImage.Own(source.Clone());
        var input = VisionValue.Image(inputImage);
        var result = await new ContourExtractNode().ExecuteAsync(
            Context(("image", input)), Node("contour.extract", """{"mode":"holes","binarize":"direct"}"""), CancellationToken.None);

        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var hole = Assert.Single(contours);
        Assert.True(hole.IsHole);
        Assert.Equal(0, hole.Index);
        Assert.Equal(-1, hole.ParentIndex);
    }

    [Fact]
    public async Task Filter_ReindexesParentReferencesWhenParentIsFilteredOrListIsReordered()
    {
        var contours = new[]
        {
            Make(0, -1, false, 100),
            Make(1, 0, true, 20),
            Make(2, -1, false, 80),
            Make(3, 2, true, 10)
        };

        var holesOnly = await new ContourFilterNode().ExecuteAsync(
            Context(("contours", VisionValue.Contours(contours))), Node("contour.filter", """{"hierarchy":"holes"}"""), CancellationToken.None);
        var holes = holesOnly.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(new[] { 0, 1 }, holes.Select(c => c.Index));
        Assert.All(holes, h => { Assert.True(h.IsHole); Assert.Equal(-1, h.ParentIndex); });

        var filtered = await new ContourFilterNode().ExecuteAsync(
            Context(("contours", VisionValue.Contours(contours))), Node("contour.filter", """{"minArea":15,"maxCount":2}"""), CancellationToken.None);
        var kept = filtered.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(new[] { 0, 1 }, kept.Select(c => c.Index));
        Assert.Equal(-1, kept[0].ParentIndex);
        Assert.Equal(0, kept[1].ParentIndex);
        Assert.True(kept[1].IsHole);
        Assert.Equal(2, Convert.ToInt32(filtered.Summary["dropped"]));
    }

    private static VisionContour Make(int index, int parent, bool hole, double area) => new(
        index, [new(0, 0), new(10, 0), new(10, 10), new(0, 10)], parent, hole,
        area, 40, 0.7, new VisionRectangle2D(index * 20, 0, 10, 10), new VisionCircle(index * 20 + 5, 5, 7));

    private static NodeExecutionContext Context(params (string Key, VisionValue Value)[] values) =>
        new(values.ToDictionary(x => x.Key, x => x.Value));

    private static NodeDefinition Node(string type, string json) => new(
        "n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
}
