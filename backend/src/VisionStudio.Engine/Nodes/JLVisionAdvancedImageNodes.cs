using System.Text.Json;
using VisionStudio.Engine.Runtime;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using OpenCvSharp;

namespace VisionStudio.Engine.Nodes;

public sealed class JLVisionDxfNode : IVisionNodeExecutor
{
    public string Type => "jlvision.dxf";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "read", "read", "write");
        var path = SaveImageNode.RequirePath(node, "");
        if (!string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("轮廓文件必须使用 .dxf 扩展名。");
        IReadOnlyList<VisionContour> contours; string status; string unit;
        if (operation == "read")
        {
            if (!File.Exists(path)) throw new FileNotFoundException("DXF 文件不存在。", path);
            if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new InvalidOperationException("DXF 文件超过 100 MB。");
            using var native = new JlXLDCont(); using var names = new JlTuple(); using var values = new JlTuple();
            using var readStatus = native.ReadContourXldDxf(path, names, values);
            contours = JlContourCodec.FromNative(native, cancellationToken, 1000000);
            unit = AdvancedJl.Choice(node, "unit", "unspecified", "unspecified", "px", "mm", "cm", "m", "um");
            var scale = AdvancedJl.Number(node, "scale", 1, 1e-9, 1e9);
            var signY = node.GetBool("flipY", false) ? -1 : 1;
            var offsetX = AdvancedJl.Number(node, "offsetX", 0); var offsetY = AdvancedJl.Number(node, "offsetY", 0);
            if (contours.Count > 0)
            {
                var adjusted = contours.Select(c => c with { Points = c.Points.Select(p => new VisionPoint2D(p.X * scale + offsetX, p.Y * scale * signY + offsetY)).ToArray() }).ToArray();
                using var transformed = JlContourCodec.ToNative(adjusted);
                contours = JlContourCodec.FromNative(transformed, cancellationToken, 1000000)
                    .Select(c => c with { Unit = unit, CoordinateFrame = $"cad:{node.Id}:{unit}" }).ToArray();
            }
            status = string.Join("; ", Enumerable.Range(0, readStatus.Length).Select(i => readStatus[i].ToString()));
        }
        else
        {
            contours = context.Require<IReadOnlyList<VisionContour>>("contours");
            if (contours.Count == 0) throw new InvalidOperationException("不能导出空轮廓集合。");
            unit = contours[0].Unit;
            if (contours.Any(c => c.Unit != unit || c.CoordinateFrame != contours[0].CoordinateFrame))
                throw new InvalidOperationException("导出轮廓的单位与坐标系必须一致；请先完成坐标转换。");
            var overwrite = node.GetBool("overwrite", false);
            if (!overwrite && File.Exists(path)) throw new IOException("DXF 文件已存在；启用覆盖或更改路径。");
            SaveImageNode.EnsureDirectory(path); var temporary = SaveImageNode.CreateTempPath(path);
            try
            {
                using var native = JlContourCodec.ToNative(contours); native.WriteContourXldDxf(temporary);
                cancellationToken.ThrowIfCancellationRequested(); SaveImageNode.Commit(temporary, path, overwrite);
            }
            finally { SaveImageNode.TryDelete(temporary); }
            status = "written";
        }
        return ValueTask.FromResult(AdvancedJl.Result(node, operation, new() { ["contours"] = VisionValue.Contours(contours), ["count"] = VisionValue.Integer(contours.Count),
            ["found"] = VisionValue.Boolean(contours.Count > 0), ["path"] = VisionValue.String(path), ["status"] = VisionValue.String(status), ["unit"] = VisionValue.String(unit) }));
    }
}

