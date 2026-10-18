using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Algorithms;

internal sealed record FisheyeViewError(double Rms, double Max);
internal sealed record FisheyeCalibrationResult(CameraCalibrationModel Model, IReadOnlyList<FisheyeViewError> ViewErrors);

internal static class JLVisionCalibration
{
    public static CameraCalibrationModel Calibrate(CameraCalibrationRequest request, CancellationToken token, string cameraType = "area_scan_polynomial", double telecentricMagnification = 0.2, double telecentricPixelSizeUm = 3.45)
    {
        var isTelecentric = cameraType == "area_scan_telecentric_polynomial";
        if (cameraType is not ("area_scan_polynomial" or "area_scan_telecentric_polynomial"))
            throw new InvalidOperationException($"Unsupported JLVision camera type '{cameraType}'.");
        if (isTelecentric && (!double.IsFinite(telecentricMagnification) || telecentricMagnification is < 0.001 or > 10))
            throw new InvalidOperationException("telecentricMagnification must be finite and between 0.001 and 10.");
        if (isTelecentric && (!double.IsFinite(telecentricPixelSizeUm) || telecentricPixelSizeUm is < 0.1 or > 100))
            throw new InvalidOperationException("telecentricPixelSizeUm must be finite and between 0.1 and 100.");
        var data = JLVisionExtendedOperators.Tuples(1873, "create_calib_data", 1, new("calibration_object"), new(1), new(request.Views.Count))[0];
        try
        {
            // The native model is retained in the JSON, rather than pretending its distortion
            // coefficients are interchangeable with OpenCV's Brown-Conrady coefficients.
            var focusOrMagnification = isTelecentric ? telecentricMagnification : .006;
            var pixelSizeX = isTelecentric ? telecentricPixelSizeUm * 1e-6 : focusOrMagnification / request.ImageWidth;
            var pixelSizeY = isTelecentric ? telecentricPixelSizeUm * 1e-6 : focusOrMagnification / request.ImageWidth;
            using var start = new JlTuple(new object[] { cameraType, focusOrMagnification, 0d, 0d, 0d, 0d, 0d, pixelSizeX, pixelSizeY, request.ImageWidth / 2d, request.ImageHeight / 2d, request.ImageWidth, request.ImageHeight });
            JLVisionExtendedOperators.Tuples(1872, "set_calib_data_cam_param", 0, new(data), new(0), new(cameraType), new(start));
            if (isTelecentric)
            {
                // Magnification and pixel pitch are coupled in a telecentric projection.
                // Keep the datasheet sensor pitch fixed while refining magnification/distortion.
                JLVisionExtendedOperators.Tuples(1861, "set_calib_data", 0,
                    new(data), new("camera"), new("general"), new("excluded_settings"), new(new object[] { "sx", "sy" }));
            }
            for (var i = 0; i < request.Views.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var view = request.Views[i];
                var xs = view.ObjectPoints.Select(p => p[0] * .001).ToArray();
                var ys = view.ObjectPoints.Select(p => p[1] * .001).ToArray();
                var zs = view.ObjectPoints.Select(p => p[2] * .001).ToArray();
                var rows = view.ImagePoints.Select(p => p[1]).ToArray(); var cols = view.ImagePoints.Select(p => p[0]).ToArray();
                JLVisionExtendedOperators.Tuples(1871, "set_calib_data_calib_object", 0, new(data), new(i), new(xs.Concat(ys).Concat(zs).ToArray()));
                var initial = JLVisionExtendedOperators.Tuples(1797, "vector_to_pose", 2, new(xs), new(ys), new(zs), new(rows), new(cols), new(start), new("iterative"), new("error"));
                using var pose = initial[0]; using var quality = initial[1];
                JLVisionExtendedOperators.Tuples(1868, "set_calib_data_observ_points", 0, new(data), new(0), new(i), new(0), new(rows), new(cols), new("all"), new(pose));
            }
            using var error = JLVisionExtendedOperators.Tuples(1859, "calibrate_cameras", 1, new JlTuple(data))[0];
            token.ThrowIfCancellationRequested();
            using var parameters = JLVisionExtendedOperators.Tuples(1870, "get_calib_data", 1, new(data), new("camera"), new(0), new("params"))[0];
            var returnedCameraType = parameters[0].S;
            if (cameraType != returnedCameraType || parameters.Length != 13) throw new InvalidOperationException("JLVision returned an unexpected camera model.");
            var raw = Enumerable.Range(1, 12).Select(i => parameters[i].D).ToArray();
            var fx = raw[0] / raw[6]; var fy = raw[0] / raw[7]; var rms = error[0].D;
            if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx <= 0 || fy <= 0 || !double.IsFinite(rms)) throw new InvalidOperationException("JLVision calibration produced invalid intrinsics.");
            return new(request.ImageWidth, request.ImageHeight, [[fx, 0, raw[8]], [0, fy, raw[9]], [0, 0, 1]], new double[8], rms, returnedCameraType, raw,
                isTelecentric ? "area_scan_telecentric_polynomial" : null);
        }
        finally
        {
            try { JLVisionExtendedOperators.Tuples(1853, "clear_calib_data", 0, new JlTuple(data)); }
            finally { data.Dispose(); }
        }
    }

    public static FisheyeCalibrationResult CalibrateFisheye(CameraCalibrationRequest request, CancellationToken token)
    {
        var objectPoints = request.Views.Select(view =>
        {
            var points = new Mat(view.ObjectPoints.Count, 1, MatType.CV_32FC3);
            for (var i = 0; i < view.ObjectPoints.Count; i++)
                points.Set(i, 0, new Point3f((float)(view.ObjectPoints[i][0] * 0.001), (float)(view.ObjectPoints[i][1] * 0.001), (float)(view.ObjectPoints[i][2] * 0.001)));
            return points;
        }).ToArray();
        var imagePoints = request.Views.Select(view =>
        {
            var points = new Mat(view.ImagePoints.Count, 1, MatType.CV_32FC2);
            for (var i = 0; i < view.ImagePoints.Count; i++)
                points.Set(i, 0, new Point2f((float)view.ImagePoints[i][0], (float)view.ImagePoints[i][1]));
            return points;
        }).ToArray();
        using var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
        cameraMatrix.Set(0, 0, 1d);
        cameraMatrix.Set(1, 1, 1d);
        cameraMatrix.Set(2, 2, 1d);
        using var distortion = new Mat(4, 1, MatType.CV_64FC1, Scalar.All(0));
        IEnumerable<Mat>? rotations = null;
        IEnumerable<Mat>? translations = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var rms = Cv2.FishEye.Calibrate(
                objectPoints,
                imagePoints,
                new Size(request.ImageWidth, request.ImageHeight),
                cameraMatrix,
                distortion,
                out rotations,
                out translations,
                FishEyeCalibrationFlags.FixSkew | FishEyeCalibrationFlags.RecomputeExtrinsic | FishEyeCalibrationFlags.CheckCond,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 100, 1e-7));
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(rms) || rms < 0 || !double.IsFinite(cameraMatrix.At<double>(0, 0)) || !double.IsFinite(cameraMatrix.At<double>(1, 1)) || cameraMatrix.At<double>(0, 0) <= 0 || cameraMatrix.At<double>(1, 1) <= 0)
                throw new InvalidOperationException("OpenCV fisheye calibration produced invalid intrinsics or RMS.");
            var coefficients = ReadFisheyeCoefficients(distortion);
            if (Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3).Select(column => cameraMatrix.At<double>(row, column))).Any(value => !double.IsFinite(value)) || coefficients.Any(value => !double.IsFinite(value)) || Math.Abs(cameraMatrix.At<double>(0, 1)) > 1e-9)
                throw new InvalidOperationException("OpenCV fisheye calibration produced a non-finite matrix or distortion coefficient, or a non-zero skew.");
            var viewErrors = new List<FisheyeViewError>(request.Views.Count);
            var rotationArray = (rotations ?? throw new InvalidOperationException("OpenCV fisheye calibration returned no rotation vectors.")).ToArray();
            var translationArray = (translations ?? throw new InvalidOperationException("OpenCV fisheye calibration returned no translation vectors.")).ToArray();
            if (rotationArray.Length != request.Views.Count || translationArray.Length != request.Views.Count)
                throw new InvalidOperationException("OpenCV fisheye calibration returned incomplete per-view poses.");
            for (var viewIndex = 0; viewIndex < request.Views.Count; viewIndex++)
            {
                token.ThrowIfCancellationRequested();
                using var projected = new Mat();
                using var jacobian = new Mat();
                Cv2.FishEye.ProjectPoints(objectPoints[viewIndex], projected, rotationArray[viewIndex], translationArray[viewIndex], cameraMatrix, distortion, 0, jacobian);
                var actual = request.Views[viewIndex].ImagePoints;
                var sumSquared = 0d;
                var maximum = 0d;
                for (var pointIndex = 0; pointIndex < actual.Count; pointIndex++)
                {
                    var estimate = projected.At<Point2f>(pointIndex, 0);
                    var dx = estimate.X - actual[pointIndex][0];
                    var dy = estimate.Y - actual[pointIndex][1];
                    var error = Math.Sqrt(dx * dx + dy * dy);
                    if (!double.IsFinite(error)) throw new InvalidOperationException($"OpenCV fisheye calibration produced a non-finite reprojection for view {viewIndex}.");
                    sumSquared += error * error;
                    maximum = Math.Max(maximum, error);
                }
                viewErrors.Add(new FisheyeViewError(Math.Sqrt(sumSquared / actual.Count), maximum));
            }
            var model = new CameraCalibrationModel(
                request.ImageWidth,
                request.ImageHeight,
                [[cameraMatrix.At<double>(0, 0), 0, cameraMatrix.At<double>(0, 2)], [0, cameraMatrix.At<double>(1, 1), cameraMatrix.At<double>(1, 2)], [0, 0, 1]],
                coefficients,
                rms,
                LensModel: "opencv_fisheye");
            return new FisheyeCalibrationResult(model, viewErrors);
        }
        finally
        {
            foreach (var pointMat in objectPoints) pointMat.Dispose();
            foreach (var pointMat in imagePoints) pointMat.Dispose();
            if (rotations is not null) foreach (var rotation in rotations) rotation.Dispose();
            if (translations is not null) foreach (var translation in translations) translation.Dispose();
        }
    }

    private static double[] ReadFisheyeCoefficients(Mat distortion)
    {
        if (distortion.Total() != 4 || distortion.Channels() != 1)
            throw new InvalidOperationException("OpenCV fisheye calibration returned an invalid distortion vector.");
        return Enumerable.Range(0, 4).Select(index => distortion.Rows == 1
            ? distortion.At<double>(0, index)
            : distortion.At<double>(index, 0)).ToArray();
    }

    public static Mat Undistort(Mat source, CameraCalibrationModel model, CancellationToken token, double fisheyeBalance = 0)
    {
        if (model.LensModel == "opencv_fisheye")
        {
            var fisheyeScaleX = (double)source.Width / model.ImageWidth;
            var fisheyeScaleY = (double)source.Height / model.ImageHeight;
            using var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1);
            cameraMatrix.Set(0, 0, model.CameraMatrix[0][0] * fisheyeScaleX);
            cameraMatrix.Set(0, 1, model.CameraMatrix[0][1] * fisheyeScaleX);
            cameraMatrix.Set(0, 2, model.CameraMatrix[0][2] * fisheyeScaleX);
            cameraMatrix.Set(1, 0, model.CameraMatrix[1][0] * fisheyeScaleY);
            cameraMatrix.Set(1, 1, model.CameraMatrix[1][1] * fisheyeScaleY);
            cameraMatrix.Set(1, 2, model.CameraMatrix[1][2] * fisheyeScaleY);
            cameraMatrix.Set(2, 0, model.CameraMatrix[2][0]);
            cameraMatrix.Set(2, 1, model.CameraMatrix[2][1]);
            cameraMatrix.Set(2, 2, model.CameraMatrix[2][2]);
            using var distortion = new Mat(1, 4, MatType.CV_64FC1);
            for (var i = 0; i < 4; i++) distortion.Set(0, i, model.DistCoeffs[i]);
            using var rectification = new Mat();
            using var outputCameraMatrix = new Mat();
            Cv2.FishEye.EstimateNewCameraMatrixForUndistortRectify(cameraMatrix, distortion, source.Size(), rectification, outputCameraMatrix, fisheyeBalance, source.Size(), 1d);
            using var undistorted = new Mat();
            Cv2.FishEye.UndistortImage(source, undistorted, cameraMatrix, distortion, outputCameraMatrix, source.Size());
            token.ThrowIfCancellationRequested();
            return undistorted.Clone();
        }
        using var image = JLVisionImages.FromMat(source);
        if (model.NativeCameraType is not null)
        {
            if (model.NativeCameraType is not ("area_scan_polynomial" or "area_scan_telecentric_polynomial") || model.NativeCameraParameters is not { Length: 12 } || model.NativeCameraParameters.Any(x => !double.IsFinite(x)))
                throw new InvalidOperationException("Invalid native JLVision camera model.");
            if (model.NativeCameraParameters[0] <= 0 || model.NativeCameraParameters[6] <= 0 || model.NativeCameraParameters[7] <= 0)
                throw new InvalidOperationException("Native camera focal length and pixel sizes must be positive.");
            if (model.NativeCameraParameters.Skip(1).Take(5).All(x => x == 0)) return source.Clone();
            var input = (double[])model.NativeCameraParameters.Clone();
            var sx = (double)source.Width / model.ImageWidth; var sy = (double)source.Height / model.ImageHeight;
            input[6] /= sx; input[7] /= sy; input[8] *= sx; input[9] *= sy; input[10] = source.Width; input[11] = source.Height;
            var output = (double[])input.Clone(); for (var i = 1; i <= 5; i++) output[i] = 0;
            using var map = JLVisionExtendedOperators.Image(1807, "gen_radial_distortion_map", [], CameraTuple(model.NativeCameraType, input), CameraTuple(model.NativeCameraType, output), new("bilinear"));
            using var result = image.MapImage(map); token.ThrowIfCancellationRequested();
            return JLVisionImages.ToMat(result);
        }
        // Old stored Brown-Conrady calibrations remain readable. Build their coordinate field,
        // then perform the actual image resampling with the JLVision map_image operator.
        if (model.DistCoeffs.All(x => x == 0)) return source.Clone();
        var scaleX = (double)source.Width / model.ImageWidth; var scaleY = (double)source.Height / model.ImageHeight;
        var fx = model.CameraMatrix[0][0] * scaleX; var fy = model.CameraMatrix[1][1] * scaleY;
        var cx = model.CameraMatrix[0][2] * scaleX; var cy = model.CameraMatrix[1][2] * scaleY;
        var d = new double[14]; Array.Copy(model.DistCoeffs, d, model.DistCoeffs.Length);
        using var rowMap = new Mat(source.Height, source.Width, MatType.CV_32FC1);
        using var colMap = new Mat(source.Height, source.Width, MatType.CV_32FC1);
        var imageWidth = source.Width; var imageHeight = source.Height;
        for (var row = 0; row < imageHeight; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var col = 0; col < imageWidth; col++)
            {
                var x = (col - cx) / fx; var y = (row - cy) / fy; var r2 = x * x + y * y; var r4 = r2 * r2; var r6 = r4 * r2;
                var radial = (1 + d[0] * r2 + d[1] * r4 + d[4] * r6) / (1 + d[5] * r2 + d[6] * r4 + d[7] * r6);
                var xd = x * radial + 2 * d[2] * x * y + d[3] * (r2 + 2 * x * x) + d[8] * r2 + d[9] * r4;
                var yd = y * radial + d[2] * (r2 + 2 * y * y) + 2 * d[3] * x * y + d[10] * r2 + d[11] * r4;
                if (d[12] != 0 || d[13] != 0)
                {
                    var tx = d[12]; var ty = d[13]; var cTx = Math.Cos(tx); var sTx = Math.Sin(tx); var cTy = Math.Cos(ty); var sTy = Math.Sin(ty);
                    var xx = cTy * xd + sTy * sTx * yd - sTy * cTx;
                    var yy = cTx * yd + sTx; var zz = sTy * xd - cTy * sTx * yd + cTy * cTx;
                    xd = (cTy * cTx * xx + sTy * cTx * zz) / zz;
                    yd = (cTy * cTx * yy - sTx * zz) / zz;
                }
                rowMap.Set(row, col, (float)(fy * yd + cy)); colMap.Set(row, col, (float)(fx * xd + cx));
            }
        }
        using var rows = JLVisionImages.FromMat(rowMap); using var cols = JLVisionImages.FromMat(colMap);
        using var field = JLVisionExtendedOperators.Image(1411, "real_to_vector_field", [rows, cols], new JlTuple("vector_field_absolute"));
        using var mapped = image.MapImage(field); token.ThrowIfCancellationRequested(); return JLVisionImages.ToMat(mapped);
    }

    private static JlTuple CameraTuple(string? cameraType, double[] values) => new(new object[] { cameraType ?? throw new InvalidOperationException("Native camera type is required.") }.Concat(values.Select(x => (object)x)).ToArray());
}
