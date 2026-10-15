using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed record CameraCalibrationView(List<List<double>> ObjectPoints, List<List<double>> ImagePoints);
public sealed record CameraCalibrationRequest(int ImageWidth, int ImageHeight, List<CameraCalibrationView> Views);
public sealed record CameraCalibrationModel(int ImageWidth, int ImageHeight, double[][] CameraMatrix, double[] DistCoeffs, double Rms, string? NativeCameraType = null, double[]? NativeCameraParameters = null);

/// <summary>Intrinsic pinhole calibration from user-provided corresponding chessboard object/image points.</summary>
public sealed class CameraIntrinsicCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.cameraIntrinsics";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = context.TryGetValue("calibrationDataJson", out var connectedViews)
            ? connectedViews.Require<string>("calibrationDataJson")
            : node.GetString("calibrationDataJson", "{}");
        CameraCalibrationRequest request;
        try { request = JsonSerializer.Deserialize<CameraCalibrationRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid calibrationDataJson: {ex.Message}", ex); }
        if (request.ImageWidth is < 16 or > 32768 || request.ImageHeight is < 16 or > 32768) throw new InvalidOperationException("ImageWidth and ImageHeight must each be between 16 and 32768 pixels.");
        if (request.Views is null || request.Views.Count < 3) throw new InvalidOperationException("Intrinsic calibration requires at least three views; use varied board orientations and positions.");
        var objects = new List<IEnumerable<Point3f>>(); var images = new List<IEnumerable<Point2f>>();
        foreach (var (view, index) in request.Views.Select((v, i) => (v, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view is null || view.ObjectPoints is null || view.ImagePoints is null || view.ObjectPoints.Count < 6 || view.ObjectPoints.Count != view.ImagePoints.Count)
                throw new InvalidOperationException($"View {index} must have matching object/image point arrays with at least six points.");
            if (view.ObjectPoints.Any(p => p is null || p.Count != 3 || p.Any(v => !double.IsFinite(v))) || view.ImagePoints.Any(p => p is null || p.Count != 2 || p.Any(v => !double.IsFinite(v))))
                throw new InvalidOperationException($"View {index} points must be finite [X,Y,Z] and [u,v] arrays.");
            var ip = view.ImagePoints.Select(p => new Point2f((float)p[0], (float)p[1])).ToArray();
            if (ip.Any(p => p.X < 0 || p.Y < 0 || p.X >= request.ImageWidth || p.Y >= request.ImageHeight)) throw new InvalidOperationException($"View {index} contains image points outside the declared image size.");
            var mx=ip.Average(p=>(double)p.X); var my=ip.Average(p=>(double)p.Y);
            var xx=ip.Sum(p=>Math.Pow(p.X-mx,2)); var yy=ip.Sum(p=>Math.Pow(p.Y-my,2)); var xy=ip.Sum(p=>(p.X-mx)*(p.Y-my));
            if (xx*yy-xy*xy < request.ImageWidth * request.ImageHeight * 1e-5) throw new InvalidOperationException($"View {index} image points are degenerate or nearly collinear.");
            objects.Add(view.ObjectPoints.Select(p => new Point3f((float)p[0], (float)p[1], (float)p[2])).ToArray());
            images.Add(ip);
        }
        CameraCalibrationModel model;
        try { model = JLVisionCalibration.Calibrate(request, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"JLVision intrinsic calibration failed: {ex.Message}", ex); }
        var rms = model.Rms;
        var resultJson = JsonSerializer.Serialize(model);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["calibrationJson"] = VisionValue.String(resultJson), ["rms"] = VisionValue.Double(rms) }, new Dictionary<string, object?> { ["views"] = request.Views.Count, ["imageSize"] = $"{request.ImageWidth}x{request.ImageHeight}", ["rms"] = rms, ["model"] = model.NativeCameraType, ["algorithmBackend"] = "JLVision" }));
    }
}

