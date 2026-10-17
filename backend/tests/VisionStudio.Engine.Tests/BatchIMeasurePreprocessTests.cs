using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
namespace VisionStudio.Engine.Tests;
public sealed class BatchIMeasurePreprocessTests
{
    private static NodeDefinition Def(string type, string json="{}") => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string,VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    [Fact] public async Task PointLineDistance_SignedAndSegment()
    {
        var line=new VisionLine2D(new(0,0),new(10,0));
        var r=await new PointLineDistanceNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(5,3))},{"line",VisionValue.Line(line)}}),Def("measure.pointLineDistance"),CancellationToken.None);
        Assert.Equal(3d,(double)r.Outputs["distance"].Value!,6); Assert.True((bool)r.Outputs["withinSegment"].Value!);
        var below=await new PointLineDistanceNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(5,-3))},{"line",VisionValue.Line(line)}}),Def("measure.pointLineDistance"),CancellationToken.None);
        Assert.True((double)below.Outputs["signedDistance"].Value! != 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PointLineDistanceNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(5,3))},{"line",VisionValue.Line(new(new(1,1),new(1,1)))}}),Def("measure.pointLineDistance"),CancellationToken.None));
    }
    [Fact] public async Task CircleSpacing_Relations()
    {
        var a=new VisionCircle(0,0,5);
        var sep=await new CircleSpacingNode().ExecuteAsync(Ctx(new(){{"circleA",VisionValue.Circle(a)},{"circleB",VisionValue.Circle(new(20,0,5))}}),Def("measure.circleSpacing"),CancellationToken.None);
        Assert.Equal("separate",(string)sep.Outputs["relation"].Value!); Assert.Equal(10d,(double)sep.Outputs["gap"].Value!,6);
        var cont=await new CircleSpacingNode().ExecuteAsync(Ctx(new(){{"circleA",VisionValue.Circle(new(0,0,10))},{"circleB",VisionValue.Circle(new(1,0,2))}}),Def("measure.circleSpacing"),CancellationToken.None);
        Assert.Equal("contained",(string)cont.Outputs["relation"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new CircleSpacingNode().ExecuteAsync(Ctx(new(){{"circleA",VisionValue.Circle(new(0,0,-1))},{"circleB",VisionValue.Circle(a)}}),Def("measure.circleSpacing"),CancellationToken.None));
    }
    [Fact] public async Task FitNodes_RmsGateAndCounts()
    {
        var inputs=new Dictionary<string,VisionValue>();
        for(var i=0;i<6;i++) inputs[$"point{i+1}"]=VisionValue.Point(new(i*10,i*10));
        var line=await new FitLineNode().ExecuteAsync(Ctx(inputs),Def("measure.fitLine"),CancellationToken.None);
        Assert.Equal(6L,(long)line.Outputs["pointCount"].Value!);
        var noisy=new Dictionary<string,VisionValue>();
        for(var i=0;i<6;i++) noisy[$"point{i+1}"]=VisionValue.Point(new(i*10,i*10+(i%2==0?2:-2)));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new FitLineNode().ExecuteAsync(Ctx(noisy),Def("measure.fitLine","{\"maxRmsError\":0.001}"),CancellationToken.None));
        var circleInputs=new Dictionary<string,VisionValue>();
        for(var i=0;i<8;i++){var ang=i*Math.PI/4; circleInputs[$"point{i+1}"]=VisionValue.Point(new(50+20*Math.Cos(ang),50+20*Math.Sin(ang)));}
        var circle=await new FitCircleNode().ExecuteAsync(Ctx(circleInputs),Def("measure.fitCircle"),CancellationToken.None);
        Assert.Equal(20d,(double)circle.Outputs["radius"].Value!,3); Assert.Equal(8L,(long)circle.Outputs["pointCount"].Value!);
    }
    [Fact] public async Task ToleranceNodes_Verdicts()
    {
        var conc=await new ConcentricityNode().ExecuteAsync(Ctx(new(){{"circleA",VisionValue.Circle(new(0,0,10))},{"circleB",VisionValue.Circle(new(3,4,10))}}),Def("measure.concentricity","{\"maxCenterDistance\":4}"),CancellationToken.None);
        Assert.Equal(5d,(double)conc.Outputs["centerDistance"].Value!,6); Assert.False((bool)conc.Outputs["withinTolerance"].Value!);
        var h=new VisionLine2D(new(0,0),new(10,0)); var v=new VisionLine2D(new(0,0),new(0,10));
        var perp=await new PerpendicularityNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Line(h)},{"b",VisionValue.Line(v)}}),Def("measure.perpendicularity","{\"maxAngleErrorDeg\":1}"),CancellationToken.None);
        Assert.True((bool)perp.Outputs["withinTolerance"].Value!); Assert.Equal(90d,(double)perp.Outputs["measuredAngleDeg"].Value!,6);
        var par=await new ParallelismNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Line(h)},{"b",VisionValue.Line(v)}}),Def("measure.parallelism"),CancellationToken.None);
        Assert.Equal(90d,(double)par.Outputs["angleError"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ParallelismNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Line(h)},{"b",VisionValue.Line(v)}}),Def("measure.parallelism","{\"maxAngleErrorDeg\":100}"),CancellationToken.None));
    }
    [Fact] public async Task Preprocess_StrictModesAndDiagnostics()
    {
        using var color=new Mat(10,10,MatType.CV_8UC3,new Scalar(10,100,200));
        var gray=await new GrayscaleNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.grayscale","{\"mode\":\"luminance\"}"),CancellationToken.None);
        Assert.Contains("image.grayscale",(string)gray.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new GrayscaleNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.grayscale","{\"mode\":\"bogus\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ChannelExtractNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.channel","{\"channel\":\"bogus\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new GaussianBlurNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.blur.gaussian","{\"sigmaX\":-1}"),CancellationToken.None));
        var median=await new MedianBlurNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.blur.median","{\"kernelSize\":4}"),CancellationToken.None);
        Assert.Contains("\"kernelSize\":5",(string)median.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new AdaptiveThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(color)}}),Def("image.threshold.adaptive","{\"method\":\"bogus\"}"),CancellationToken.None));
    }
    [Fact] public async Task OtsuResizeRotateRotatedRect_Diagnostics()
    {
        using var half=new Mat(10,20,MatType.CV_8UC1,Scalar.All(0));
        half[new Rect(0,0,10,10)]=new Mat(10,10,MatType.CV_8UC1,Scalar.All(255));
        var otsu=await new OtsuThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(half)}}),Def("image.threshold.otsu"),CancellationToken.None);
        Assert.Equal(0.5,(double)otsu.Outputs["foregroundRatio"].Value!,3);
        var resize=await new ImageResizeNode().ExecuteAsync(Ctx(new(){{"image",Img(half)}}),Def("image.resize","{\"width\":40,\"height\":20,\"interpolation\":\"nearest\"}"),CancellationToken.None);
        Assert.Equal(40L,(long)resize.Outputs["outputWidth"].Value!);
        var rotate=await new ImageRotateNode().ExecuteAsync(Ctx(new(){{"image",Img(half)}}),Def("image.rotate","{\"angleDeg\":90,\"expand\":true}"),CancellationToken.None);
        Assert.Contains("image.rotate",(string)rotate.Outputs["diagnostics"].Value!);
        using var rectImg=new Mat(40,40,MatType.CV_8UC1,Scalar.All(0));
        Cv2.Rectangle(rectImg,new Rect(10,10,20,10),Scalar.All(255),-1);
        var rr=await new RotatedRectangleNode().ExecuteAsync(Ctx(new(){{"image",Img(rectImg)}}),Def("measure.rotatedRect","{\"minArea\":100}"),CancellationToken.None);
        Assert.True((double)rr.Outputs["width"].Value! >= 19); Assert.True((double)rr.Outputs["aspectRatio"].Value! > 1.5);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RotatedRectangleNode().ExecuteAsync(Ctx(new(){{"image",Img(rectImg)}}),Def("measure.rotatedRect","{\"minArea\":0}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RotatedRectangleNode().ExecuteAsync(Ctx(new(){{"image",Img(rectImg)}}),Def("measure.rotatedRect","{\"minArea\":100000}"),CancellationToken.None));
    }
}