public sealed class JLVisionWorldPlaneNode : IVisionNodeExecutor
{
    public string Type => "jlvision.worldPlane";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "image", "image", "map", "contour", "applyMap");
        using var outputs = new AdvancedJl.OwnedOutputs();
        if (operation == "applyMap")
        {
            var sourceImage = context.RequireImage("image");
            var rowsImage = context.RequireImage("mapRows"); var columnsImage = context.RequireImage("mapColumns");
            var info = JlPlaneMapContract.Validate(context.Require<string>("mapInfo"), sourceImage.Mat, rowsImage.Mat, columnsImage.Mat);
            using var source = JLVisionImages.FromMat(sourceImage.Mat);
            using var mapRows = JLVisionImages.FromMat(rowsImage.Mat);
            using var mapColumns = JLVisionImages.FromMat(columnsImage.Mat);
            using var map = mapRows.RealToVectorField(mapColumns, "vector_field_absolute");
            using var mapped = source.MapImage(map); outputs["image"] = AdvancedJl.Image(mapped);
            outputs["unit"] = VisionValue.String(info.Unit);
            outputs["mapInfo"] = VisionValue.String(context.Require<string>("mapInfo"));
        }
        else
        {
            var json = context.TryGetValue("calibration", out var value) ? value.Require<string>("calibration") : node.GetString("cameraParameters", "");
            using var camera = CameraParameters(json);
            var poseJson = context.TryGetValue("worldPose", out var poseValue) ? poseValue.Require<string>("worldPose") : node.GetString("worldPoseJson", "");
            using var pose = AdvancedJl.Tuple(poseJson, "平面位姿");
            if (pose.Length != 7) throw new InvalidOperationException("平面位姿必须包含 7 个 JLVision 位姿值，平移使用米、角度使用度。");
            var unit = AdvancedJl.Choice(node, "unit", "mm", "m", "cm", "mm", "um");
            using var scale = new JlTuple(unit);
            if (operation == "contour")
            {
                using var contours = JlContourCodec.ToNative(context.Require<IReadOnlyList<VisionContour>>("contours"));
                JlOperatorSet.ContourToWorldPlaneXld(contours, out var transformed, camera, pose, scale);
                using (transformed)
                using (var typed = new JlXLDCont(transformed)) outputs["contours"] = VisionValue.Contours(JlContourCodec.FromNative(typed, cancellationToken).Select(c => c with { Unit = unit, CoordinateFrame = $"world:{node.Id}:plane" }).ToArray());
            }
            else
            {
                var source = context.RequireImage("image");
                var width = AdvancedJl.Integer(node, "width", 512, 1, 16384); var height = AdvancedJl.Integer(node, "height", 512, 1, 16384);
                if ((long)width * height > 100000000) throw new InvalidOperationException("映射图像最多 100 MP。");
                // Image scale is metres per output pixel; contour scale is a physical unit name.
                var pixelSize = AdvancedJl.Number(node, "pixelSize", .1, 1e-9, 1e6);
                using var pixelScale = new JlTuple(pixelSize * (unit switch { "m" => 1, "cm" => .01, "mm" => .001, _ => .000001 }));
                using var w = new JlTuple(width); using var h = new JlTuple(height);
                if (operation == "image")
                {
                    using var native = JLVisionImages.FromMat(source.Mat); using var interpolation = new JlTuple("bilinear");
                    JlOperatorSet.ImageToWorldPlane(native, out var mapped, camera, pose, w, h, pixelScale, interpolation);
                    using (mapped) using (var typed = new JlImage(mapped)) outputs["image"] = AdvancedJl.Image(typed);
                }
                else
                {
                    using var widthIn = new JlTuple(source.Width); using var heightIn = new JlTuple(source.Height); using var mapType = new JlTuple("coord_map_sub_pix");
                    JlOperatorSet.GenImageToWorldPlaneMap(out var map, camera, pose, widthIn, heightIn, w, h, pixelScale, mapType);
                    using (map) using (var typed = new JlImage(map))
                    using (var mapRows = typed.VectorFieldToReal(out var mapColumns)) using (mapColumns)
                    {
                        outputs["mapRows"] = AdvancedJl.Image(mapRows); outputs["mapColumns"] = AdvancedJl.Image(mapColumns);
                        var rowsMat = outputs["mapRows"].Require<IVisionImage>("mapRows").NativeImage as OpenCvSharp.Mat ?? throw new InvalidOperationException("映射行图无效。");
                        var columnsMat = outputs["mapColumns"].Require<IVisionImage>("mapColumns").NativeImage as OpenCvSharp.Mat ?? throw new InvalidOperationException("映射列图无效。");
                        outputs["mapInfo"] = VisionValue.String(JlPlaneMapContract.Create(node.Id, source.Width, source.Height, unit, pixelSize, json, poseJson, rowsMat, columnsMat));
                    }
                }
            }
            outputs["unit"] = VisionValue.String(unit);
        }
        return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
    }
    private static JlTuple CameraParameters(string json)
    {
        if (json.TrimStart().StartsWith('[')) return AdvancedJl.Tuple(json, "相机参数", mixed: true);
        try
        {
            var model = JsonSerializer.Deserialize<CameraCalibrationModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (model?.NativeCameraType is null || model.NativeCameraParameters is not { Length: > 0 } parameters || parameters.Any(v => !double.IsFinite(v)))
                throw new InvalidOperationException("请连接包含 JLVision 原生相机参数的内参标定结果。");
            return new JlTuple(new object[] { model.NativeCameraType }.Concat(parameters.Cast<object>()).ToArray());
        }
        catch (JsonException ex) { throw new InvalidOperationException("相机标定结果不是有效 JSON。", ex); }
    }
}

