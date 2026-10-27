namespace VisionStudio.Api.Endpoints;

public static class EndpointMapping
{
    public static IEndpointRouteBuilder MapVisionStudioApi(this IEndpointRouteBuilder app)
    {
        app.MapSecurityEndpoints();
        app.MapSystemEndpoints();
        app.MapPluginEndpoints();
        app.MapPluginWorkerEndpoints();
        app.MapPluginBenchmarkEndpoints();
        app.MapPluginPackageEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapCalibrationEndpoints();
        app.MapMatchingEndpoints();
        app.MapCameraEndpoints();
        app.MapCameraFeatureProfileEndpoints();
        app.MapCameraSynchronizationEndpoints();
        app.MapMediaEndpoints();
        app.MapHardwareProvenanceEndpoints();
        app.MapDeviceEndpoints();
        app.MapRobotEndpoints();
        app.MapWorkflowExecutionEndpoints();
        app.MapDebugSessionEndpoints();
        app.MapJobEndpoints();
        app.MapProductRecipeEndpoints();
        app.MapRecipeParameterEndpoints();
        app.MapProductionEndpoints();
        app.MapTraceEndpoints();
        app.MapInvestigationEndpoints();
        app.MapReplayEndpoints();
        app.MapDatasetValidationEndpoints();
        app.MapModelPackageEndpoints();
        app.MapParameterTuningEndpoints();
        app.MapWorkflowModuleEndpoints();
        app.MapWorkflowStoreEndpoints();
        app.MapScriptEndpoints();
        return app;
    }
}
