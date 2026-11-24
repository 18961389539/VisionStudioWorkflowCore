using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Spc;

namespace VisionStudio.Api.Tests;

/// <summary>result_records → 数值序列的解析：类型筛选、顺序、时间范围与读取上限。</summary>
public sealed class SpcSeriesReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static async Task<(SqliteMetadataDatabase Db, SqliteResultRecordStore Store, SpcSeriesReader Reader)> CreateAsync(TempWebHostEnvironment env)
    {
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        return (db, new SqliteResultRecordStore(db), new SpcSeriesReader(db));
    }

    private static ResultRecord Record(string id, string dataset, string? key, string payload, DateTimeOffset timestamp)
        => new(id, dataset, key, payload, "run-spc", "scope", "db-node", timestamp);

    [Fact]
    public async Task ReadSeriesFiltersNonNumericPayloadsAndPreservesOrder()
    {
        using var env = new TempWebHostEnvironment();
        var (_, store, reader) = await CreateAsync(env);
        await store.AppendAsync(Record("r1", "width", "SN-1", """{"type":"Double","value":1.5}""", Start));
        await store.AppendAsync(Record("r2", "width", "SN-2", """{"type":"Integer","value":2}""", Start.AddSeconds(1)));
        await store.AppendAsync(Record("r3", "width", "SN-3", """{"type":"NumberList","value":[3,4]}""", Start.AddSeconds(2)));
        await store.AppendAsync(Record("r4", "width", null, """{"type":"String","value":"ok"}""", Start.AddSeconds(3)));
        await store.AppendAsync(Record("r5", "width", null, """{"type":"NumberList","value":[]}""", Start.AddSeconds(4)));
        await store.AppendAsync(Record("r6", "width", null, "not-json", Start.AddSeconds(5)));
        await store.AppendAsync(Record("r7", "other", null, """{"type":"Double","value":9}""", Start.AddSeconds(6)));

        var series = await reader.ReadSeriesAsync("width", null, null);

        Assert.Equal([1.5, 2, 3, 4], series.Samples.Select(sample => sample.Value));
        Assert.Equal(6, series.RecordsRead);
        Assert.Equal(2, series.UnsupportedRecords);
        Assert.Equal(1, series.EmptyRecords);
        Assert.Equal(0, series.NonFiniteSamples);
        Assert.False(series.Truncated);
        Assert.Equal("SN-3", series.Samples[2].RecordKey);
        Assert.Equal(Start.AddSeconds(2), series.Samples[2].Timestamp);
    }

    [Fact]
    public async Task ReadSeriesAppliesTimeRangeAndLimit()
    {
        using var env = new TempWebHostEnvironment();
        var (_, store, reader) = await CreateAsync(env);
        for (var index = 0; index < 3; index++)
            await store.AppendAsync(Record($"r{index}", "width", null,
                $$"""{"type":"Integer","value":{{index}}}""", Start.AddSeconds(index)));

        var ranged = await reader.ReadSeriesAsync("width", Start.AddSeconds(1), Start.AddSeconds(2));
        Assert.Equal([1d, 2d], ranged.Samples.Select(sample => sample.Value));

        var limited = await reader.ReadSeriesAsync("width", null, null, limit: 2);
        Assert.Equal(2, limited.RecordsRead);
        Assert.True(limited.Truncated);
        Assert.Single(limited.Samples, sample => sample.Value == 0);
    }

    [Fact]
    public async Task ReadSeriesSkipsNonFiniteValuesButCountsThem()
    {
        using var env = new TempWebHostEnvironment();
        var (_, store, reader) = await CreateAsync(env);
        await store.AppendAsync(Record("r1", "width", null, """{"type":"NumberList","value":[1,null,3]}""", Start));

        var series = await reader.ReadSeriesAsync("width", null, null);

        Assert.Equal([1d, 3d], series.Samples.Select(sample => sample.Value));
        Assert.Equal(1, series.NonFiniteSamples);
    }

    [Fact]
    public async Task ListDatasetsAggregatesCountsAndOrdersByLatestRecord()
    {
        using var env = new TempWebHostEnvironment();
        var (_, store, reader) = await CreateAsync(env);
        await store.AppendAsync(Record("a1", "alpha", null, """{"type":"Double","value":1}""", Start));
        await store.AppendAsync(Record("b1", "beta", null, """{"type":"Double","value":2}""", Start.AddMinutes(1)));
        await store.AppendAsync(Record("b2", "beta", null, """{"type":"Double","value":3}""", Start.AddMinutes(2)));

        var datasets = await reader.ListDatasetsAsync();

        Assert.Equal(["beta", "alpha"], datasets.Select(item => item.Dataset));
        Assert.Equal(2, datasets[0].RecordCount);
        Assert.True(datasets[0].PayloadBytes > 0);
        Assert.Equal(Start.AddMinutes(1), datasets[0].FirstRecordAt);
        Assert.Equal(Start.AddMinutes(2), datasets[0].LastRecordAt);
    }
}