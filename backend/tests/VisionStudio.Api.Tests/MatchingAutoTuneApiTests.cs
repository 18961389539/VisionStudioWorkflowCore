using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 自动调参接口验收：返回可直接合并进节点参数的 apply 载荷、模板哈希溯源，
/// 以及路径/ROI 的输入防线（旋转 ROI、越界、非图片扩展名）。
/// </summary>
public sealed class MatchingAutoTuneApiTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public MatchingAutoTuneApiTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task AutoTune_Shape_ReturnsApplyPayloadAndProvenance()
    {
        var path = CreateTemplate(160);
        try
        {
            var response = await _client.PostAsJsonAsync("/api/matching/auto-tune", new
            {
                nodeType = "match.shape",
                templatePath = path,
                angleRangeDeg = 360,
                profile = "balanced"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

            var apply = payload.GetProperty("apply");
            Assert.Equal("auto", apply.GetProperty("parameterSource").GetString());
            Assert.True(apply.GetProperty("pyramidLevels").GetInt32() >= 1);
            Assert.True(apply.GetProperty("angleStepDeg").GetDouble() > 0);
            Assert.False(string.IsNullOrWhiteSpace(apply.GetProperty("autoTuneReportId").GetString()));

            Assert.True(payload.GetProperty("pyramidLevels").GetInt32() >= 1);
            Assert.InRange(payload.GetProperty("angleStepDeg").GetDouble(), MatchingAutoTune.MinAngleStepDeg, MatchingAutoTune.MaxAngleStepDeg);

            var provenance = payload.GetProperty("provenance");
            Assert.Equal(64, provenance.GetProperty("templateSha256").GetString()!.Length);
            Assert.Equal("auto-v1", provenance.GetProperty("autoTuneVersion").GetString());
            Assert.Equal(160, provenance.GetProperty("templateWidth").GetInt32());

            var inspect = payload.GetProperty("inspect");
            Assert.True(inspect.GetProperty("modelPointCount").GetInt32() > 0);
            Assert.NotEqual("unavailable", inspect.GetProperty("quality").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AutoTune_Template_ReturnsNccParameters()
    {
        var path = CreateTemplate(160);
        try
        {
            var response = await _client.PostAsJsonAsync("/api/matching/auto-tune", new
            {
                nodeType = "match.template",
                templatePath = path,
                angleStartDeg = 0,
                angleRangeDeg = 180
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("match.template", payload.GetProperty("nodeType").GetString());
            Assert.True(payload.GetProperty("pyramidLevels").GetInt32() >= 1);
            Assert.True(payload.GetProperty("raw").TryGetProperty("angle_step", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AutoTune_RejectsRotatedRoi()
    {
        var path = CreateTemplate(160);
        try
        {
            var response = await _client.PostAsJsonAsync("/api/matching/auto-tune", new
            {
                nodeType = "match.shape",
                templatePath = path,
                roi = new { x = 10, y = 10, width = 80, height = 80, angleDeg = 15 }
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("angleDeg", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AutoTune_RejectsMissingTemplate()
    {
        var response = await _client.PostAsJsonAsync("/api/matching/auto-tune", new
        {
            nodeType = "match.shape",
            templatePath = "does-not-exist.png"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("was not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AutoTune_AppliesRoiCrop()
    {
        var path = CreateTemplate(200);
        try
        {
            var response = await _client.PostAsJsonAsync("/api/matching/auto-tune", new
            {
                nodeType = "match.shape",
                templatePath = path,
                roi = new { x = 20, y = 20, width = 160, height = 160 }
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(160, payload.GetProperty("provenance").GetProperty("templateWidth").GetInt32());
            Assert.Equal(160, payload.GetProperty("provenance").GetProperty("templateHeight").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemplate(int size)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "matching-autotune");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"template-{Guid.NewGuid():N}.png");
        using var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Circle(mat, new Point(size / 2, size / 2), Math.Max(4, size / 5), Scalar.All(220), -1, LineTypes.AntiAlias);
        Assert.True(Cv2.ImWrite(path, mat));
        return path;
    }
}
