using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>Analyzes bounded ROI intensity profiles and proposes safe detector settings on a clone.</summary>
internal static class DetectionParameterRecommendationService
{
    private const int MaximumCropPixels = 4_000_000;
    private const int MaximumProfileSamples = 16_384;
    private const int MaximumProfileCount = 96;
    private const int MaximumSearchRange = 1024;

    private readonly record struct SamplePoint(double X, double Y);
    private readonly record struct ProfileSpec(SamplePoint Center, SamplePoint Direction, SamplePoint Across);
    private readonly record struct Peak(double Offset, double Gradient, int Index);
    private sealed record ProfileStatistics(double Contrast, double Noise, double PeakGradient, double PeakOffset,
        double PeakSignedGradient, Peak[] Peaks);

    /// <summary>Returns whether this service can inspect the ROI type.</summary>
    /// <param name="roi">ROI to inspect.</param>
    /// <returns>True for supported caliper and blob ROIs.</returns>
    internal static bool IsSupported(RoiBase roi) => roi is ISingleEdgeCaliperRoi or CaliperMeasureRoi or BlobAnalysisRoi;

    /// <summary>Builds a recommendation from actual pixels without changing the supplied ROI.</summary>
    /// <param name="bitmap">Source image.</param>
    /// <param name="roi">ROI whose parameters should be reviewed.</param>
    /// <returns>A localized, reviewable proposal with a suggested deep clone on success.</returns>
    internal static DetectionParameterProposal Recommend(BitmapSource bitmap, RoiBase roi)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(roi);

        if (!IsSupported(roi))
        {
            return Failure("RecommendationUnsupportedRoi");
        }

        if (roi is BlobAnalysisRoi blob)
        {
            return RecommendBlob(bitmap, blob);
        }

