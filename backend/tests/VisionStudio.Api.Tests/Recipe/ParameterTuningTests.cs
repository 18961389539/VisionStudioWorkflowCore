namespace VisionStudio.Api.Tests;

public sealed class ParameterTuningTests
{
    [Fact]
    public void V049Classification_UsesIndustrialFalseOkFalseNgSemantics()
    {
        Assert.Equal("FALSE_OK", ParameterTuningService.Classify("NG", "OK"));
        Assert.Equal("FALSE_NG", ParameterTuningService.Classify("OK", "NG"));
        Assert.Equal("TRUE_OK", ParameterTuningService.Classify("OK", "OK"));
        Assert.Equal("ERROR", ParameterTuningService.Classify("NG", "ERROR"));
    }
}