public sealed class ImageUndistortNode : IVisionNodeExecutor
{
    public string Type => "image.undistort";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        CameraCalibrationModel model;
        var calibrationJson = context.TryGetValue("calibrationJson", out var connectedCalibration)
            ? connectedCalibration.Require<string>("calibrationJson")
            : node.GetString("calibrationJson", "{}");
        try { model = JsonSerializer.Deserialize<CameraCalibrationModel>(calibrationJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid camera calibration JSON: {ex.Message}", ex); }
        if (model.ImageWidth < 1 || model.ImageHeight < 1 || model.CameraMatrix is not { Length: 3 } || model.CameraMatrix.Any(r => r is not { Length: 3 } || r.Any(x => !double.IsFinite(x))) || model.DistCoeffs is null || model.DistCoeffs.Length is not (4 or 5 or 8 or 12 or 14) || model.DistCoeffs.Any(x => !double.IsFinite(x)) || model.CameraMatrix[0][0] <= 0 || model.CameraMatrix[1][1] <= 0 || Math.Abs(model.CameraMatrix[2][0]) > 1e-12 || Math.Abs(model.CameraMatrix[2][1]) > 1e-12 || Math.Abs(model.CameraMatrix[2][2]-1) > 1e-9)
            throw new InvalidOperationException("Calibration JSON must contain a finite 3x3 cameraMatrix, positive focal lengths, and 4/5/8/12/14 finite distCoeffs.");
        Mat output;
        try { output = JLVisionCalibration.Undistort(source, model, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"JLVision undistortion failed: {ex.Message}", ex); }
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) }, new Dictionary<string, object?> { ["sourceSize"] = $"{source.Width}x{source.Height}", ["calibrationSize"] = $"{model.ImageWidth}x{model.ImageHeight}", ["scaledIntrinsics"] = source.Width != model.ImageWidth || source.Height != model.ImageHeight }));
    }
}

public sealed record HandEyeSample(VisionPose2D Robot, VisionPose2D Vision);
public sealed record HandEyeRequest(List<HandEyeSample> Samples);

