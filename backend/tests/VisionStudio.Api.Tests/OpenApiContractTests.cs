using System.Net;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

public sealed class OpenApiContractTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiContractTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task OpenApi_ExposesEveryRequiredProductionContractEndpoint()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var contractPath = Path.Combine(AppContext.BaseDirectory, "contracts", "api-required-endpoints.json");
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(contractPath));

        foreach (var required in contract.RootElement.GetProperty("required").EnumerateArray())
        {
            var path = required.GetProperty("path").GetString()!;
            var method = required.GetProperty("method").GetString()!;
            Assert.True(paths.TryGetProperty(path, out var pathItem), $"OpenAPI is missing required path '{path}'.");
            Assert.True(pathItem.TryGetProperty(method, out _), $"OpenAPI path '{path}' is missing required method '{method.ToUpperInvariant()}'.");
        }
    }

    [Fact]
    public async Task OpenApi_HasUniqueOperationIdsWhenOperationIdsArePresent()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));
        var operationIds = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object) continue;
                if (operation.Value.TryGetProperty("operationId", out var operationId) && operationId.ValueKind == JsonValueKind.String)
                    operationIds.Add(operationId.GetString()!);
            }
        }

        var duplicates = operationIds.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        Assert.Empty(duplicates);
    }
}
