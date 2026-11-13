using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using ZXing;

namespace VisionStudio.Engine.Tests;

public sealed class BarcodeRecoveryTests
{
    [Theory]
    [InlineData(2, 2, false, true, "none")]
    [InlineData(2, 2, false, false, "none")]
    [InlineData(2, 2, true, false, "auto")]
    [InlineData(1, 2, true, true, "none")]
    [InlineData(1, 2, true, false, "otsu")]
    [InlineData(1, 2, true, false, "adaptive")]
    [InlineData(3, 3, false, false, "none")]
    public async Task RecoversDataMatrixGrid_WithConfiguredPreprocessingAndPolarity(
        int rows, int columns, bool inverted, bool identicalPayload, string preprocess)
    {
        using var source = Grid(rows, columns, inverted, identicalPayload);
        var result = await Decode(source, preprocess);

        Assert.Equal((long)rows * columns, result.Outputs["count"].Require<long>("count"));
        Assert.True(Convert.ToInt32(result.Summary["tileAttempts"]) > 0);
        using var json = JsonDocument.Parse(result.Outputs["results"].Require<string>("results"));
        var centres = json.RootElement.EnumerateArray().Select(item =>
        {
            var points = item.GetProperty("Points").EnumerateArray().ToArray();
            return (X: points.Average(point => point.GetProperty("x").GetDouble()),
                Y: points.Average(point => point.GetProperty("y").GetDouble()));
        }).ToArray();
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            Assert.Contains(centres, centre => Math.Abs(centre.X - (130 + column * 260)) < 10
                && Math.Abs(centre.Y - (130 + row * 260)) < 10);
        if (inverted)
            Assert.All(json.RootElement.EnumerateArray(), item => Assert.True(item.GetProperty("Inverted").GetBoolean()));
    }

    [Fact]
    public async Task RecoveryHonoursDisabledPolarityAndTileSearch()
    {
        using var normal = Grid(1, 2, inverted: false, identicalPayload: true);
        using var inverted = Grid(1, 2, inverted: true, identicalPayload: true);

        var withoutTiles = await Decode(normal, "none", tileSearch: "off");
        var withoutInversion = await Decode(inverted, "none", tryInverted: false);

        Assert.Equal(0L, withoutTiles.Outputs["count"].Require<long>("count"));
        Assert.Equal(0, Convert.ToInt32(withoutTiles.Summary["tileAttempts"]));
        Assert.Equal(0L, withoutInversion.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task RecoveryPreservesResultLimitAndReportsTruncation()
    {
        using var source = Grid(2, 2, inverted: false, identicalPayload: false);
        var result = await Decode(source, "none", maxResults: 2);

        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(true, result.Summary["truncated"]);
    }

    private static Mat Grid(int rows, int columns, bool inverted, bool identicalPayload)
    {
        var source = new Mat(rows * 260, columns * 260, MatType.CV_8UC1, Scalar.All(inverted ? 0 : 255));
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var payload = identicalPayload ? "SAME-SN-001" : $"GRID-SN-{row * columns + column + 1:000}";
            var matrix = new MultiFormatWriter().encode(payload, BarcodeFormat.DATA_MATRIX, 220, 220);
            for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                source.Set(row * 260 + 20 + y, column * 260 + 20 + x, matrix[x, y] ^ inverted ? (byte)0 : (byte)255);
        }
        return source;
    }

    private static async Task<NodeExecutorResult> Decode(Mat source, string preprocess,
        bool tryInverted = true, string tileSearch = "auto", int maxResults = 100)
    {
        using var image = VisionImage.Own(source.Clone());
        var parameters = JsonSerializer.SerializeToElement(new { mode = "2d", multiple = true, preprocess, tryInverted, tileSearch, maxResults });
        var node = new NodeDefinition("barcode", "recognition.barcode", null, null,
            parameters.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));
        return await new BarcodeDecodeNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }), node, default);
    }
}
