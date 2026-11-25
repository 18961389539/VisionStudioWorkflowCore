from __future__ import annotations

import json
import re
from .context import AuditContext

EXPECTED_ROUTE_COUNT = 357


def audit_host_and_api_contracts(ctx: AuditContext, catalog_ctx: dict) -> dict:
    program = ctx.read_safe("backend/src/VisionStudio.Api/Program.cs")
    host_registration = catalog_ctx["host_registration"]
    host_bootstrap = ctx.read_safe("backend/src/VisionStudio.Api/Hosting/BootstrapHostedServices.cs")
    api_source_files = list((ctx.root / "backend/src/VisionStudio.Api").rglob("*.cs"))
    api_sources = "\n".join(p.read_text(encoding="utf-8") for p in api_source_files)
    endpoint_sources = "\n".join(
        p.read_text(encoding="utf-8")
        for p in (ctx.root / "backend/src/VisionStudio.Api/Endpoints").glob("*.cs")
    )

    api_csproj = ctx.read_safe("backend/src/VisionStudio.Api/VisionStudio.Api.csproj")
    packages_props = ctx.read_safe("backend/Directory.Packages.props")
    if "Microsoft.AspNetCore.OpenApi" not in api_csproj:
        ctx.fail("OpenAPI package missing from API csproj")
    if "Microsoft.Data.Sqlite" not in api_csproj:
        ctx.fail("Microsoft.Data.Sqlite missing from API csproj")
    if "Microsoft.Data.Sqlite" not in packages_props or "10.0.12" not in packages_props:
        ctx.fail("Microsoft.Data.Sqlite 10.0.12 missing from Directory.Packages.props")
    if "services.AddOpenApi(" not in host_registration or "app.MapOpenApi()" not in program:
        ctx.fail("OpenAPI service/endpoint not wired")
    if "AddSchemaTransformer<JsonElementSchemaTransformer>" not in host_registration:
        ctx.fail("JsonElement OpenAPI schema transformer not registered")
    if "AddDocumentTransformer<StructSchemaRequiredDocumentTransformer>" not in host_registration:
        ctx.fail("Struct schema required document transformer not registered")
    if "V0.63" not in endpoint_sources:
        ctx.fail("API health version not V0.63")
    api_smoke = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/ApiSmokeTests.cs")
    if "v0.63" not in api_smoke.lower():
        ctx.fail("API smoke test does not assert V0.63 health surface")
    if "RuntimeOnlyPolicy.IsMutationAllowed" not in api_sources:
        ctx.fail("Runtime-only explicit mutation allowlist is missing")

    abstractions_csproj = ctx.read_safe("backend/src/VisionStudio.Abstractions/VisionStudio.Abstractions.csproj")
    engine_csproj = ctx.read_safe("backend/src/VisionStudio.Engine/VisionStudio.Engine.csproj")
    sample_csproj = ctx.read_safe("backend/src/VisionStudio.Plugin.Sample/VisionStudio.Plugin.Sample.csproj")
    plugin_contracts = ctx.read_safe("backend/src/VisionStudio.Abstractions/PluginContracts.cs")
    execution_contracts = ctx.read_safe("backend/src/VisionStudio.Abstractions/ExecutionContracts.cs")
    plugin_registry = ctx.read_safe("backend/src/VisionStudio.Engine/PluginSdk.cs")
    plugin_manager = (
        ctx.read_family("backend/src/VisionStudio.Api/Plugin/PluginManager*.cs")
        + "\n"
        + ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginPackageModels.cs")
    )
    node_dispatcher = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionNodeDispatcher.cs")
    if "<PackageReference" in abstractions_csproj:
        ctx.fail("VisionStudio.Abstractions must not have third-party package references")
    if "VisionStudio.Abstractions" not in engine_csproj:
        ctx.fail("Engine does not reference VisionStudio.Abstractions")
    if "VisionStudio.Abstractions" not in sample_csproj or "VisionStudio.Engine" in sample_csproj:
        ctx.fail("Sample plugin must reference Abstractions only, not Engine")
    for token in ["VisionExecutorLifetime", "VisionExecutorConcurrency", "Func<IVisionNodeExecutor> Factory", "IVisionPlugin"]:
        if token not in plugin_contracts:
            ctx.fail(f"Missing plugin contract token: {token}")
    for token in ["IVisionNodeExecutor", "NodeExecutionContext", "NodeExecutorResult"]:
        if token not in execution_contracts:
            ctx.fail(f"Missing executor abstraction token: {token}")
    for token in ["RegisterFactory", "VisionExecutorConcurrency.Serialized", "SemaphoreSlim", "SupportsParallel == false"]:
        if token not in plugin_registry:
            ctx.fail(f"Missing runtime plugin enforcement token: {token}")
    if "Transient" not in plugin_contracts or "Singleton" not in plugin_contracts:
        ctx.fail("Plugin lifetime enum is incomplete")
    if "registration.ExecuteAsync" not in node_dispatcher:
        ctx.fail("VisionNodeDispatcher bypasses host-managed registration execution")
    if "typeof(VisionNodeRegistry)" in plugin_manager or "VisionStudio.Engine.Runtime" in plugin_manager:
        ctx.fail("Plugin loader shares Engine/runtime types with plugins")
    if "typeof(IVisionPlugin)" not in plugin_manager or "typeof(VisionPluginNode)" not in plugin_manager:
        ctx.fail("Plugin loader does not share Abstractions contract types")
    sample_source = ctx.read_safe("backend/src/VisionStudio.Plugin.Sample/SampleMathPlugin.cs")
    if "using VisionStudio.Engine" in sample_source:
        ctx.fail("Sample plugin source still imports Engine")
    if "VisionPluginNode.PerNode" not in sample_source:
        ctx.fail("Sample plugin does not demonstrate SDK 2.0 PerNode lifetime")
    for p in (ctx.root / "backend/src/VisionStudio.Abstractions").glob("*.cs"):
        txt = p.read_text(encoding="utf-8")
        for forbidden in ["using OpenCvSharp", "using WorkflowCore", "using NModbus", "using S7.Net", "using VisionStudio.Engine"]:
            if forbidden in txt:
                ctx.fail(f"Abstractions leaks implementation dependency {forbidden}: {p.name}")
    ctx.ok("V0.22 Abstractions-only plugin boundary + factory/lifetime/concurrency semantics present")

    contract = json.loads(ctx.read_safe("contracts/api-required-endpoints.json"))
    if contract.get("version") != "v0.63":
        ctx.fail(f"API contract version is not v0.63: {contract.get('version')}")
    endpoint_sources_normalized = re.sub(r"\{([A-Za-z0-9_]+):[A-Za-z0-9_]+\}", r"{\1}", endpoint_sources)
    seen_contract = set()
    for item in contract.get("required", []):
        pair = (item.get("method", "").lower(), item.get("path", ""))
        if pair in seen_contract:
            ctx.fail(f"Duplicate API contract entry: {pair}")
        seen_contract.add(pair)
        method = pair[0].capitalize()
        if f'app.Map{method}("{pair[1]}"' not in endpoint_sources_normalized:
            ctx.fail(f"Contract endpoint is not mapped in endpoint modules: {pair[0].upper()} {pair[1]}")
    ctx.ok(f"API contract endpoints: {len(seen_contract)}")

    route_pairs = re.findall(r'\.Map(Get|Post|Put|Delete|Patch)\("([^"]+)"', endpoint_sources)
    normalized_routes = [(method.lower(), path) for method, path in route_pairs]
    if len(normalized_routes) != len(set(normalized_routes)):
        duplicates = sorted({x for x in normalized_routes if normalized_routes.count(x) > 1})
        ctx.fail(f"Duplicate endpoint mappings: {duplicates}")
    if len(set(normalized_routes)) != EXPECTED_ROUTE_COUNT:
        ctx.fail(f"Full route inventory changed unexpectedly: expected {EXPECTED_ROUTE_COUNT}, got {len(set(normalized_routes))}")
    ctx.ok(f"Full endpoint inventory: {len(set(normalized_routes))}")

    if len(program.splitlines()) > 80:
        ctx.fail(f"Program.cs remains a God file: {len(program.splitlines())} lines")
    for token in ["AddProblemDetails()", "AddExceptionHandler<ApiExceptionHandler>()"]:
        if token not in host_registration:
            ctx.fail(f"ProblemDetails infrastructure missing: {token}")
    if "app.UseExceptionHandler();" not in program:
        ctx.fail("Global exception handler middleware is not enabled")
    exception_handler = ctx.read_safe("backend/src/VisionStudio.Api/Infrastructure/ApiExceptionHandler.cs")
    for status in ["Status400BadRequest", "Status404NotFound", "Status409Conflict", "Status503ServiceUnavailable", "Status500InternalServerError"]:
        if status not in exception_handler:
            ctx.fail(f"ProblemDetails mapper missing {status}")
    for token in ["correlationId", "X-Correlation-ID"]:
        if token not in exception_handler:
            ctx.fail(f"ProblemDetails correlation token missing: {token}")
    if "catch (Exception ex) { return Results.BadRequest" in endpoint_sources:
        ctx.fail("Endpoint modules still contain catch-all Exception -> 400 mapping")
    for token in ["StorageMigrationHostedService", "RuntimeBootstrapHostedService", "ProductionRuntimeHostedService", "TraceRetentionHostedService"]:
        if f"AddHostedService<{token}>()" not in host_registration:
            ctx.fail(f"Hosted service registration missing: {token}")
    order = [
        host_registration.find(f"AddHostedService<{x}>()")
        for x in ["StorageMigrationHostedService", "RuntimeBootstrapHostedService", "ProductionRuntimeHostedService", "TraceRetentionHostedService"]
    ]
    if any(x < 0 for x in order) or order != sorted(order):
        ctx.fail("Hosted startup order is not migrate -> bootstrap -> production -> retention")
    if "Cors:AllowedOrigins" not in host_registration:
        ctx.fail("CORS origins are not configuration-backed")
    if "app.MapVisionStudioApi();" not in program:
        ctx.fail("Modular endpoint root mapping is missing")
    ctx.ok(f'V0.23 modular host retained: Program.cs={len(program.splitlines())} lines, endpoint modules={len(list((ctx.root/"backend/src/VisionStudio.Api/Endpoints").glob("*.cs")))}')

    security_models = ctx.read_safe("backend/src/VisionStudio.Api/Security/SecurityModels.cs")
    security_store = ctx.read_safe("backend/src/VisionStudio.Api/Security/SecurityStore.cs")
    auth_handler = ctx.read_safe("backend/src/VisionStudio.Api/Security/VisionStudioAuthenticationHandler.cs")
    audit_source = ctx.read_safe("backend/src/VisionStudio.Api/Security/Audit.cs")
    security_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/SecurityEndpoints.cs")
    ctx.read_safe("frontend/src/security.tsx")
    for token in ["SecurityRoles.Operator", "SecurityRoles.Engineer", "SecurityRoles.Administrator", "SetFallbackPolicy", "AddAuthentication", "AddRateLimiter", "RateLimitPartition.GetFixedWindowLimiter"]:
        if token not in host_registration:
            ctx.fail(f"V0.24 security registration missing: {token}")

    schema_migration_source = ctx.read_family("backend/src/VisionStudio.Api/SchemaMigrations*.cs")
    current_schema_match = re.search(r"CurrentVersion\s*=\s*(\d+)", schema_migration_source)
    current_schema_token = f"CurrentVersion = {current_schema_match.group(1)}" if current_schema_match else "CurrentVersion = 0"
    security_schema = ctx.read_safe("backend/src/VisionStudio.Api/Storage/SqliteMetadataDatabase.cs") + "\n" + schema_migration_source
    for token in ["security_users", "security_sessions", "audit_events", current_schema_token]:
        if token not in security_schema:
            ctx.fail(f"V0.24 security schema missing: {token}")
    for token in ["Rfc2898DeriveBytes.Pbkdf2", "CryptographicOperations.FixedTimeEquals", "SHA256.HashData", "At least one enabled Administrator must remain"]:
        if token not in security_store + ctx.read_safe("backend/src/VisionStudio.Api/Security/ServiceTokenStore.cs"):
            ctx.fail(f"Credential/session hardening missing: {token}")
    if "password TEXT" in security_schema.lower() or "password TEXT" in security_store:
        ctx.fail("Plaintext password persistence detected")
    if "CookieName" not in security_models or "vs_session" not in security_models:
        ctx.fail("HttpOnly session cookie option is missing")
    for token in ["Request.Cookies[_security.CookieName]", "Bearer ", "HandleChallengeAsync", "HandleForbiddenAsync"]:
        if token not in auth_handler:
            ctx.fail(f"Authentication handler missing: {token}")
    for token in ["AuditActionMetadata", "audit_events", "FindFirstValue", "RouteTarget"]:
        if token not in audit_source:
            ctx.fail(f"Audit implementation missing: {token}")
    for token in ["/api/auth/bootstrap", "/api/auth/login", "/api/auth/me", "/api/security/users", "/api/audit"]:
        if token not in security_endpoints:
            ctx.fail(f"Security endpoint missing: {token}")
    if security_endpoints.count('RequireRateLimiting("auth")') < 2:
        ctx.fail("Bootstrap/login rate limiting is not applied")
    for token in ["RequireOperator(", "RequireEngineer(", "RequireAdministrator("]:
        if token not in endpoint_sources:
            ctx.fail(f"Role-protected mutation mappings missing: {token}")
    if "app.UseAuthentication();" not in program or "app.UseAuthorization();" not in program or "app.UseMiddleware<AuditMiddleware>();" not in program:
        ctx.fail("Authentication/authorization/audit middleware pipeline is incomplete")
    if "/api/auth/" not in ctx.read_safe("backend/src/VisionStudio.Api/Infrastructure/RuntimeOnlyMiddleware.cs"):
        ctx.fail("Runtime-only host blocks security/session administration")
    security_settings = json.loads(ctx.read_safe("backend/src/VisionStudio.Api/appsettings.json"))
    if not security_settings.get("Security", {}).get("Enabled", False):
        ctx.fail("Security is not enabled by default")

    reverse_proxy_source = ctx.read_safe("backend/src/VisionStudio.Api/Hosting/ReverseProxyRegistration.cs")
    if "app.UseForwardedHeaders();" not in program:
        ctx.fail("Program.cs does not call app.UseForwardedHeaders() (reverse proxy boundary missing)")
    if program.index("app.UseForwardedHeaders();") > program.index("app.UseRateLimiter();"):
        ctx.fail("app.UseForwardedHeaders() must run before app.UseRateLimiter() (rate-limit partition needs the real client IP)")
    if "AddVisionStudioForwardedHeaders" not in host_registration:
        ctx.fail("ForwardedHeaders options are not registered in AddVisionStudioHost")
    for token in ["ForwardLimit", "KnownIPNetworks", "KnownProxies"]:
        if token not in reverse_proxy_source:
            ctx.fail(f"ForwardedHeaders hardening missing: {token}")
    if security_settings.get("ReverseProxy", {}).get("Enabled", True):
        ctx.fail("ReverseProxy must default to disabled (enabling it without a trusted proxy lets any client spoof X-Forwarded-For)")
    if "MapOpenApi().AllowAnonymous()" in program:
        ctx.fail("OpenAPI document must not be anonymous outside Development (full attack surface listing)")
    production_settings = json.loads(ctx.read_safe("backend/src/VisionStudio.Api/appsettings.Production.json"))
    if production_settings.get("RobotTcpSimulator", {}).get("Enabled", True):
        ctx.fail("RobotTcpSimulator must be disabled in Production (a virtual robot must not be selectable in the field)")
    if "environment.IsProduction()" not in host_bootstrap:
        ctx.fail("Robot bootstrap must hard-refuse the simulator in Production, not only rely on appsettings")
    if "simulated: true" not in host_bootstrap:
        ctx.fail("Simulator-backed robot adapter must be registered with simulated: true so the UI can flag it")
    if "bool Simulated" not in ctx.read_safe("backend/src/VisionStudio.Engine/Robot/RobotContracts.cs"):
        ctx.fail("RobotDescriptor must expose a Simulated flag (otherwise a virtual robot is indistinguishable in UI/audit)")
    ctx.ok("V0.24 RBAC + PBKDF2 local users + revocable sessions + audit trail present")

    service_token_store = ctx.read_safe("backend/src/VisionStudio.Api/Security/ServiceTokenStore.cs")
    service_token_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Operations/ServiceTokenTests.cs")
    audit_chain_source = ctx.read_safe("backend/src/VisionStudio.Api/Security/AuditChain.cs")
    audit_chain_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Operations/AuditChainTests.cs")
    for token in ["security_service_tokens", "token_hash", "scopes", "revoked_at", "last_used_at"]:
        if token not in schema_migration_source:
            ctx.fail(f"P3 service-token schema missing: {token}")
    for token in ["vst_", "SHA256.HashData", "RandomNumberGenerator.GetBytes(32)", "NormalizeScopes", "RotateAsync", "RevokeAsync", "last_used_at"]:
        if token not in service_token_store:
            ctx.fail(f"P3 service-token store missing: {token}")
    for token in ["SecurityRoles.Service", "ServiceTokenId", "class ServiceScopes", "results:read", "runs:run", "ResultsReadOrScope", "RunTriggerOrScope"]:
        if token not in security_models + auth_handler:
            ctx.fail(f"P3 service-token scope model missing: {token}")
    for token in ["SecurityPolicies.ResultsRead", "SecurityPolicies.RunTrigger", "SecurityClaims.ServiceTokenId"]:
        if token not in host_registration:
            ctx.fail(f"P3 service-token policy registration missing: {token}")
    for token in ["/api/security/service-tokens", "/api/security/audit/verify"]:
        if token not in security_endpoints:
            ctx.fail(f"P3 service-token/audit endpoint missing: {token}")
    for token in ["prev_hash", "ApplyAuditChainAsync", "audit_events"]:
        if token not in schema_migration_source:
            ctx.fail(f"P3 audit-chain schema missing: {token}")
    for token in ["GenesisHash", "AuditChainEntry", "ComputeHash"]:
        if token not in audit_chain_source:
            ctx.fail(f"P3 audit-chain hash implementation missing: {token}")
    for token in ["ReadChainHeadAsync", "VerifyChainAsync", "deferred: false", "prev_hash", "hash"]:
        if token not in audit_source:
            ctx.fail(f"P3 audit-chain append/verify missing: {token}")
    for token in ["ServiceToken_IsStoredHashed", "ResultsReadToken_ReadsTraces", "RotationReplacesTheSecret"]:
        if token not in service_token_tests:
            ctx.fail(f"P3 service-token regression test missing: {token}")
    for token in ["ModifiedRow_IsReportedWithItsSequence", "DeletedMiddleRow_BreaksTheLinkAtTheNextRow", "VerificationEndpoint_RequiresAdministrator"]:
        if token not in audit_chain_tests:
            ctx.fail(f"P3 audit-chain regression test missing: {token}")
    for doc in ["docs/service-tokens.md", "docs/audit-integrity.md"]:
        if not (ctx.root / doc).exists():
            ctx.fail(f"P3 documentation missing: {doc}")
    ctx.ok("P3 scoped hashed service tokens + tamper-evident audit hash chain present")

    trace_store_dependency = ctx.read_family("backend/src/VisionStudio.Api/Trace/TraceabilityStore*.cs")
    dependency_source = ctx.read_family("backend/src/VisionStudio.Api/RuntimeDependencies*.cs")
    job_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/JobEndpoints.cs")
    trace_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/TraceEndpoints.cs")
    production_source = ctx.read_family("backend/src/VisionStudio.Api/Production/ProductionRuntime*.cs")
    job_store_source = ctx.read_safe("backend/src/VisionStudio.Api/Operations/JobStore.cs")
    for token in ["RuntimeDependencyManifest", "EngineRuntimeDependency", "PluginRuntimeDependency", "CameraRuntimeDependency", "DeviceRuntimeDependency", "RobotRuntimeDependency", "CalibrationRuntimeDependency", "ManifestHash", "CaptureAsync", "ValidateAsync", "ValidateOrThrowAsync"]:
        if token not in dependency_source:
            ctx.fail(f"Runtime dependency manifest implementation missing: {token}")
    for token in ["registration.Source", "AssemblySha256", "cameraId", "deviceId", "robotId", "assetId", "assetVersion", "ConfigurationHash"]:
        if token not in dependency_source + plugin_manager:
            ctx.fail(f"Runtime dependency capture token missing: {token}")
    for token in ["runtime_dependency_manifests", "published_dependency_manifest_hash", "dependency_manifest_hash", current_schema_token]:
        if token not in security_schema:
            ctx.fail(f"V0.25 dependency schema missing: {token}")
    for token in ["StoreManifestAsync", "GetPublishedSnapshotAsync", "dependency_manifest_hash", "PublishedDependencyManifestHash"]:
        if token not in job_store_source:
            ctx.fail(f"Job publication dependency persistence missing: {token}")
    for token in ["GetPublishedSnapshotAsync", "ValidateOrThrowAsync", "PROD-DEPENDENCY-DRIFT", "EnsureDependencyMutationAllowed", "LockedDependencyManifestHash"]:
        if token not in production_source:
            ctx.fail(f"Production dependency drift guard missing: {token}")
    for token in ["/api/jobs/{id}/dependencies", "/api/jobs/{id}/dependencies/validate"]:
        if token not in job_endpoints:
            ctx.fail(f"Job dependency endpoint missing: {token}")
    if "/api/traces/{runId}/dependencies" not in trace_endpoints:
        ctx.fail("Trace dependency provenance endpoint missing")
    if "GetDependencyManifestAsync" not in trace_store_dependency:
        ctx.fail("TraceabilityStore cannot resolve dependency manifest")
    if "AddSingleton<RuntimeDependencyManifestService>()" not in host_registration:
        ctx.fail("RuntimeDependencyManifestService is not registered")
    for rel in [
        "backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs",
        "backend/src/VisionStudio.Api/Endpoints/DeviceEndpoints.cs",
        "backend/src/VisionStudio.Api/Endpoints/RobotEndpoints.cs",
    ]:
        if "EnsureDependencyMutationAllowed" not in ctx.read_safe(rel):
            ctx.fail(f"Production dependency mutation guard missing from {rel}")
    ctx.read_safe("frontend/src/types.ts")
    ctx.read_safe("frontend/src/components/ProductionPanel.tsx")
    ctx.read_safe("frontend/src/components/JobPanel.tsx")
    ctx.ok("V0.25 RuntimeDependencyManifest + publication/trace provenance + production drift guard present")

    maturity_gate = ctx.read_safe("backend/src/VisionStudio.Api/Workflow/NodeMaturityGate.cs")
    maturity_catalog = ctx.read_safe("backend/src/VisionStudio.Engine/NodeMaturityCatalog.cs")
    for token in ["NodeMaturityCatalog.Declarations", "AcknowledgementRequired", "NODE_MATURITY_ACK_REQUIRED"]:
        if token not in maturity_gate:
            ctx.fail(f"Node maturity gate implementation missing: {token}")
    for token in ["NodeMaturityGate.Scan", "acknowledgeMaturityNodes", "AuditContext.SetTarget"]:
        if token not in job_endpoints:
            ctx.fail(f"Job publish path does not enforce node maturity: {token}")
    publish_route = job_endpoints.split("/versions/{version:int}/publish", 1)[-1].split("app.Map", 1)[0]
    if "PublishVersionAsync" not in publish_route:
        ctx.fail("Job publish route does not invoke its shared publication checks")
    rollback_route = job_endpoints.split("/rollback/", 1)[-1].split("app.Map", 1)[0]
    if any(token in rollback_route for token in ("NodeMaturityGate.Scan", "PublishVersionAsync")):
        ctx.fail("Rollback must not be gated by node maturity (the target version was already acknowledged)")
    if "acknowledgeMaturityNodes=true" not in ctx.read_safe("frontend/src/endpoints.ts"):
        ctx.fail("Frontend cannot acknowledge the maturity gate (publish would always fail on Preview nodes)")
    if "NODE_MATURITY_ACK_REQUIRED" not in ctx.read_safe("frontend/src/components/RecipePublicationDialog.tsx"):
        ctx.fail("Publication dialog does not surface the maturity gate as a confirmable step")
    script_maturity = maturity_catalog.split('["script.csharp"]', 1)
    if len(script_maturity) < 2 or "NodeMaturity.Preview" not in script_maturity[1][:200]:
        ctx.fail("script.csharp must stay non-Stable in NodeMaturityCatalog (trusted-code runtime requires publish acknowledgement)")
    script_runner = ctx.read_family("backend/src/VisionStudio.Scripting/CSharpScriptRunner*.cs")
    for token in ["ForbiddenDirective", "ForbiddenApiWalker", "MaxCachedScripts"]:
        if token not in script_runner:
            ctx.fail(f"C# script sandbox guard missing: {token}")
    script_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/ScriptEndpoints.cs")
    if script_endpoints.count("RequireEngineer") < 3:
        ctx.fail("Script editor endpoints must remain engineer-only")
    ctx.ok("Node maturity gate enforced on publish + recorded in audit (rollback exempt)")
    ctx.ok("script.csharp stays a non-Stable trusted-code capability; editor endpoints are engineer-only")

    return {
        "program": program,
        "host_bootstrap": host_bootstrap,
        "api_sources": api_sources,
        "endpoint_sources": endpoint_sources,
        "schema_migration_source": schema_migration_source,
        "current_schema_token": current_schema_token,
        "security_schema": security_schema,
        "plugin_contracts": plugin_contracts,
        "plugin_registry": plugin_registry,
        "plugin_manager": plugin_manager,
        "trace_store_dependency": trace_store_dependency,
        "dependency_source": dependency_source,
        "job_endpoints": job_endpoints,
        "trace_endpoints": trace_endpoints,
        "production_source": production_source,
        "api_smoke": api_smoke,
    }