        return RecommendCaliper(bitmap, roi);
    }

    private static DetectionParameterProposal RecommendCaliper(BitmapSource bitmap, RoiBase roi)
    {
        if (!TryCreateCaliperProfiles(roi, out ProfileSpec[] specs, out int searchRange, out int halfWidth,
                out int currentCount, out int countMinimum, out int countMaximum, out int minimumValid,
                out bool dualEdge, out string geometryFailure))
        {
            return Failure(geometryFailure);
        }

        if (searchRange > MaximumSearchRange || halfWidth > 64)
        {
            return Failure("RecommendationInsufficientSupport");
        }

        int stepsPerProfile = checked((searchRange * 2 + 1) * Math.Max(1, halfWidth * 2 + 1));
        int maxProfiles = MaximumProfileSamples / stepsPerProfile;
        int sampleProfiles = Math.Min(Math.Min(specs.Length, MaximumProfileCount), maxProfiles);
        int requiredProfiles = dualEdge ? 3 : 3;
        if (sampleProfiles < requiredProfiles)
        {
            return Failure("RecommendationInsufficientSupport");
        }

        ProfileSpec[] sampledSpecs = SelectEvenly(specs, sampleProfiles);
        if (!TryReadProfiles(bitmap, sampledSpecs, searchRange, halfWidth, out ProfileStatistics[] profiles,
                out int sampleCount, out string readFailure))
        {
            return Failure(readFailure);
        }

        double contrast = Median(profiles.Select(profile => profile.Contrast));
        double noise = Median(profiles.Select(profile => profile.Noise));
        double medianPeak = Median(profiles.Select(profile => profile.PeakGradient));
        double polarityRatio = DominantPolarityRatio(profiles, out double dominantSignedGradient);
        int fullScale = GetFullScale(bitmap);
        double minimumContrast = Math.Max(2, fullScale * 0.002);
        if (!double.IsFinite(contrast) || contrast < minimumContrast || !double.IsFinite(medianPeak) ||
            medianPeak < Math.Max(fullScale * 0.001, noise * 3.0))
        {
            return Failure("RecommendationLowContrast", new DetectionParameterEvidence(contrast, noise, sampleCount,
                polarityRatio, ["RecommendationNoteStableEdge"]));
        }

        int requiredEdgeProfiles = Math.Max(3, (int)Math.Ceiling(sampleProfiles * 0.6));
        int supported = profiles.Count(profile => profile.Peaks.Length >= (dualEdge ? 2 : 1) &&
            profile.PeakGradient >= Math.Max(fullScale * 0.001, noise * 3.0));
        if (supported < requiredEdgeProfiles)
        {
            return Failure("RecommendationInsufficientSupport", new DetectionParameterEvidence(contrast, noise,
                sampleCount, polarityRatio, ["RecommendationNoteStrongEdgeCoverage"]));
        }

        if (profiles.Any(profile => profile.PeakOffset <= -searchRange + 1 || profile.PeakOffset >= searchRange - 1))
        {
            return Failure("RecommendationInsufficientSupport", new DetectionParameterEvidence(contrast, noise,
                sampleCount, polarityRatio, ["RecommendationNoteSearchBoundary"]));
        }

        RoiBase candidate = roi.Clone();
        var changes = new List<DetectionParameterChange>();
        List<double> edgeOffsets = profiles.SelectMany(profile => profile.Peaks.Select(peak => Math.Abs(peak.Offset))).ToList();
        double offset90 = Percentile(edgeOffsets, 0.90);
        // The native Gaussian derivative needs support beyond the edge itself. A two-pixel
        // window can truncate that kernel even when the intensity peak lies at the center.
        int kernelSupport = Math.Max(6, (int)Math.Ceiling(GetSigma(roi) * 4 + 2));
        int recommendedSearch = Math.Clamp((int)Math.Ceiling(offset90 + Math.Max(2, Math.Min(4, noise * 0.2))),
            Math.Min(kernelSupport, searchRange), searchRange);
        if (recommendedSearch < searchRange - 1)
        {
            SetSearchRange(candidate, recommendedSearch);
            AddChange(changes, "CaliperSearchRange", "EditorSearchRange", searchRange, recommendedSearch, "RecommendationReasonSearchRange");
        }

        int recommendedCount = RecommendCount(roi, currentCount, countMinimum, countMaximum);
        if (recommendedCount > currentCount)
        {
            SetCaliperCount(candidate, recommendedCount);
            AddChange(changes, "CaliperCount", "EditorCaliperCount", currentCount, recommendedCount, "RecommendationReasonSampleCount");

            int recommendedMinimumValid = Math.Clamp(
                (int)Math.Ceiling((double)minimumValid / Math.Max(1, currentCount) * recommendedCount),
                minimumValid,
                recommendedCount);
            if (recommendedMinimumValid > minimumValid)
            {
                SetMinimumValid(candidate, recommendedMinimumValid);
                AddChange(changes, "MinimumValidCalipers", "EditorMinimumValidCalipers", minimumValid,
                    recommendedMinimumValid, "RecommendationReasonSampleCount");
            }
        }

        double contrastRatio = noise / Math.Max(contrast, 1);
        int recommendedHalfWidth = Math.Clamp((int)Math.Ceiling(1 + contrastRatio * 8), 0, 5);
        if (recommendedHalfWidth != halfWidth)
        {
            SetSamplingHalfWidth(candidate, recommendedHalfWidth);
            AddChange(changes, "CaliperSamplingHalfWidth", "EditorSamplingHalfWidth", halfWidth,
                recommendedHalfWidth, "RecommendationReasonAveraging");
        }

        double recommendedSigma = Math.Clamp(1.0 + 2.2 * contrastRatio, 0.5, 3.0);
        double currentSigma = GetSigma(roi);
        if (Math.Abs(recommendedSigma - currentSigma) >= 0.15)
        {
            SetSigma(candidate, recommendedSigma);
            AddChange(changes, "CaliperEdgeSigma", "EditorEdgeSigma", currentSigma, recommendedSigma,
                "RecommendationReasonSigma");
        }

        double recommendedMinimumGradient = Math.Max(GetMinimumGradient(roi),
            Math.Max(noise * 3.0, Percentile(profiles.Select(profile => profile.PeakGradient), 0.25) * 0.12));
        if (recommendedMinimumGradient > GetMinimumGradient(roi) * 1.1 + 0.25)
        {
            SetMinimumGradient(candidate, recommendedMinimumGradient);
            AddChange(changes, "CaliperMinimumGradient", "EditorMinimumGradient", GetMinimumGradient(roi),
                recommendedMinimumGradient, "RecommendationReasonMinimumGradient");
        }

        if (!dualEdge && polarityRatio >= 0.85 && TryInferPolarity(dominantSignedGradient, out CaliperEdgePolarity inferred) &&
            GetPolarity(roi) != inferred)
        {
            SetPolarity(candidate, inferred);
            AddChange(changes, "CaliperEdgePolarity", "EditorEdgePolarity", GetPolarity(roi), inferred,
                "RecommendationReasonPolarity");
        }

        string[] notes =
        [
            fullScale == ushort.MaxValue ? "RecommendationNoteGray16Native" : "RecommendationNoteByteIntensity",
            dualEdge ? "RecommendationNoteDualAnyPolarity" :
                polarityRatio >= 0.85 ? "RecommendationNoteConsistentPolarity" : "RecommendationNoteMixedPolarity"
        ];
        return new DetectionParameterProposal(true, string.Empty, candidate, [.. changes],
            new DetectionParameterEvidence(contrast, noise, sampleCount, polarityRatio, notes));
    }

    private static DetectionParameterProposal RecommendBlob(BitmapSource bitmap, BlobAnalysisRoi roi)
    {
        if (!IsFinite(roi.Center.X) || !IsFinite(roi.Center.Y) || !IsFinite(roi.Width) || !IsFinite(roi.Height) ||
            !IsFinite(roi.Angle) || roi.Width <= 2 || roi.Height <= 2)
        {
            return Failure("RecommendationInvalidGeometry");
        }

        BitmapSource source = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
        int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(source);
        double halfExtentX = GetRotatedHalfExtentX(roi);
        double halfExtentY = GetRotatedHalfExtentY(roi);
        double minX = roi.Center.X - halfExtentX, maxX = roi.Center.X + halfExtentX;
        double minY = roi.Center.Y - halfExtentY, maxY = roi.Center.Y + halfExtentY;
        if (!IsFinite(minX) || !IsFinite(maxX) || !IsFinite(minY) || !IsFinite(maxY))
        {
            return Failure("RecommendationInvalidGeometry");
        }

        if (minX < 0 || minY < 0 || maxX >= source.PixelWidth || maxY >= source.PixelHeight)
        {
            return Failure("RecommendationClippedRoi");
        }

        // Bounds are checked against bitmap dimensions as doubles before conversion, so even
        // extreme but finite geometry cannot wrap during a floating point to integer cast.
        int left = (int)Math.Floor(minX);
        int top = (int)Math.Floor(minY);
        int right = (int)Math.Ceiling(maxX);
        int bottom = (int)Math.Ceiling(maxY);

        int width = right - left + 1;
        int height = bottom - top + 1;
        long cropPixels = (long)width * height;
        if (width <= 0 || height <= 0 || cropPixels > MaximumCropPixels)
        {
            return Failure("RecommendationCropTooLarge");
        }

        int stride = checked(width * bytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        int maxIntensity = ImageViewerPixelAccess.GetIntensityMaximum(source.Format);
        var histogram = new int[maxIntensity + 1];
        int targetColumns = Math.Clamp(
            (int)Math.Floor(Math.Sqrt((double)MaximumProfileSamples * width / height)), 1, width);
        int targetRows = Math.Clamp(MaximumProfileSamples / targetColumns, 1, height);
        double radians = roi.Angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        long totalSamples = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0, sumSquared = 0;
        for (int sampleRow = 0; sampleRow < targetRows; sampleRow++)
        for (int sampleColumn = 0; sampleColumn < targetColumns; sampleColumn++)
        {
            int y = Math.Min(height - 1, (int)Math.Floor((sampleRow + 0.5) * height / targetRows));
            int x = Math.Min(width - 1, (int)Math.Floor((sampleColumn + 0.5) * width / targetColumns));
            double dx = left + x + 0.5 - roi.Center.X;
            double dy = top + y + 0.5 - roi.Center.Y;
            double localX = dx * cos + dy * sin;
            double localY = -dx * sin + dy * cos;
            if (Math.Abs(localX) > roi.Width / 2 || Math.Abs(localY) > roi.Height / 2)
            {
                continue;
            }

            ushort intensity = ImageViewerPixelAccess.ReadIntensity(pixels, y * stride + x * bytesPerPixel, bytesPerPixel, source.Format);
            histogram[intensity]++;
            min = Math.Min(min, intensity);
            max = Math.Max(max, intensity);
            sum += intensity;
            sumSquared += (double)intensity * intensity;
            totalSamples++;
        }

        if (totalSamples < 16 || !double.IsFinite(min) || max - min < Math.Max(2, maxIntensity * 0.002))
        {
            return Failure("RecommendationLowContrast", new DetectionParameterEvidence(max - min, 0,
                checked((int)totalSamples), 0, ["RecommendationNoteBlobSeparationInsufficient"]));
        }

        int otsu = Otsu(histogram, out double separability);
        int darkCount = histogram.Take(otsu + 1).Sum();
        long lightCount = totalSamples - darkCount;
        double darkFraction = (double)darkCount / totalSamples;
        double lightFraction = (double)lightCount / totalSamples;
        double contrast = ClassMeanDifference(histogram, otsu, darkCount, lightCount);
        double mean = sum / totalSamples;
        double variance = Math.Max(0, sumSquared / totalSamples - mean * mean);
        double noise = Math.Sqrt(variance * Math.Max(0, 1 - separability));
        if (separability < 0.55 || Math.Min(darkFraction, lightFraction) < 0.02 || contrast < maxIntensity * 0.01)
        {
            return Failure("RecommendationLowContrast", new DetectionParameterEvidence(contrast, noise,
                checked((int)totalSamples), 0, ["RecommendationNoteOtsuNotStrong"]));
        }

        RoiBase suggested = roi.Clone();
        var changes = new List<DetectionParameterChange>();
        if (!roi.UseOtsu)
        {
            ((BlobAnalysisRoi)suggested).UseOtsu = true;
            AddChange(changes, "UseOtsu", "EditorUseOtsu", false, true, "RecommendationReasonOtsu");
        }

        // DetectDark is intentionally retained: intensity histograms alone do not establish which class
        // represents the object of interest in the operator's inspection task.
        return new DetectionParameterProposal(true, string.Empty, suggested, [.. changes],
            new DetectionParameterEvidence(contrast, noise, checked((int)totalSamples), 0,
            ["RecommendationNoteOtsuStrong", "RecommendationNoteDetectDarkRetained"]));
    }

    private static bool TryCreateCaliperProfiles(RoiBase roi, out ProfileSpec[] profiles, out int searchRange,
        out int halfWidth, out int count, out int countMinimum, out int countMaximum, out int minimumValid,
        out bool dualEdge, out string failureKey)
    {
        profiles = [];
        searchRange = 0;
        halfWidth = 0;
        count = 0;
        countMinimum = 0;
        countMaximum = 0;
        minimumValid = 0;
        dualEdge = false;
        failureKey = "RecommendationInvalidGeometry";

        if (roi is LineCaliperMeasureRoi lineCaliper)
        {
            SamplePoint start = ToSample(lineCaliper.P1), end = ToSample(lineCaliper.P2);
            SamplePoint axis = Normalize(Subtract(end, start));
            double length = Distance(start, end);
            if (!Valid(start) || !Valid(end) || !IsFinite(length) || length <= 2 || !Valid(axis)) return false;
            searchRange = lineCaliper.CaliperSearchRange;
            halfWidth = lineCaliper.CaliperSamplingHalfWidth;
            count = Math.Clamp(lineCaliper.CaliperCount, 6, 180);
            countMinimum = 6;
            countMaximum = 180;
            minimumValid = Math.Clamp(lineCaliper.MinimumValidCalipers, 3, count);
            profiles = MakeProfiles(start, end, count, new SamplePoint(-axis.Y, axis.X), axis);
            return true;
        }

        if (roi is CaliperMeasureRoi dual)
        {
            SamplePoint p1 = ToSample(dual.P1), p2 = ToSample(dual.P2);
            SamplePoint direction;
            SamplePoint center;
            double regionLength;
            if (dual.HasExplicitCaliperRegion)
            {
                direction = DirectionFromAngle(dual.CaliperAngleDegrees);
                center = ToSample(dual.CaliperCenter);
                regionLength = dual.GetResolvedCaliperRegionLength();
            }
            else
            {
                direction = Normalize(Subtract(p2, p1));
                center = Scale(Add(p1, p2), 0.5);
                regionLength = Math.Max(10, Math.Min(48, Distance(p1, p2) * 0.1));
            }

            if (!Valid(p1) || !Valid(p2) || !Valid(direction) || !Valid(center) || !IsFinite(regionLength) || regionLength <= 2)
            {
                return false;
            }

            SamplePoint across = new(-direction.Y, direction.X);
            searchRange = dual.CaliperSearchRange;
            halfWidth = dual.CaliperSamplingHalfWidth;
            count = Math.Clamp(dual.CaliperCount, 3, 31);
            countMinimum = 3;
            countMaximum = 31;
            minimumValid = Math.Clamp(dual.MinimumValidCalipers, 2, count);
            profiles = MakeCenteredProfiles(center, across, direction, regionLength, count);
            dualEdge = true;
            return true;
        }

        if (roi is CircularCaliperMeasureRoi circle)
        {
            SamplePoint center = ToSample(circle.Center);
            double radius = circle.Radius;
            if (!Valid(center) || !IsFinite(radius) || radius <= 2) return false;
            searchRange = circle.CaliperSearchRange;
            halfWidth = circle.CaliperSamplingHalfWidth;
            count = Math.Clamp(circle.CaliperCount, 6, 180);
            countMinimum = 6;
            countMaximum = 180;
            minimumValid = Math.Clamp(circle.MinimumValidCalipers, 3, count);
            double startAngle = roi is ArcCaliperMeasureRoi arc ? arc.StartAngle : 0;
            double sweep = roi is ArcCaliperMeasureRoi arcRoi ? arcRoi.SweepAngle : 360;
            if (!IsFinite(startAngle) || !IsFinite(sweep) || Math.Abs(sweep) < 1) return false;
            profiles = new ProfileSpec[count];
            for (int i = 0; i < count; i++)
            {
                double fraction = roi is ArcCaliperMeasureRoi ? (double)i / (count - 1) : (double)i / count;
                double angle = (startAngle + sweep * fraction) * Math.PI / 180;
                SamplePoint radial = new(Math.Cos(angle), Math.Sin(angle));
                profiles[i] = new ProfileSpec(Add(center, Scale(radial, radius)), radial, new SamplePoint(-radial.Y, radial.X));
            }

            return true;
        }

        failureKey = "RecommendationUnsupportedRoi";
        return false;
    }

    private static bool TryReadProfiles(BitmapSource bitmap, ProfileSpec[] specs, int searchRange, int halfWidth,
        out ProfileStatistics[] statistics, out int sampleCount, out string failureKey)
    {
        statistics = [];
        sampleCount = 0;
        failureKey = "RecommendationInvalidGeometry";
        BitmapSource source = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
        int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(source);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (ProfileSpec spec in specs)
        {
            for (int end = -1; end <= 1; end += 2)
            for (int across = -halfWidth; across <= halfWidth; across++)
            {
                SamplePoint point = Add(Add(spec.Center, Scale(spec.Direction, searchRange * end)), Scale(spec.Across, across));
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            }
        }

        int left = (int)Math.Floor(minX) - 1, top = (int)Math.Floor(minY) - 1;
        int right = (int)Math.Ceiling(maxX) + 1, bottom = (int)Math.Ceiling(maxY) + 1;
        if (left < 0 || top < 0 || right >= source.PixelWidth || bottom >= source.PixelHeight)
        {
            failureKey = "RecommendationClippedRoi";
            return false;
        }

        int width = right - left + 1, height = bottom - top + 1;
        long cropPixels = (long)width * height;
        if (width <= 0 || height <= 0 || cropPixels > MaximumCropPixels)
        {
            failureKey = "RecommendationCropTooLarge";
            return false;
        }

        int stride = checked(width * bytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        int acrossCount = checked(2 * halfWidth + 1);
        int profileLength = checked(searchRange * 2 + 1);
        int maxOperations = MaximumProfileSamples;
        double[] values = new double[profileLength];
        var result = new List<ProfileStatistics>(specs.Length);
        foreach (ProfileSpec spec in specs)
        {
            Array.Clear(values);
            for (int across = -halfWidth; across <= halfWidth; across++)
            {
                SamplePoint lateral = Scale(spec.Across, across);
                for (int i = 0; i < profileLength; i++)
                {
                    double offset = i - searchRange;
                    SamplePoint point = Add(Add(spec.Center, Scale(spec.Direction, offset)), lateral);
                    values[i] += ImageViewerPixelAccess.ReadBilinearIntensity(pixels, stride, width, height,
                        bytesPerPixel, source.Format, point.X - left, point.Y - top) / acrossCount;
                }
            }

            result.Add(AnalyzeProfile(values, searchRange));
            sampleCount += profileLength * acrossCount;
            if (sampleCount + profileLength * acrossCount > maxOperations)
            {
                break;
            }
        }

        if (result.Count < 3 || sampleCount > MaximumProfileSamples)
        {
            failureKey = "RecommendationInsufficientSupport";
            return false;
        }

        statistics = [.. result];
        return true;
    }

    private static ProfileStatistics AnalyzeProfile(double[] values, int searchRange)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        double contrast = Percentile(sorted, 0.9) - Percentile(sorted, 0.1);
        var gradients = new double[values.Length - 1];
        int peakIndex = 0;
        for (int i = 0; i < gradients.Length; i++)
        {
            gradients[i] = values[i + 1] - values[i];
            if (Math.Abs(gradients[i]) > Math.Abs(gradients[peakIndex])) peakIndex = i;
        }

        double peakMagnitude = Math.Abs(gradients[peakIndex]);
        double[] noiseSamples = gradients.Where((_, index) => Math.Abs(index - peakIndex) > 2)
            .Select(Math.Abs).ToArray();
        double noise = noiseSamples.Length == 0 ? 0 : Median(noiseSamples);
        Peak[] localPeaks = Enumerable.Range(1, Math.Max(0, gradients.Length - 2))
            .Where(index => Math.Abs(gradients[index]) >= Math.Abs(gradients[index - 1]) &&
                            Math.Abs(gradients[index]) > Math.Abs(gradients[index + 1]))
            .OrderByDescending(index => Math.Abs(gradients[index]))
            .Take(8)
            .Where(index => Math.Abs(gradients[index]) >= Math.Max(noise * 3, peakMagnitude * 0.10))
            .Select(index => new Peak(index + 0.5 - searchRange, gradients[index], index))
            .ToArray();
        if (localPeaks.Length == 0)
        {
            localPeaks = [new Peak(peakIndex + 0.5 - searchRange, gradients[peakIndex], peakIndex)];
        }

        return new ProfileStatistics(contrast, noise, peakMagnitude, peakIndex + 0.5 - searchRange,
            gradients[peakIndex], localPeaks);
    }

    private static ProfileSpec[] MakeProfiles(SamplePoint start, SamplePoint end, int count, SamplePoint direction, SamplePoint across)
    {
        var profiles = new ProfileSpec[count];
        for (int i = 0; i < count; i++)
        {
            double t = count <= 1 ? 0.5 : (double)i / (count - 1);
            profiles[i] = new ProfileSpec(Add(start, Scale(Subtract(end, start), t)), direction, across);
        }

        return profiles;
    }

    private static ProfileSpec[] MakeCenteredProfiles(SamplePoint center, SamplePoint across, SamplePoint direction, double length, int count)
    {
        var profiles = new ProfileSpec[count];
        for (int i = 0; i < count; i++)
        {
            double t = count <= 1 ? 0.5 : (double)i / (count - 1);
            profiles[i] = new ProfileSpec(Add(center, Scale(across, (t - 0.5) * length)), direction, across);
        }

        return profiles;
    }

    private static ProfileSpec[] SelectEvenly(ProfileSpec[] profiles, int count)
    {
        if (count >= profiles.Length) return profiles;
        var result = new ProfileSpec[count];
        for (int i = 0; i < count; i++)
        {
            int index = count == 1 ? profiles.Length / 2 : (int)Math.Round((double)i * (profiles.Length - 1) / (count - 1));
            result[i] = profiles[index];
        }

        return result;
    }

    private static DetectionParameterProposal Failure(string key, DetectionParameterEvidence? evidence = null) =>
        new(false, key, null, [], evidence ?? new DetectionParameterEvidence(0, 0, 0, 0, []));

    private static void AddChange(List<DetectionParameterChange> changes, string property, string nameKey,
        object before, object after, string reasonKey)
    {
        if (Equals(before, after)) return;
        changes.Add(new DetectionParameterChange(property, nameKey, Format(before), Format(after), reasonKey));
    }

    private static string Format(object value) => value switch
    {
        double number => number.ToString("G4", CultureInfo.InvariantCulture),
        float number => number.ToString("G4", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static int RecommendCount(RoiBase roi, int current, int minimum, int maximum)
    {
        double length = roi switch
        {
            LineCaliperMeasureRoi line => Distance(ToSample(line.P1), ToSample(line.P2)),
            CaliperMeasureRoi line => line.HasExplicitCaliperRegion ? line.GetResolvedCaliperRegionLength() : Distance(ToSample(line.P1), ToSample(line.P2)),
            CircularCaliperMeasureRoi circle when roi is ArcCaliperMeasureRoi arc =>
                circle.Radius * Math.PI * Math.Abs(arc.SweepAngle) / 180,
            CircularCaliperMeasureRoi circle => 2 * Math.PI * circle.Radius,
            _ => current * 4.0
        };
        int target = (int)Math.Ceiling(length / 4.0) + 1;
        return Math.Clamp(Math.Max(current, target), minimum, maximum);
    }

    private static void SetCaliperCount(RoiBase roi, int value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperCount = value; break;
            case CaliperMeasureRoi dual: dual.CaliperCount = value; break;
        }
    }

    private static void SetSearchRange(RoiBase roi, int value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperSearchRange = value; break;
            case CaliperMeasureRoi dual: dual.CaliperSearchRange = value; break;
        }
    }

    private static void SetSamplingHalfWidth(RoiBase roi, int value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperSamplingHalfWidth = value; break;
            case CaliperMeasureRoi dual: dual.CaliperSamplingHalfWidth = value; break;
        }
    }

    private static void SetSigma(RoiBase roi, double value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperEdgeSigma = value; break;
            case CaliperMeasureRoi dual: dual.CaliperEdgeSigma = value; break;
        }
    }

    private static void SetMinimumGradient(RoiBase roi, double value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperMinimumGradient = value; break;
            case CaliperMeasureRoi dual: dual.CaliperMinimumGradient = value; break;
        }
    }

    private static void SetPolarity(RoiBase roi, CaliperEdgePolarity value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.CaliperEdgePolarity = value; break;
            case CaliperMeasureRoi dual: dual.CaliperEdgePolarity = value; break;
        }
    }

    private static void SetMinimumValid(RoiBase roi, int value)
    {
        switch (roi)
        {
            case ISingleEdgeCaliperRoi single: single.MinimumValidCalipers = value; break;
            case CaliperMeasureRoi dual: dual.MinimumValidCalipers = value; break;
        }
    }

    private static double GetMinimumGradient(RoiBase roi) => roi switch
    {
        ISingleEdgeCaliperRoi single => single.CaliperMinimumGradient,
        CaliperMeasureRoi dual => dual.CaliperMinimumGradient,
        _ => 0
    };

    private static double GetSigma(RoiBase roi) => roi switch
    {
        ISingleEdgeCaliperRoi single => single.CaliperEdgeSigma,
        CaliperMeasureRoi dual => dual.CaliperEdgeSigma,
        _ => 1
    };

    private static CaliperEdgePolarity GetPolarity(RoiBase roi) => roi switch
    {
        ISingleEdgeCaliperRoi single => single.CaliperEdgePolarity,
        CaliperMeasureRoi dual => dual.CaliperEdgePolarity,
        _ => CaliperEdgePolarity.Any
    };

    private static double GetRotatedHalfExtentX(RotatedRect roi)
    {
        double angle = roi.Angle * Math.PI / 180;
        return Math.Abs(Math.Cos(angle)) * roi.Width / 2 + Math.Abs(Math.Sin(angle)) * roi.Height / 2;
    }

    private static double GetRotatedHalfExtentY(RotatedRect roi)
    {
        double angle = roi.Angle * Math.PI / 180;
        return Math.Abs(Math.Sin(angle)) * roi.Width / 2 + Math.Abs(Math.Cos(angle)) * roi.Height / 2;
    }

    private static int Otsu(int[] histogram, out double separability)
    {
        long total = histogram.Sum(value => (long)value);
        double sum = 0;
        for (int i = 0; i < histogram.Length; i++) sum += (double)i * histogram[i];
        long backgroundCount = 0;
        double backgroundSum = 0, maximumBetween = -1;
        int threshold = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            backgroundCount += histogram[i];
            backgroundSum += (double)i * histogram[i];
            long foregroundCount = total - backgroundCount;
            if (backgroundCount == 0 || foregroundCount == 0) continue;
            double meanBackground = backgroundSum / backgroundCount;
            double meanForeground = (sum - backgroundSum) / foregroundCount;
            double between = (double)backgroundCount * foregroundCount * Math.Pow(meanBackground - meanForeground, 2);
            if (between > maximumBetween) { maximumBetween = between; threshold = i; }
        }

        double mean = sum / Math.Max(total, 1);
        double totalVariance = 0;
        for (int i = 0; i < histogram.Length; i++) totalVariance += histogram[i] * Math.Pow(i - mean, 2);
        separability = totalVariance <= 0 ? 0 : maximumBetween / (totalVariance * Math.Max(total, 1));
        return threshold;
    }

    private static double ClassMeanDifference(int[] histogram, int threshold, long lowerCount, long upperCount)
    {
        if (lowerCount <= 0 || upperCount <= 0) return 0;
        double lowerSum = 0, upperSum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (i <= threshold) lowerSum += (double)i * histogram[i];
            else upperSum += (double)i * histogram[i];
        }

        return Math.Abs(upperSum / upperCount - lowerSum / lowerCount);
    }

    private static int GetFullScale(BitmapSource bitmap) => ImageViewerPixelAccess.GetIntensityMaximum(ImageViewerPixelAccess.NormalizeForIntensity(bitmap).Format);

    private static bool TryInferPolarity(double signedGradient, out CaliperEdgePolarity polarity)
    {
        polarity = signedGradient >= 0 ? CaliperEdgePolarity.DarkToLight : CaliperEdgePolarity.LightToDark;
        return IsFinite(signedGradient) && Math.Abs(signedGradient) > 0;
    }

    private static double DominantPolarityRatio(IEnumerable<ProfileStatistics> profiles, out double signedGradient)
    {
        double positive = 0, negative = 0;
        foreach (ProfileStatistics profile in profiles)
        {
            if (profile.PeakSignedGradient >= 0) positive++;
            else negative++;
        }

        signedGradient = positive >= negative ? 1 : -1;
        return (positive + negative) == 0 ? 0 : Math.Max(positive, negative) / (positive + negative);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Where(IsFinite).Order().ToArray();
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Where(IsFinite).Order().ToArray();
        if (sorted.Length == 0) return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static SamplePoint ToSample(PointD point) => new(point.X, point.Y);
    private static SamplePoint DirectionFromAngle(double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new SamplePoint(Math.Cos(radians), Math.Sin(radians));
    }
    private static SamplePoint Add(SamplePoint a, SamplePoint b) => new(a.X + b.X, a.Y + b.Y);
    private static SamplePoint Subtract(SamplePoint a, SamplePoint b) => new(a.X - b.X, a.Y - b.Y);
    private static SamplePoint Scale(SamplePoint value, double factor) => new(value.X * factor, value.Y * factor);
    private static double Distance(SamplePoint a, SamplePoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static SamplePoint Normalize(SamplePoint value)
    {
        double length = Math.Sqrt(value.X * value.X + value.Y * value.Y);
        return length <= 1e-9 || !IsFinite(length) ? new SamplePoint(double.NaN, double.NaN) : new SamplePoint(value.X / length, value.Y / length);
    }
    private static bool Valid(SamplePoint point) => IsFinite(point.X) && IsFinite(point.Y);
    private static bool IsFinite(double value) => double.IsFinite(value);
}
