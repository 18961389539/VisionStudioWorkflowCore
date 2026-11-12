namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Single source of truth for hand-eye solver parameter ranges.
/// </summary>
/// <remarks>
/// Before this existed the same ranges were enforced three times: inline in each node, again in
/// <c>CalibrationWizardService.ValidateHandEyeSettings</c>, and a third time in the front-end forms.
/// Changing a bound in one place silently left the others accepting values the node would reject.
/// Each validator returns the first error message (or <c>null</c>); callers wrap it in the exception
/// type their layer uses (<see cref="InvalidOperationException"/> in the engine,
/// <c>ApiValidationException</c> in the API).
/// </remarks>
public static class HandEyeSettings
{
    public static string? ValidatePlanar(
        string mode,
        double angularWeight,
        double visionScale,
        string robustLoss,
        double maxTranslationError,
        double maxAngleErrorDeg,
        double maxConditionNumber)
    {
        if (!IsMode(mode)) return "mode must be eyeInHand or eyeToHand.";
        if (!double.IsFinite(angularWeight) || angularWeight is < 0.001 or > 1e6)
            return "angularWeight must be finite and between 0.001 and 1000000 translation units per radian.";
        if (!double.IsFinite(visionScale) || visionScale is < 1e-6 or > 1e6)
            return "visionScale must be finite and between 0.000001 and 1000000 target units per vision source unit.";
        if (!string.Equals(robustLoss, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(robustLoss, "tukey", StringComparison.OrdinalIgnoreCase))
            return "robustLoss must be none or tukey.";
        if (!double.IsFinite(maxTranslationError) || maxTranslationError is < 0 or > 1e6)
            return "maxTranslationError must be finite and between 0 and 1000000 (0 disables).";
        if (!double.IsFinite(maxAngleErrorDeg) || maxAngleErrorDeg is < 0 or > 180)
            return "maxAngleErrorDeg must be finite and between 0 and 180 (0 disables).";
        if (!double.IsFinite(maxConditionNumber) || maxConditionNumber is < 0 or > 1e12)
            return "maxConditionNumber must be finite and between 0 and 1e12 (0 disables).";
        return null;
    }

    public static string? ValidateSpatial(
        string mode,
        string method,
        double rotationWeight,
        string outlierRejection,
        string crossCheckMethod,
        double maxTranslationError,
        double maxRotationErrorDeg,
        double minTranslationSpan,
        int maxRejections,
        double minRmsImprovement)
    {
        if (!IsMode(mode)) return "mode must be eyeInHand or eyeToHand.";
        if (!IsMethod(method, allowNone: false)) return "method must be TSAI, PARK, HORAUD, ANDREFF, or DANIILIDIS.";
        if (!double.IsFinite(rotationWeight) || rotationWeight is < 0.001 or > 1e6)
            return "rotationWeight must be finite and between 0.001 and 1000000 translation units per radian.";
        if (!string.Equals(outlierRejection, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(outlierRejection, "iterative", StringComparison.OrdinalIgnoreCase))
            return "outlierRejection must be none or iterative.";
        if (!IsMethod(crossCheckMethod, allowNone: true)) return "crossCheckMethod must be NONE, TSAI, PARK, HORAUD, ANDREFF, or DANIILIDIS.";
        if (!double.IsFinite(maxTranslationError) || maxTranslationError is < 0 or > 1e6)
            return "maxTranslationError must be finite and between 0 and 1000000 (0 disables).";
        if (!double.IsFinite(maxRotationErrorDeg) || maxRotationErrorDeg is < 0 or > 180)
            return "maxRotationErrorDeg must be finite and between 0 and 180 (0 disables).";
        if (!double.IsFinite(minTranslationSpan) || minTranslationSpan is < 0 or > 1e9)
            return "minTranslationSpan must be finite and between 0 and 1e9 (0 disables).";
        if (maxRejections is < 0 or > 1000) return "maxRejections must be between 0 and 1000.";
        if (!double.IsFinite(minRmsImprovement) || minRmsImprovement is < 0.05 or > 1)
            return "minRmsImprovement must be finite and between 0.05 and 1 (1 accepts only strict improvements).";
        return null;
    }

    private static bool IsMode(string mode)
        => string.Equals(mode, "eyeInHand", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "eyeToHand", StringComparison.OrdinalIgnoreCase);

    private static bool IsMethod(string method, bool allowNone)
    {
        var value = (method ?? string.Empty).Trim().ToUpperInvariant();
        if (allowNone && value == "NONE") return true;
        return value is "TSAI" or "PARK" or "HORAUD" or "ANDREFF" or "DANIILIDIS";
    }
}
