using Microsoft.Data.Sqlite;

namespace VisionStudio.Api.Tests;

public sealed class DatasetValidationTests
{
    [Fact]
    public void V048Summary_ComputesFalseOkFalseNgErrorsAndTiming()
    {
        var now = DateTimeOffset.UtcNow;
        ValidationResultRecord Item(string id, string expected, string actual, string classification, double ms) =>
            new("run", id, "TRACE", $"source-{id}", expected, actual, classification, null, classification != "ERROR", ms, [], null, now);
        var summary = DatasetValidationStore.BuildSummary([
            Item("1", "OK", "OK", "TRUE_OK", 1),
            Item("2", "NG", "NG", "TRUE_NG", 2),
            Item("3", "NG", "OK", "FALSE_OK", 3),
            Item("4", "OK", "NG", "FALSE_NG", 4),
            Item("5", "OK", "ERROR", "ERROR", 5)
        ]);

        Assert.Equal(5, summary.Completed);
        Assert.Equal(1, summary.FalseOk);
        Assert.Equal(1, summary.FalseNg);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(0.5, summary.Accuracy, 6);
        Assert.Equal(3, summary.P50DurationMs, 6);
        Assert.Equal(5, summary.MaxDurationMs, 6);
    }

    [Fact]
    public void SummaryAndAcceptance_RequireBothClassesAndMinimumSampleCounts()
    {
        var now = DateTimeOffset.UtcNow;
        var okOnly = Enumerable.Range(1, 5)
            .Select(index => new ValidationResultRecord("run", index.ToString(), "TRACE", $"source-{index}", "OK", "OK",
                "TRUE_OK", null, true, 1, [], null, now))
            .ToArray();
        var summary = DatasetValidationStore.BuildSummary(okOnly);
        Assert.Null(summary.FalseOkRate);
        Assert.Equal(0, summary.FalseNgRate);

        var blockers = ProductRecipeStore.Evaluate(summary,
            new ValidationAcceptancePolicy(MinimumSamplesPerDisposition: 5));
        Assert.Contains(blockers, reason => reason.Contains("expected NG samples", StringComparison.Ordinal));

        var balanced = new ValidationRunSummary(10, 10, 5, 5, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1);
        Assert.Empty(ProductRecipeStore.Evaluate(balanced,
            new ValidationAcceptancePolicy(MinimumSamplesPerDisposition: 5)));
    }

    [Fact]
    public async Task V048Schema12_CreatesDatasetBatchValidationTables()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var status = await db.GetSchemaStatusAsync();
        Assert.Equal(SqliteSchemaMigrationRunner.CurrentVersion, status.CurrentVersion);

        await using var connection = await db.OpenConnectionAsync();
        foreach (var table in new[] { "validation_datasets", "validation_dataset_items", "validation_runs", "validation_results" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }
}
