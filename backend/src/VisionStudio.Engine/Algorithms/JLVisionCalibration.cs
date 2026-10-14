using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionCalibration
{
    public static CameraCalibrationModel Calibrate(CameraCalibrationRequest request, CancellationToken token)
    {
        var data = JLVisionExtendedOperators.Tuples(1873, "create_calib_data", 1, new("calibration_object"), new(1), new(request.Views.Count))[0];
        try
        {
            // The native model is retained in the JSON, rather than pretending its distortion
            // coefficients are interchangeable with OpenCV's Brown-Conrady coefficients.
            var focus = .006;
            using var start = new JlTuple(new object[] { "area_scan_polynomial", focus, 0d, 0d, 0d, 0d, 0d, focus / request.ImageWidth, focus / request.ImageWidth, request.ImageWidth / 2d, request.ImageHeight / 2d, request.ImageWidth, request.ImageHeight });
            JLVisionExtendedOperators.Tuples(1872, "set_calib_data_cam_param", 0, new(data), new(0), new("area_scan_polynomial"), new(start));
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
            var cameraType = parameters[0].S;
            if (cameraType != "area_scan_polynomial" || parameters.Length != 13) throw new InvalidOperationException("JLVision returned an unexpected camera model.");
            var raw = Enumerable.Range(1, 12).Select(i => parameters[i].D).ToArray();
            var fx = raw[0] / raw[6]; var fy = raw[0] / raw[7]; var rms = error[0].D;
            if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx <= 0 || fy <= 0 || !double.IsFinite(rms)) throw new InvalidOperationException("JLVision calibration produced invalid intrinsics.");
            return new(request.ImageWidth, request.ImageHeight, [[fx, 0, raw[8]], [0, fy, raw[9]], [0, 0, 1]], new double[8], rms, cameraType, raw);
        }
        finally
        {
            try { JLVisionExtendedOperators.Tuples(1853, "clear_calib_data", 0, new JlTuple(data)); }
            finally { data.Dispose(); }
        }
    }

    public static Mat Undistort(Mat source, CameraCalibrationModel model, CancellationToken token)
    {
        using var image = JLVisionImages.FromMat(source);
        if (model.NativeCameraType is not null)
        {
            if (model.NativeCameraType != "area_scan_polynomial" || model.NativeCameraParameters is not { Length: 12 } || model.NativeCameraParameters.Any(x => !double.IsFinite(x)))
                throw new InvalidOperationException("Invalid native JLVision camera model.");
            if (model.NativeCameraParameters[0] <= 0 || model.NativeCameraParameters[6] <= 0 || model.NativeCameraParameters[7] <= 0)
                throw new InvalidOperationException("Native camera focal length and pixel sizes must be positive.");
            if (model.NativeCameraParameters.Skip(1).Take(5).All(x => x == 0)) return source.Clone();
            var input = (double[])model.NativeCameraParameters.Clone();
            var sx = (double)source.Width / model.ImageWidth; var sy = (double)source.Height / model.ImageHeight;
            input[6] /= sx; input[7] /= sy; input[8] *= sx; input[9] *= sy; input[10] = source.Width; input[11] = source.Height;
            var output = (double[])input.Clone(); for (var i = 1; i <= 5; i++) output[i] = 0;
            using var map = JLVisionExtendedOperators.Image(1807, "gen_radial_distortion_map", [], CameraTuple(input), CameraTuple(output), new("bilinear"));
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

    private static JlTuple CameraTuple(double[] values) => new(new object[] { "area_scan_polynomial" }.Concat(values.Select(x => (object)x)).ToArray());
}