public sealed class JLVisionRestorationNode : IVisionNodeExecutor
{
    public string Type => "jlvision.restoration";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "harmonic", "harmonic", "inpaintingCt", "inpaintingMcf", "inpaintingCed", "inpaintingAniso", "wiener", "wienerNi", "psfMotion", "psfDefocus");
        JlImage? image = null; JlImage? second = null; JlImage? third = null; JlRegion? region = null; JlImage? result = null;
        try
        {
            if (operation is "psfMotion" or "psfDefocus")
            {
                result = new JlImage();
                var reference = context.TryGetValue("image", out _) ? context.RequireImage("image") : null;
                var width = reference?.Width ?? AdvancedJl.Integer(node, "width", 31, 1, 4095);
                var height = reference?.Height ?? AdvancedJl.Integer(node, "height", 31, 1, 4095);
                if ((long)width * height > 100000000) throw new InvalidOperationException("模糊核最多支持 100 MP，请降低参考图尺寸。");
                var blur = AdvancedJl.Number(node, "blur", 3, .01, 1000);
                if (operation == "psfMotion") result.GenPsfMotion(width, height, blur, AdvancedJl.Integer(node, "angle", 0, -180, 180), AdvancedJl.Integer(node, "psfType", 3, 1, 5));
                else result.GenPsfDefocus(width, height, blur);
                using var domain = result.GetDomain();
                var energy = domain.Intensity(result, out double _) * width * height;
                if (!double.IsFinite(energy) || energy <= 0) throw new InvalidOperationException("原生模糊核能量无效。");
                var normalized = result.ScaleImage(1 / energy, 0); result.Dispose(); result = normalized;
            }
            else
            {
                var source = context.RequireImage("image"); image = JLVisionImages.FromMat(source.Mat);
                if (operation != "wiener")
                {
                    var mask = context.Require<VisionRegion>("region");
                    if (mask.Width != source.Width || mask.Height != source.Height || mask.Components.Count == 0) throw new InvalidOperationException("修复或噪声区域必须非空并与图像尺寸一致。");
                    using var components = JLVisionRegions.ToNative(mask); region = components.Union1();
                }
                if (operation is "wiener" or "wienerNi")
                {
                    var psf = context.RequireImage("psf");
                    if (psf.Width != source.Width || psf.Height != source.Height || psf.Mat.Type() != OpenCvSharp.MatType.CV_32FC1)
                        throw new InvalidOperationException("维纳复原需要与原图尺寸相同的单通道实数 PSF；请将原图接到模糊核生成节点的 image 输入以自动匹配尺寸。");
                    if (!AdvancedImageValidation.FiniteFloatImage(psf.Mat)) throw new InvalidOperationException("模糊核包含 NaN 或无穷值，请重新生成有效的实数 PSF。");
                    second = JLVisionImages.FromMat(psf.Mat);
                }
                switch (operation)
                {
                    case "harmonic": result = image.HarmonicInterpolation(region!, AdvancedJl.Number(node, "precision", .01, 1e-9, 100)); break;
                    case "inpaintingCt": result = image.InpaintingCt(region!, AdvancedJl.Number(node, "epsilon", 5, 1, 20), AdvancedJl.Number(node, "kappa", 25, 0, 100), AdvancedJl.Number(node, "sigma", 1, 0), AdvancedJl.Number(node, "rho", 2, .01), AdvancedJl.Number(node, "channelCoefficient", 1, 1e-9)); break;
                    case "inpaintingMcf": result = image.InpaintingMcf(region!, AdvancedJl.Number(node, "sigma", 1, 0), AdvancedJl.Number(node, "theta", .5, .001, 1), AdvancedJl.Integer(node, "iterations", 10, 1, 10000)); break;
                    case "inpaintingCed": result = image.InpaintingCed(region!, AdvancedJl.Number(node, "sigma", 1, 0), AdvancedJl.Number(node, "rho", 2, .01), AdvancedJl.Number(node, "theta", .5, .001, 1), AdvancedJl.Integer(node, "iterations", 10, 1, 10000)); break;
                    case "inpaintingAniso": result = image.InpaintingAniso(region!, node.GetString("mode", "perona-malik"), AdvancedJl.Number(node, "contrast", 5, .01), AdvancedJl.Number(node, "theta", .5, .001, 1), AdvancedJl.Integer(node, "iterations", 10, 1, 10000), AdvancedJl.Number(node, "rho", 2, .01)); break;
                    case "wienerNi": result = image.WienerFilterNi(second!, region!, AdvancedJl.Integer(node, "maskWidth", 5, 1, 511), AdvancedJl.Integer(node, "maskHeight", 5, 1, 511)); break;
                    case "wiener":
                        var filtered = context.RequireImage("filteredImage");
                        if (filtered.Width != source.Width || filtered.Height != source.Height || filtered.Mat.Type() != source.Mat.Type()) throw new InvalidOperationException("平滑参考图必须与原图尺寸、通道数和像素类型一致。");
                        third = JLVisionImages.FromMat(filtered.Mat); result = image.WienerFilter(second!, third); break;
                }
            }
            using var outputs = new AdvancedJl.OwnedOutputs { ["image"] = AdvancedJl.Image(result!) };
            var outputImage = outputs["image"].Require<IVisionImage>("image");
            outputs["width"] = VisionValue.Integer(outputImage.Width);
            outputs["height"] = VisionValue.Integer(outputImage.Height);
            return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
        }
        finally { result?.Dispose(); region?.Dispose(); third?.Dispose(); second?.Dispose(); image?.Dispose(); }
    }
}

