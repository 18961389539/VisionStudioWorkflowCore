using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

/// <summary>
/// Read-only ABB Robot Web Services provenance probe. Motion remains in the configured robot adapter;
/// RWS is used only to lock controller identity, RobotWare and RAPID program content.
/// </summary>
public sealed class AbbRwsProvenanceProbe : CachedVendorProvenanceProbe
{
    private readonly AbbRwsProbeOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public AbbRwsProvenanceProbe(AbbRwsProbeOptions options, int cacheSeconds)
        : this(options, cacheSeconds, CreateClient(options), true) { }

    public AbbRwsProvenanceProbe(AbbRwsProbeOptions options, int cacheSeconds, HttpClient client, bool ownsClient = false)
        : base("robot", options.AssetId, "abb-rws", options.RequiredForProduction, TimeSpan.FromSeconds(Math.Max(1, cacheSeconds)))
    {
        _options = options;
        _client = client;
        _ownsClient = ownsClient;
        _client.BaseAddress ??= EnsureBaseUri(options.BaseUrl);
        _client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
    }

    protected override async ValueTask<HardwareProvenanceData> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        var identity = await GetXmlAsync("ctrl/identity", cancellationToken);
        var system = await GetXmlAsync("rw/system", cancellationToken);

        var controllerName = Span(identity, "ctrl-name");
        var controllerId = Span(identity, "ctrl-id");
        var controllerType = Span(identity, "ctrl-type");
        var mac = Span(identity, "ctrl-mac");
        var robotWare = Span(system, "rwversion") ?? Span(system, "name");
        var robotWareName = Span(system, "rwversionname");
        var systemName = Span(system, "name");
        var systemId = Span(system, "sysid");

        string? programName = null;
        string? programHash = null;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["liveProbe"] = "abb-rws",
            ["rwsBaseUrl"] = RedactBaseUrl(_client.BaseAddress!),
            ["rapidTask"] = _options.Task
        };
        Put(attributes, "controllerName", controllerName);
        Put(attributes, "controllerId", controllerId);
        Put(attributes, "controllerType", controllerType);
        Put(attributes, "controllerMac", mac);
        Put(attributes, "systemName", systemName);
        Put(attributes, "systemId", systemId);
        Put(attributes, "robotWareVersionName", robotWareName);

        try
        {
            var program = await GetXmlAsync($"rw/rapid/tasks/{E(_options.Task)}/program", cancellationToken);
            programName = SpanWithinClass(program, "rap-program", "name") ?? Span(program, "name");
            if (_options.HashRapidProgram)
                programHash = await HashRapidProgramAsync(programName, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Identity and RobotWare are still valuable on controllers/tasks where RAPID read permission is denied.
            attributes["rapidProbeState"] = "unavailable";
            attributes["rapidProbeReason"] = StableReason(ex.Message);
        }

        return new HardwareProvenanceData(
            Manufacturer: "ABB",
            ProductName: systemName ?? controllerName,
            Model: controllerType,
            SerialNumber: controllerId,
            FirmwareVersion: robotWare,
            SoftwareVersion: robotWareName ?? robotWare,
            ControllerVersion: robotWare,
            ProgramName: programName,
            ProgramHash: programHash,
            Attributes: attributes);
    }

    private async Task<string?> HashRapidProgramAsync(string? programName, CancellationToken ct)
    {
        var modulesDoc = await GetXmlAsync($"rw/rapid/modules?task={E(_options.Task)}", ct);
        var modules = modulesDoc.Descendants()
            .Where(x => Class(x) == "rap-module-info-li")
            .Select(x => new
            {
                Name = ChildSpan(x, "name"),
                Type = ChildSpan(x, "type")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && string.Equals(x.Type, "ProgMod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (modules.Length == 0) return null;

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(aggregate, $"task={_options.Task}\nprogram={programName}\n");
        long total = 0;
        foreach (var module in modules)
        {
            ct.ThrowIfCancellationRequested();
            var ext = await GetXmlAsync($"rw/rapid/modules/{E(module.Name!)}?resource=module-extension&task={E(_options.Task)}", ct);
            var linesText = Span(ext, "num-of-lines") ?? "1";
            if (!int.TryParse(linesText, out var lines) || lines < 1) lines = 1;
            var textDoc = await GetXmlAsync($"rw/rapid/modules/{E(module.Name!)}?task={E(_options.Task)}&startrow=1&startcol=1&endrow={lines}&endcol=-1", ct);
            var text = SpanWithinClass(textDoc, "rap-mod-text", "text") ?? Span(textDoc, "text") ?? "";
            var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
            total += bytes.LongLength;
            if (total > Math.Max(1024, _options.MaxRapidBytes))
                throw new InvalidOperationException($"ABB RAPID provenance exceeded MaxRapidBytes={_options.MaxRapidBytes}.");
            Append(aggregate, $"module={module.Name}\n");
            aggregate.AppendData(bytes);
            Append(aggregate, "\n");
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<XDocument> GetXmlAsync(string relative, CancellationToken ct)
    {
        using var response = await _client.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ABB RWS GET {relative} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        try { return XDocument.Parse(body, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { throw new InvalidDataException($"ABB RWS GET {relative} returned non-XML content.", ex); }
    }

    private static HttpClient CreateClient(AbbRwsProbeOptions options)
    {
        var password = Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"ABB RWS password environment variable '{options.PasswordEnvironmentVariable}' is not set.");
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(options.Username, password),
            PreAuthenticate = false
        };
        if (options.AllowInvalidTlsCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = EnsureBaseUri(options.BaseUrl) };
    }

    private static Uri EnsureBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("ABB RWS BaseUrl must be an absolute http/https URL.");
        var text = uri.ToString();
        if (!text.EndsWith('/')) text += "/";
        return new Uri(text);
    }

    private static string? Span(XDocument doc, string className)
        => doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "span" && Class(x) == className)?.Value.Trim();

    private static string? SpanWithinClass(XDocument doc, string parentClass, string spanClass)
    {
        var parent = doc.Descendants().FirstOrDefault(x => Class(x) == parentClass);
        return parent?.Descendants().FirstOrDefault(x => x.Name.LocalName == "span" && Class(x) == spanClass)?.Value.Trim();
    }

    private static string? ChildSpan(XElement parent, string className)
        => parent.Descendants().FirstOrDefault(x => x.Name.LocalName == "span" && Class(x) == className)?.Value.Trim();
    private static string? Class(XElement element) => element.Attribute("class")?.Value;
    private static string E(string value) => Uri.EscapeDataString(value);
    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    private static void Put(IDictionary<string, string> target, string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) target[key] = value; }
    private static string StableReason(string value) => value.Length <= 160 ? value : value[..160];
    private static string RedactBaseUrl(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    public override void Dispose()
    {
        base.Dispose();
        if (_ownsClient) _client.Dispose();
    }
}
