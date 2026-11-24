using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using VisionStudio.Api;

namespace VisionStudio.Api.Tests;

public sealed class ProductionWorkstationTests
{
    [Fact]
    public async Task OperatorLayoutIsSavedByRecipeWithARevisionAndEngineeringPermissions()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await SignInAdminAsync(admin);
        await CreateRecipeAsync(admin, "recipe-a");
        await CreateRecipeAsync(admin, "recipe-b");

        const string path = "/api/production/operator-layout/recipe-a";
        using var initial = await admin.GetAsync(path);
        initial.EnsureSuccessStatusCode();
        var revision = initial.Headers.ETag!.Tag;
        var update = new { title = " 正面检测 ", widgets = new[] { "Alarms", "Result" } };
        Assert.Equal((HttpStatusCode)428, (await admin.PutAsJsonAsync(path, update)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WriteLayoutAsync(admin, path, revision,
            new { widgets = new[] { "Result", "Result" } })).StatusCode);
        using var saved = await WriteLayoutAsync(admin, path, revision, update);
        saved.EnsureSuccessStatusCode();
        Assert.NotEqual(revision, saved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteLayoutAsync(admin, path, revision, update)).StatusCode);
        var distinct = await admin.GetFromJsonAsync<JsonElement>("/api/production/operator-layout/recipe-b");
        Assert.Equal(JsonValueKind.Null, distinct.GetProperty("title").ValueKind);

        (await admin.PostAsJsonAsync("/api/security/users", new
        {
            username = "operator", displayName = "Operator", role = "Operator", password = "Operator!234"
        })).EnsureSuccessStatusCode();
        using var worker = factory.CreateFreshClient();
        (await worker.PostAsJsonAsync("/api/auth/login", new { username = "operator", password = "Operator!234" })).EnsureSuccessStatusCode();
        var actual = await worker.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("正面检测", actual.GetProperty("title").GetString());
        Assert.Equal(new[] { "Alarms", "Result" }, actual.GetProperty("widgets").EnumerateArray().Select(widget => widget.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await WriteLayoutAsync(worker, path, saved.Headers.ETag.Tag, update)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/production/recipe-rollouts/latest-by-station")).StatusCode);
    }

    [Fact]
    public async Task StationReadTokenOnlyReachesItsStationStatus()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("ProductionFleet:StationId", "inspection-a"));
        using var admin = configured.CreateClient();
        await SignInAdminAsync(admin);
        var token = await admin.PostAsJsonAsync("/api/security/service-tokens", new { name = "station-monitor", scopes = new[] { "stations:read" } });
        token.EnsureSuccessStatusCode();
        var secret = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var stationReader = configured.CreateClient();
        stationReader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var status = await stationReader.GetFromJsonAsync<JsonElement>("/api/production/stations/local");
        Assert.Equal("inspection-a", status.GetProperty("stationId").GetString());
        Assert.Equal("Stopped", status.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await stationReader.GetAsync("/api/production/stations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stationReader.GetAsync("/api/production/recipe-rollouts/latest-by-station")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stationReader.GetAsync("/api/production/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stationReader.PostAsync("/api/production/stop", null)).StatusCode);
    }

    [Fact]
    public async Task FleetVerifiesPublishedAndRunningVersionsAndKeepsOnlyLastVerifiedStatusOnFailure()
    {
        var hash = new string('a', 64);
        var desired = new ProductionStationOptions
        {
            Id = "inspection-a", Name = "Inspection A", BaseUrl = "https://station.example/vision/",
            Token = "vst_example", ExpectedJobId = "recipe-a", ExpectedVersion = 2, ExpectedWorkflowHash = hash
        };
        var status = new ProductionStationStatus("inspection-a", ProductionRuntimeState.Running, "recipe-a",
            new StationJob("Recipe A", 2, hash), 1, new string('b', 64), 1, 1, 0, "OK", null, 0, 0, 0);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var httpStatus = HttpStatusCode.OK;
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("https://station.example/vision/api/production/stations/local", request.RequestUri!.AbsoluteUri);
            Assert.Equal("vst_example", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(httpStatus) { Content = JsonContent.Create(status, options: json) };
        });
        using var fleet = new ProductionFleet(Options.Create(new ProductionFleetOptions { Stations = [desired] }),
            new StubClientFactory(handler), TimeProvider.System);

        Assert.Equal(StationConnection.Waiting, Assert.Single(fleet.Snapshot()).Connection);
        await fleet.PollAsync(default);
        Assert.Equal(StationVersionCheck.Mismatch, Assert.Single(fleet.Snapshot()).VersionCheck);

        status = status with { LockedVersion = 2, LockedWorkflowHash = new string('b', 64) };
        await fleet.PollAsync(default);
        Assert.Equal(StationVersionCheck.Mismatch, Assert.Single(fleet.Snapshot()).VersionCheck);

        status = status with { LockedWorkflowHash = hash };
        await fleet.PollAsync(default);
        Assert.Equal(StationVersionCheck.Matches, Assert.Single(fleet.Snapshot()).VersionCheck);

        status = status with { State = ProductionRuntimeState.Stopped, LockedVersion = null, LockedWorkflowHash = null };
        await fleet.PollAsync(default);
        var stopped = Assert.Single(fleet.Snapshot());
        Assert.Equal(StationVersionCheck.PublishedOnly, stopped.VersionCheck);
        Assert.Equal("https://station.example/vision/?view=operator", stopped.OperatorUrl);

        status = status with { StationId = "unexpected-station" };
        await fleet.PollAsync(default);
        var mismatch = Assert.Single(fleet.Snapshot());
        Assert.Equal(StationConnection.IdentityMismatch, mismatch.Connection);
        Assert.Equal(StationVersionCheck.Unverified, mismatch.VersionCheck);
        Assert.Equal(stopped.LastSeenAt, mismatch.LastSeenAt);
        Assert.Equal("inspection-a", mismatch.Status?.StationId);

        httpStatus = HttpStatusCode.Unauthorized;
        await fleet.PollAsync(default);
        Assert.Equal(StationConnection.Unauthorized, Assert.Single(fleet.Snapshot()).Connection);
    }

    private static async Task CreateRecipeAsync(HttpClient admin, string id)
    {
        var workflow = new
        {
            id, name = id,
            nodes = new[] { new { id = "source", name = "Source", type = "image.synthetic", parameters = new { } } },
            edges = Array.Empty<object>()
        };
        using var response = await admin.PostAsJsonAsync("/api/jobs", new { id, name = id, workflow });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> WriteLayoutAsync(HttpClient client, string path, string revision, object update)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(update) };
        request.Headers.IfMatch.ParseAdd(revision);
        return await client.SendAsync(request);
    }

    private static async Task SignInAdminAsync(HttpClient admin)
    {
        (await admin.PostAsJsonAsync("/api/auth/bootstrap", new { username = "admin", displayName = "Administrator", password = "AdminPass!234" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "AdminPass!234" })).EnsureSuccessStatusCode();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
