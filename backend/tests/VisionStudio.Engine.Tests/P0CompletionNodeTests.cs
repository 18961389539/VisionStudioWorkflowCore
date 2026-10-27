using System.Net;
using System.Text;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// P0 补全批次契约：value.jsonBuild / value.jsonParse / device.readTable / output.notify / value.counter。
/// </summary>
public sealed class P0CompletionNodeTests
{
    // ------------------------------------------------------------ value.jsonBuild

    [Fact]
    public async Task JsonBuild_PacksScalarsWithTheirOwnTypes()
    {
        var result = await Run(new JsonBuildNode(), "value.jsonBuild", new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.String("SN001"),
            ["b"] = VisionValue.Boolean(true),
            ["c"] = VisionValue.Double(12.5),
            ["d"] = VisionValue.Integer(3)
        }, """{"keys":"sn,result,x,y"}""");

        using var document = JsonDocument.Parse(result.Outputs["json"].Require<string>("json"));
        var root = document.RootElement;
        Assert.Equal("SN001", root.GetProperty("sn").GetString());
        Assert.True(root.GetProperty("result").GetBoolean());
        Assert.Equal(12.5, root.GetProperty("x").GetDouble(), 6);
        Assert.Equal(3, root.GetProperty("y").GetInt64());
        Assert.Equal(4L, result.Outputs["fieldCount"].Require<long>("fieldCount"));
    }

    [Fact]
    public async Task JsonBuild_ArrayModeKeepsPortOrderAndSkipsDashKeys()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Integer(7), ["b"] = VisionValue.String("ok")
        };
        var arrayResult = await Run(new JsonBuildNode(), "value.jsonBuild", new(inputs), """{"mode":"array"}""");
        Assert.Equal("[7,\"ok\"]", arrayResult.Outputs["json"].Require<string>("json"));

        var skipped = await Run(new JsonBuildNode(), "value.jsonBuild", new(inputs), """{"keys":"count,-"}""");
        using var document = JsonDocument.Parse(skipped.Outputs["json"].Require<string>("json"));
        Assert.True(document.RootElement.TryGetProperty("count", out _));
        Assert.Equal(1, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task JsonBuild_RejectsComplexValuesDuplicateKeysAndEmptyInput()
    {
        var detection = new VisionDetection(0, 1, 2, 3, 4, 5, 6, 1, new VisionCircle(1, 2, 3),
            new VisionRectangle2D(1, 2, 3, 4), 0.9, 1);
        var complex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonBuildNode(), "value.jsonBuild", new Dictionary<string, VisionValue>
            {
                ["a"] = VisionValue.Detections([detection])
            }, "{}"));
        Assert.Contains("'a'", complex.Message);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonBuildNode(), "value.jsonBuild", new Dictionary<string, VisionValue>
            {
                ["a"] = VisionValue.String("x"), ["b"] = VisionValue.String("y")
            }, """{"keys":"same,same"}"""));
        Assert.Contains("more than one port", duplicate.Message);

        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonBuildNode(), "value.jsonBuild", new Dictionary<string, VisionValue>(), "{}"));
        Assert.Contains("at least one connected input", empty.Message);
    }

    // ------------------------------------------------------------ value.jsonParse

    [Fact]
    public async Task JsonParse_WalksPathAndConvertsTypes()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["json"] = VisionValue.String("""{"data":{"items":[{"code":"A1","qty":"12"}],"ok":true}}""")
        };

        var code = await Run(new JsonParseNode(), "value.jsonParse", new(inputs), """{"path":"$.data.items[0].code"}""");
        Assert.True(code.Outputs["found"].Require<bool>("found"));
        Assert.Equal("A1", code.Outputs["text"].Require<string>("text"));
        Assert.Equal("String", code.Outputs["kind"].Require<string>("kind"));

        var qty = await Run(new JsonParseNode(), "value.jsonParse", new(inputs),
            """{"path":"data.items[0].qty","type":"number"}""");
        Assert.Equal(12d, qty.Outputs["number"].Require<double>("number"), 6);

        var items = await Run(new JsonParseNode(), "value.jsonParse", new(inputs), """{"path":"$.data.items"}""");
        Assert.Equal(1L, items.Outputs["count"].Require<long>("count"));
        Assert.Equal("Array", items.Outputs["kind"].Require<string>("kind"));

        var ok = await Run(new JsonParseNode(), "value.jsonParse", new(inputs), """{"path":"data.ok"}""");
        Assert.True(ok.Outputs["boolean"].Require<bool>("boolean"));
    }

    [Fact]
    public async Task JsonParse_HandlesMissingPathPerRequiredFlagAndRejectsBadPayload()
    {
        var inputs = new Dictionary<string, VisionValue> { ["json"] = VisionValue.String("""{"a":1}""") };

        var strict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonParseNode(), "value.jsonParse", new(inputs), """{"path":"a.b.c"}"""));
        Assert.Contains("was not found", strict.Message);

        var soft = await Run(new JsonParseNode(), "value.jsonParse", new(inputs),
            """{"path":"a.b.c","required":false}""");
        Assert.False(soft.Outputs["found"].Require<bool>("found"));
        Assert.False(soft.Outputs.ContainsKey("value"));

        var broken = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonParseNode(), "value.jsonParse",
                new Dictionary<string, VisionValue> { ["json"] = VisionValue.String("{oops") }, "{}"));
        Assert.Contains("not valid JSON", broken.Message);

        var wrongType = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new JsonParseNode(), "value.jsonParse",
                new Dictionary<string, VisionValue> { ["json"] = VisionValue.String("""{"a":"abc"}""") },
                """{"path":"a","type":"number"}"""));
        Assert.Contains("not a valid number", wrongType.Message);
    }

    // ------------------------------------------------------------ device.readTable

    [Fact]
    public async Task ReadTable_ReturnsRowObjectAndHonoursSequence()
    {
        var file = WriteTable("sn,result,x\nSN001,OK,1.5\nSN002,NG,2.5\nSN003,OK,3.5\n");
        try
        {
            var resolver = new StubResolver(file);
            var first = await Run(new TableReadNode(resolver), "device.readTable",
                new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(file) }, """{"path":"x"}""");
            Assert.True(first.Outputs["found"].Require<bool>("found"));
            Assert.Equal(3L, first.Outputs["total"].Require<long>("total"));
            Assert.Equal("[\"SN001\",\"OK\",\"1.5\"]", first.Outputs["rowJson"].Require<string>("rowJson"));
            using var mapped = JsonDocument.Parse(first.Outputs["rowObjectJson"].Require<string>("rowObjectJson"));
            Assert.Equal("SN001", mapped.RootElement.GetProperty("sn").GetString());

            var last = await Run(new TableReadNode(resolver), "device.readTable", new Dictionary<string, VisionValue>
            {
                ["path"] = VisionValue.String(file), ["sequence"] = VisionValue.Integer(2)
            }, """{"path":"x"}""");
            Assert.Equal(2L, last.Outputs["index"].Require<long>("index"));
            Assert.Contains("SN003", last.Outputs["row"].Require<string>("row"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ReadTable_WrapsCyclesAndFailsLoudlyWhenExhausted()
    {
        var file = WriteTable("sn\nA\nB\n");
        try
        {
            var resolver = new StubResolver(file);
            var wrapped = await Run(new TableReadNode(resolver), "device.readTable", new Dictionary<string, VisionValue>
            {
                ["path"] = VisionValue.String(file), ["sequence"] = VisionValue.Integer(5)
            }, """{"path":"x","wrap":true}""");
            Assert.Equal(1L, wrapped.Outputs["index"].Require<long>("index")); // 5 % 2

            var exhausted = await Run(new TableReadNode(resolver), "device.readTable", new Dictionary<string, VisionValue>
            {
                ["path"] = VisionValue.String(file), ["sequence"] = VisionValue.Integer(5)
            }, """{"path":"x"}""");
            Assert.False(exhausted.Outputs["found"].Require<bool>("found"));
            Assert.Contains("outside", exhausted.Outputs["diagnostics"].Require<string>("diagnostics"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ReadTable_ParsesQuotedFieldsAndGb18030Text()
    {
        var quoted = WriteTable("sn,note\nSN001,\"OK, but \"\"loose\"\"\"\n");
        try
        {
            var result = await Run(new TableReadNode(new StubResolver(quoted)), "device.readTable",
                new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(quoted) }, """{"path":"x"}""");
            Assert.Equal("[\"SN001\",\"OK, but \\\"loose\\\"\"]", result.Outputs["rowJson"].Require<string>("rowJson"));
            Assert.Equal(2L, result.Outputs["cellCount"].Require<long>("cellCount"));
        }
        finally { File.Delete(quoted); }

        var gbk = Path.Combine(Path.GetTempPath(), $"vs-gbk-{Guid.NewGuid():N}.csv");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(gbk, "工单号,结果\nSN-甲,合格\n", Encoding.GetEncoding("GB18030"));
        try
        {
            var result = await Run(new TableReadNode(new StubResolver(gbk)), "device.readTable",
                new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(gbk) },
                """{"path":"x","encoding":"gb18030"}""");
            Assert.Contains("工单号", result.Outputs["columnsJson"].Require<string>("columnsJson"));
            Assert.Contains("SN-甲", result.Outputs["row"].Require<string>("row"));
        }
        finally { File.Delete(gbk); }
    }

    [Fact]
    public async Task ReadTable_RequiresHostAuthorization()
    {
        var file = WriteTable("sn\nA\n");
        try
        {
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Run(new TableReadNode(new StubResolver(null)), "device.readTable",
                    new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(file) }, """{"path":"x"}"""));
            Assert.Contains("outside the authorized read roots", denied.Message);

            var noHost = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Run(new TableReadNode(), "device.readTable",
                    new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(file) }, """{"path":"x"}"""));
            Assert.Contains("IReplayInputResolver", noHost.Message);
        }
        finally { File.Delete(file); }
    }

    // ------------------------------------------------------------ output.notify

    [Fact]
    public async Task Notify_PostsJsonAndReportsDelivery()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var node = new NotifyNode(new HttpClient(handler));
        var result = await Run(node, "output.notify", new Dictionary<string, VisionValue>
        {
            ["message"] = VisionValue.String("3 件连续 NG"),
            ["value"] = VisionValue.Integer(3)
        }, """{"channel":"webhook","url":"http://plant.local/alarm","subject":"产线告警"}""");

        Assert.True(result.Outputs["delivered"].Require<bool>("delivered"));
        Assert.Equal(1L, result.Outputs["attempts"].Require<long>("attempts"));
        Assert.Equal(200L, result.Outputs["statusCode"].Require<long>("statusCode"));
        Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("产线告警", payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Notify_RetriesServerErrorsButNotClientErrors()
    {
        var flaky = new StubHandler(call => call == 0
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK));
        var retried = await Run(new NotifyNode(new HttpClient(flaky)), "output.notify",
            new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("x") },
            """{"channel":"webhook","url":"http://plant.local/a","retries":1,"retryDelayMs":0}""");
        Assert.True(retried.Outputs["delivered"].Require<bool>("delivered"));
        Assert.Equal(2L, retried.Outputs["attempts"].Require<long>("attempts"));

        var clientError = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new NotifyNode(new HttpClient(clientError)), "output.notify",
                new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("x") },
                """{"channel":"webhook","url":"http://plant.local/a","retries":3,"retryDelayMs":0}"""));
        Assert.Contains("400", failed.Message);
        Assert.Single(clientError.Requests); // 4xx 不重试

        var soft = await Run(new NotifyNode(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)))),
            "output.notify", new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("x") },
            """{"channel":"webhook","url":"http://plant.local/a","retries":0,"failOnError":false}""");
        Assert.False(soft.Outputs["delivered"].Require<bool>("delivered"));
    }

    [Fact]
    public async Task Notify_UsesWeComPayloadAndValidatesChannelFields()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await Run(new NotifyNode(new HttpClient(handler)), "output.notify",
            new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("相机掉线") },
            """{"channel":"wecom","url":"http://qyapi.weixin.qq.com/x","subject":"告警"}""");
        Assert.True(result.Outputs["delivered"].Require<bool>("delivered"));
        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("text", payload.RootElement.GetProperty("msgtype").GetString());
        Assert.Contains("相机掉线", payload.RootElement.GetProperty("text").GetProperty("content").GetString());

        var missingHost = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new NotifyNode(new HttpClient(handler)), "output.notify",
                new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("x") },
                """{"channel":"smtp"}"""));
        Assert.Contains("smtpHost", missingHost.Message);

        var badUrl = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new NotifyNode(new HttpClient(handler)), "output.notify",
                new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("x") },
                """{"channel":"webhook","url":"not-a-url"}"""));
        Assert.Contains("absolute http", badUrl.Message);
    }

    // ------------------------------------------------------------ value.counter

    [Fact]
    public async Task Counter_AccumulatesYieldAcrossExecutions()
    {
        var store = new InMemoryRunCounterStore();
        var node = new ValueCounterNode(store);

        await Count(node, store, true);
        await Count(node, store, true);
        var third = await Count(node, store, false);

        Assert.Equal(3L, third.Outputs["count"].Require<long>("count"));
        Assert.Equal(3L, third.Outputs["total"].Require<long>("total"));
        Assert.Equal(2L, third.Outputs["passCount"].Require<long>("passCount"));
        Assert.Equal(1L, third.Outputs["failCount"].Require<long>("failCount"));
        Assert.Equal(2d / 3d, third.Outputs["rate"].Require<double>("rate"), 6);
    }

    [Fact]
    public async Task Counter_ResetsBeforeIncrementingOnPass()
    {
        var store = new InMemoryRunCounterStore();
        var node = new ValueCounterNode(store);
        const string json = """{"counterId":"consecutiveNg","resetOnPass":true}""";

        await Count(node, store, false, json);
        await Count(node, store, false, json);
        var afterPass = await Count(node, store, true, json);
        // 复位后这一件算第 1 件，而不是第 0 件。
        Assert.Equal(1L, afterPass.Outputs["count"].Require<long>("count"));
        Assert.Equal(1L, afterPass.Outputs["total"].Require<long>("total"));

        var manual = await Count(node, store, false, """{"counterId":"consecutiveNg"}""", reset: true);
        Assert.Equal(1L, manual.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task Counter_IsolatesRunScopeAndSupportsCountdown()
    {
        var store = new InMemoryRunCounterStore();
        var node = new ValueCounterNode(store);

        await Count(node, store, true, """{"counterId":"c","scope":"run"}""", runId: "run-1");
        var otherRun = await Count(node, store, true, """{"counterId":"c","scope":"run"}""", runId: "run-2");
        Assert.Equal(1L, otherRun.Outputs["count"].Require<long>("count"));

        var workflowScoped = await Count(node, store, true, """{"counterId":"c","scope":"workflow"}""", runId: "run-2");
        Assert.Equal(1L, workflowScoped.Outputs["count"].Require<long>("count"));
        var workflowAgain = await Count(node, store, true, """{"counterId":"c","scope":"workflow"}""", runId: "run-3");
        Assert.Equal(2L, workflowAgain.Outputs["count"].Require<long>("count"));

        var countdown = await Count(node, store, true, """{"counterId":"remaining","step":-1}""");
        Assert.Equal(-1L, countdown.Outputs["count"].Require<long>("count"));
    }

    // ------------------------------------------------------------ 辅助

    private static async Task<NodeExecutorResult> Count(
        ValueCounterNode node, IRunCounterStore store, bool pass, string json = """{"counterId":"counter"}""",
        bool reset = false, string? runId = null)
    {
        var inputs = new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass) };
        if (reset) inputs["reset"] = VisionValue.Boolean(true);
        var definition = new NodeDefinition("n1", node.Type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await node.ExecuteAsync(new NodeExecutionContext(inputs, "wf-scope", runId), definition, CancellationToken.None);
    }

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, Dictionary<string, VisionValue> inputs, string json)
    {
        var node = new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static string WriteTable(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vs-table-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private sealed class StubResolver(string? resolved) : IReplayInputResolver
    {
        public string? Resolve(string requestedPath) => resolved;
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.ToString() ?? string.Empty, body));
            return respond(Requests.Count - 1);
        }
    }
}