/// <summary>Planar SE(2) hand-eye calibration only. Uses closure minimization; no 3D rigid transform is produced.</summary>
public sealed class PlanarHandEyeCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.handEye2D";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HandEyeRequest request;
        try { request = JsonSerializer.Deserialize<HandEyeRequest>(node.GetString("samplesJson", "{}"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid samplesJson: {ex.Message}", ex); }
        var samples = request.Samples;
        if (samples is null || samples.Count < 4) throw new InvalidOperationException("2D hand-eye calibration requires at least four robot/vision pose pairs.");
        if (samples.Any(s => s is null || !Finite(s.Robot) || !Finite(s.Vision))) throw new InvalidOperationException("Robot and vision pose coordinates and angles must be finite, and each sample must be non-null.");
        var mode = node.GetString("mode", "eyeInHand").Trim();
        if (!mode.Equals("eyeInHand", StringComparison.OrdinalIgnoreCase) && !mode.Equals("eyeToHand", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mode must be eyeInHand or eyeToHand.");
        var robotAngles = samples.Select(s => Normalize(s.Robot.ThetaDeg)).ToArray();
        var visionAngles=samples.Select(s=>Normalize(s.Vision.ThetaDeg)).ToArray();
        if (robotAngles.Max() - robotAngles.Min() < 8 || samples.Select(s => s.Robot.X).Distinct().Count() < 2 && samples.Select(s => s.Robot.Y).Distinct().Count() < 2 || visionAngles.Max()-visionAngles.Min()<8)
            throw new InvalidOperationException("Robot poses are degenerate: vary both rotation and translation across calibration samples.");
        var x = new double[3];
        // Damped finite-difference Gauss-Newton over the unknown SE(2) hand-eye transform.
        var damping = 1e-3;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var residual = Residuals(samples, x, mode); var jac = new double[residual.Length, 3];
            for (var j = 0; j < 3; j++) { var perturbed = (double[])x.Clone(); perturbed[j] += j == 2 ? 1e-5 : 1e-4; var delta = Residuals(samples, perturbed, mode); for (var i = 0; i < residual.Length; i++) jac[i,j] = (delta[i]-residual[i]) / (j == 2 ? 1e-5 : 1e-4); }
            var h = new double[3,3]; var g = new double[3];
            for (var i = 0; i < residual.Length; i++) for (var r = 0; r < 3; r++) { g[r] -= jac[i,r]*residual[i]; for (var c = 0; c < 3; c++) h[r,c] += jac[i,r]*jac[i,c]; }
            var trace = h[0,0]+h[1,1]+h[2,2];
            if (!double.IsFinite(trace) || trace < 1e-10) throw new InvalidOperationException("Hand-eye solve is degenerate; robot/vision motions do not constrain a unique 2D transform.");
            EnsureRank(h);
            var hd = (double[,])h.Clone(); for (var d=0;d<3;d++) hd[d,d] += damping * Math.Max(1, h[d,d]);
            double[] step;
            try { step = Solve(hd,g); } catch (InvalidOperationException) { throw new InvalidOperationException("Hand-eye solve is singular; collect more diverse robot and vision poses."); }
            var candidate = new[] { x[0]+step[0], x[1]+step[1], x[2]+step[2] };
            if (Norm(Residuals(samples,candidate,mode)) < Norm(residual)) { x = candidate; damping = Math.Max(1e-9,damping*0.3); if (Math.Sqrt(step.Sum(v=>v*v)) < 1e-7) break; }
            else damping = Math.Min(1e9,damping*10);
        }
        cancellationToken.ThrowIfCancellationRequested();
        CheckObservability(samples,x,mode);
        var errors = Residuals(samples,x,mode); var translationRms = Math.Sqrt(Enumerable.Range(0,samples.Count).Average(i=>errors[3*i]*errors[3*i]+errors[3*i+1]*errors[3*i+1])); var angleRms = Math.Sqrt(Enumerable.Range(0,samples.Count).Average(i=>errors[3*i+2]*errors[3*i+2]))/50;
        if (!double.IsFinite(translationRms) || translationRms > 1e6) throw new InvalidOperationException("Hand-eye solve did not converge to a finite solution.");
        var theta=Normalize(x[2]*180/Math.PI); var pose = new VisionPose2D(x[0],x[1],theta);
        var radians=theta*Math.PI/180; var sourceFrame=node.GetString("sourceFrame","Camera");
        var defaultTargetFrame=mode.Equals("eyeInHand",StringComparison.OrdinalIgnoreCase)?"RobotTool":"RobotBase";
        var targetFrame=node.GetString("targetFrame",defaultTargetFrame); if(string.IsNullOrWhiteSpace(targetFrame))targetFrame=defaultTargetFrame;
        var unit=node.GetString("unit","mm");
        var transform=new VisionTransform2D(sourceFrame,targetFrame,unit,Math.Cos(radians),-Math.Sin(radians),x[0],Math.Sin(radians),Math.Cos(radians),x[1],0,0,1,node.GetString("sourceUnit","mm"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["pose"] = VisionValue.Pose(pose), ["transform"] = VisionValue.Transform(transform), ["translationRms"] = VisionValue.Double(translationRms), ["angleRmsDeg"] = VisionValue.Double(angleRms*180/Math.PI) }, new Dictionary<string, object?> { ["mode"] = mode, ["samples"] = samples.Count, ["transform2D"] = "SE(2)", ["sourceFrame"] = sourceFrame, ["targetFrame"] = targetFrame, ["unit"] = unit, ["translationRms"] = translationRms, ["angleRmsDeg"] = angleRms*180/Math.PI, ["scope"] = "planar only; no 3D hand-eye" }));
    }
    private static bool Finite(VisionPose2D p) => double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.ThetaDeg);
    private static double[] Residuals(List<HandEyeSample> s, double[] x, string mode)
    {
        var result = new double[s.Count*3]; var closures = s.Select(v => mode.Equals("eyeInHand",StringComparison.OrdinalIgnoreCase) ? Mul(Mul(v.Robot,x),v.Vision) : Mul(Mul(Inv(v.Robot),x),v.Vision)).ToArray();
        var meanAngle = Math.Atan2(closures.Average(v=>Math.Sin(v.Theta)),closures.Average(v=>Math.Cos(v.Theta)));
        for(var i=0;i<s.Count;i++){result[i*3]=closures[i].X-closures.Average(v=>v.X);result[i*3+1]=closures[i].Y-closures.Average(v=>v.Y);result[i*3+2]=Wrap(closures[i].Theta-meanAngle)*50;}
        return result;
    }
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,(double X,double Y,double Theta) b){var c=Math.Cos(a.Theta);var s=Math.Sin(a.Theta);return(a.X+c*b.X-s*b.Y,a.Y+s*b.X+c*b.Y,a.Theta+b.Theta);}
    private static (double X,double Y,double Theta) Inv(VisionPose2D p){var t=p.ThetaDeg*Math.PI/180;return(-Math.Cos(t)*p.X-Math.Sin(t)*p.Y,Math.Sin(t)*p.X-Math.Cos(t)*p.Y,-t);}
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,VisionPose2D b)=>Mul(a,(b.X,b.Y,b.ThetaDeg*Math.PI/180));
    private static (double X,double Y,double Theta) Mul(VisionPose2D a,double[] b)=>Mul((a.X,a.Y,a.ThetaDeg*Math.PI/180),(b[0],b[1],b[2]));
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,double[] b)=>Mul(a,(b[0],b[1],b[2]));
    private static double Wrap(double x)=>Math.Atan2(Math.Sin(x),Math.Cos(x));
    private static double Normalize(double deg){var r=deg%360;if(r>180)r-=360;if(r<=-180)r+=360;return r;}
    private static double Norm(double[] v)=>Math.Sqrt(v.Sum(x=>x*x));
    private static void CheckObservability(List<HandEyeSample> samples,double[] x,string mode)
    {
        var residual=Residuals(samples,x,mode);var jac=new double[residual.Length,3];
        for(var j=0;j<3;j++){var p=(double[])x.Clone();var step=j==2?1e-5:1e-4;p[j]+=step;var shifted=Residuals(samples,p,mode);for(var i=0;i<residual.Length;i++)jac[i,j]=(shifted[i]-residual[i])/step;}
        var h=new double[3,3];for(var i=0;i<residual.Length;i++)for(var r=0;r<3;r++)for(var c=0;c<3;c++)h[r,c]+=jac[i,r]*jac[i,c];EnsureRank(h);
    }
    private static void EnsureRank(double[,] h){var a=(double[,])h.Clone();var n=3;var scale=Math.Max(a[0,0],Math.Max(a[1,1],a[2,2]));if(scale<=0)throw new InvalidOperationException("Hand-eye solve is rank deficient.");for(var k=0;k<n;k++){var p=k;for(var r=k+1;r<n;r++)if(Math.Abs(a[r,k])>Math.Abs(a[p,k]))p=r;if(Math.Abs(a[p,k])<scale*1e-10)throw new InvalidOperationException("Hand-eye solve is non-unique: the 2D robot/vision motion does not sufficiently constrain translation and rotation.");for(var c=k;c<n;c++)(a[k,c],a[p,c])=(a[p,c],a[k,c]);var d=a[k,k];for(var c=k;c<n;c++)a[k,c]/=d;for(var r=k+1;r<n;r++){var f=a[r,k];for(var c=k;c<n;c++)a[r,c]-=f*a[k,c];}}}
    private static double[] Solve(double[,] a,double[] b){var n=b.Length;for(var k=0;k<n;k++){var p=k;for(var r=k+1;r<n;r++)if(Math.Abs(a[r,k])>Math.Abs(a[p,k]))p=r;if(Math.Abs(a[p,k])<1e-14)throw new InvalidOperationException();for(var c=k;c<n;c++)(a[k,c],a[p,c])=(a[p,c],a[k,c]);(b[k],b[p])=(b[p],b[k]);var d=a[k,k];for(var c=k;c<n;c++)a[k,c]/=d;b[k]/=d;for(var r=0;r<n;r++)if(r!=k){var f=a[r,k];for(var c=k;c<n;c++)a[r,c]-=f*a[k,c];b[r]-=f*b[k];}}return b;}
}