public sealed class JLVisionSurfaceNode : IVisionNodeExecutor
{
    public string Type => "jlvision.surface";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "sfsPentland", "sfsPentland", "sfsOrigLr", "sfsModLr", "shadeHeightField", "reconstructGradient");
        var source = context.RequireImage("image");
        if (operation.StartsWith("sfs", StringComparison.Ordinal) && source.Mat.Type() != OpenCvSharp.MatType.CV_8UC1)
            throw new InvalidOperationException("明暗重建需要单通道 8 位灰度图；请先进行灰度与像素类型转换。");
        if (!operation.StartsWith("sfs", StringComparison.Ordinal) && source.Mat.Type() != OpenCvSharp.MatType.CV_32FC1)
            throw new InvalidOperationException("高度场渲染和梯度重建需要单通道实数图，请先转换为 real 像素类型。");
        if (!AdvancedImageValidation.FiniteFloatImage(source.Mat)) throw new InvalidOperationException("高度场或梯度图包含 NaN 或无穷值。");
        if (operation == "reconstructGradient")
        {
            var columns = context.RequireImage("gradientColumns");
            if (columns.Width != source.Width || columns.Height != source.Height || columns.Mat.Type() != OpenCvSharp.MatType.CV_32FC1)
                throw new InvalidOperationException("行、列梯度必须为同尺寸的单通道实数图；不接受彩色图或隐式灰度转换。");
            if (!AdvancedImageValidation.FiniteFloatImage(columns.Mat)) throw new InvalidOperationException("列梯度图包含 NaN 或无穷值。");
        }
        using var image = JLVisionImages.FromMat(source.Mat);
        var slant = operation == "reconstructGradient" ? 0 : AdvancedJl.Number(node, "slant", .5, 0, Math.PI / 2);
        var tilt = operation == "reconstructGradient" ? 0 : AdvancedJl.Number(node, "tilt", 0, -Math.PI * 2, Math.PI * 2);
        var albedo = operation == "reconstructGradient" ? 1 : AdvancedJl.Number(node, "albedo", 1, .000001);
        var ambient = operation == "reconstructGradient" ? 0 : AdvancedJl.Number(node, "ambient", 0, 0);
        JlImage? result = null;
        try
        {
            switch (operation)
            {
                case "sfsPentland": result = image.SfsPentland(slant, tilt, albedo, ambient); break;
                case "sfsOrigLr": result = image.SfsOrigLr(slant, tilt, albedo, ambient); break;
                case "sfsModLr": result = image.SfsModLr(slant, tilt, albedo, ambient); break;
                case "shadeHeightField":
                    using (var s = new JlTuple(slant)) using (var t = new JlTuple(tilt)) using (var a = new JlTuple(albedo)) using (var b = new JlTuple(ambient))
                        result = image.ShadeHeightField(s, t, a, b, node.GetBool("shadows", false) ? "true" : "false"); break;
                case "reconstructGradient":
                    using (var second = JLVisionImages.FromMat(context.RequireImage("gradientColumns").Mat))
                    using (var vector = image.RealToVectorField(second, "vector_field_relative"))
                    using (var names = new JlTuple()) using (var values = new JlTuple())
                        result = vector.ReconstructHeightFieldFromGradient(node.GetString("method", "poisson"), names, values);
                    break;
            }
            var isShading = operation == "shadeHeightField";
            using var outputs = new AdvancedJl.OwnedOutputs { ["image"] = AdvancedJl.Image(result!),
                ["resultKind"] = VisionValue.String(isShading ? "shading" : "relativeHeight"),
                ["unit"] = VisionValue.String(isShading ? "intensity" : "relative") };
            // The workflow ownership table deduplicates aliases by reference.
            outputs[isShading ? "shadingImage" : "heightField"] = outputs["image"];
            return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
        }
        finally { result?.Dispose(); }
    }
}

internal static class AdvancedImageValidation
{
    public static bool FiniteFloatImage(Mat mat)
    {
        if (mat.Empty()) return false;
        if (mat.Depth() == MatType.CV_32F)
        {
            using var copy = mat.IsContinuous() ? null : mat.Clone();
            var source = copy ?? mat;
            source.GetArray(out float[] values);
            foreach (var value in values)
                if (!float.IsFinite(value)) return false;
        }
        else if (mat.Depth() == MatType.CV_64F)
        {
            using var copy = mat.IsContinuous() ? null : mat.Clone();
            var source = copy ?? mat;
            source.GetArray(out double[] values);
            foreach (var value in values)
                if (!double.IsFinite(value)) return false;
        }
        return true;
    }
}
