using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class JobPublicationConcurrencyTests
{
    [Fact]
    public async Task Matching_review_publishes_and_stale_review_cannot_add_history()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(new SqliteMetadataDatabase(env));
        var created = await jobs.CreateAsync(new("job", "Job", null, Workflow()), default);
        var published = await jobs.PublishAsync("job", 1, "Publish", Manifest("first"), default, created.UpdatedAt);
        Assert.Equal(1, published.PublishedVersion);
        Assert.True(published.UpdatedAt > created.UpdatedAt);

        await Assert.ThrowsAsync<ApiConflictException>(() => jobs.PublishAsync("job", 1, "Publish", Manifest("duplicate"), default, created.UpdatedAt));
        var unchanged = (await jobs.GetAsync("job", default))!;
        Assert.Single(unchanged.PublicationHistory);
        Assert.Equal(published.PublishedDependencyManifestHash, unchanged.PublishedDependencyManifestHash);
    }

    [Fact]
    public async Task Returning_to_the_same_active_version_does_not_revalidate_an_old_review()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(new SqliteMetadataDatabase(env));
        await jobs.CreateAsync(new("job", "Job", null, Workflow()), default);
        await jobs.AddVersionAsync("job", new(Workflow(2)), default);
        var first = await jobs.PublishAsync("job", 1, "Publish", Manifest("one"), default);
        var second = await jobs.PublishAsync("job", 2, "Publish", Manifest("two"), default, first.UpdatedAt);
        var restored = await jobs.PublishAsync("job", 1, "Rollback", Manifest("restored"), default, second.UpdatedAt);
        Assert.Equal(first.PublishedVersion, restored.PublishedVersion);
        await Assert.ThrowsAsync<ApiConflictException>(() => jobs.PublishAsync("job", 2, "Publish", Manifest("stale"), default, first.UpdatedAt));
        Assert.Equal(3, (await jobs.GetAsync("job", default))!.PublicationHistory.Count);
    }

    [Fact]
    public async Task Competing_publications_from_one_review_have_exactly_one_winner()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(new SqliteMetadataDatabase(env));
        await jobs.CreateAsync(new("job", "Job", null, Workflow()), default);
        await jobs.AddVersionAsync("job", new(Workflow(2)), default);
        var baseline = (await jobs.GetAsync("job", default))!;
        async Task<bool> Publish(int version)
        {
            try { await jobs.PublishAsync("job", version, "Publish", Manifest($"v{version}"), default, baseline.UpdatedAt); return true; }
            catch (ApiConflictException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Publish(1)), Task.Run(() => Publish(2)));
        Assert.Single(results, value => value);
        Assert.Single((await jobs.GetAsync("job", default))!.PublicationHistory);
    }

    [Fact]
    public async Task Revision_advances_even_when_the_stored_timestamp_is_ahead_of_the_clock()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        await jobs.CreateAsync(new("job", "Job", null, Workflow()), default);
        var future = DateTimeOffset.UtcNow.AddHours(1);
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE jobs SET updated_at=$updated WHERE id='job';";
            command.Parameters.AddWithValue("$updated", future.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var published = await jobs.PublishAsync("job", 1, "Publish", Manifest("future"), default, future.ToOffset(TimeSpan.FromHours(8)));
        Assert.True(published.UpdatedAt > future);
        await Assert.ThrowsAsync<ApiConflictException>(() => jobs.PublishAsync("job", 1, "Publish", Manifest("stale"), default, future));
    }

    [Theory]
    [InlineData("versions/1/publish")]
    [InlineData("rollback/1")]
    public async Task Endpoints_enforce_review_revision_and_preserve_legacy_calls(string path)
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<JobStore>();
        await jobs.CreateAsync(new("publication", "Publication", null, Workflow()), default);
        await jobs.AddVersionAsync("publication", new(Workflow(2)), default);
        var baseline = (await jobs.GetAsync("publication", default))!;
        var guardedUrl = $"/api/jobs/publication/{path}?expectedUpdatedAt={Uri.EscapeDataString(baseline.UpdatedAt.ToString("O"))}";
        var first = await client.PostAsync(guardedUrl, null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stale = await client.PostAsync(guardedUrl, null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("publication review", await stale.Content.ReadAsStringAsync());
        Assert.Single((await jobs.GetAsync("publication", default))!.PublicationHistory);

        var legacy = await client.PostAsync($"/api/jobs/publication/{path}", null);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(2, (await jobs.GetAsync("publication", default))!.PublicationHistory.Count);
    }

    private static WorkflowDefinition Workflow(int revision = 1) => new("wf", "WF", [
        new NodeDefinition("source", "image.synthetic", "Source", null, new()
        {
            ["width"] = JsonSerializer.SerializeToElement(320 + revision),
            ["height"] = JsonSerializer.SerializeToElement(240),
        }),
    ], []);

    private static RuntimeDependencyManifest Manifest(string seed)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return new RuntimeDependencyManifest(1, hash, DateTimeOffset.UtcNow,
            new EngineRuntimeDependency("VisionStudio.Engine", "test", "test", hash), [], [], [], [], []);
    }
}
