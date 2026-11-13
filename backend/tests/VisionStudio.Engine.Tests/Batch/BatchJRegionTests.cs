using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
namespace VisionStudio.Engine.Tests;
public sealed class BatchJRegionTests
{
    private static NodeDefinition Def(string type, string json="{}") => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string,VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    [Fact] public async Task RegionFromThreshold_EmitsDiagnostics()
    {
        using var img=new Mat(20,20,MatType.CV_8UC1,Scalar.All(0));
        Cv2.Rectangle(img,new Rect(5,5,10,10),Scalar.All(200),-1);
        var r=await new RegionFromThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("region.fromThreshold","{\"minGray\":100,\"maxGray\":255}"),CancellationToken.None);
        Assert.True((bool)r.Outputs["found"].Value!); Assert.Equal(100d,(double)r.Outputs["area"].Value!,3);
        Assert.Contains("componentCount",(string)r.Outputs["diagnostics"].Value!);
        var empty=await new RegionFromThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("region.fromThreshold","{\"minGray\":201,\"maxGray\":255}"),CancellationToken.None);
        // 200-valued block is below 201, so no region
        Assert.False((bool)empty.Outputs["found"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RegionFromThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("region.fromThreshold","{\"minGray\":200,\"maxGray\":100}"),CancellationToken.None));
    }
    [Fact] public async Task RegionBooleanAndFillHoles_Diagnostics()
    {
        using var img=new Mat(20,20,MatType.CV_8UC1,Scalar.All(0));
        Cv2.Rectangle(img,new Rect(2,2,10,10),Scalar.All(255),-1);
        var a=await new RegionFromThresholdNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("region.fromThreshold","{\"minGray\":100}"),CancellationToken.None);
        var region=(VisionRegion)a.Outputs["region"].Value!;
        var union=await new RegionBooleanNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Region(region)},{"b",VisionValue.Region(region)}}),Def("region.boolean","{\"operation\":\"union\"}"),CancellationToken.None);
        Assert.Contains("diagnostics",union.Outputs.Keys);
        var filled=await new RegionFillHolesNode().ExecuteAsync(Ctx(new(){{"region",VisionValue.Region(region)}}),Def("region.fillHoles"),CancellationToken.None);
        Assert.True((bool)filled.Outputs["found"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RegionBooleanNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Region(region)},{"b",VisionValue.Region(region)}}),Def("region.boolean","{\"operation\":\"bogus\"}"),CancellationToken.None));
    }
    [Fact] public async Task TopHatAndHistogram_Diagnostics()
    {
        using var img=new Mat(30,30,MatType.CV_8UC1,Scalar.All(10));
        Cv2.Rectangle(img,new Rect(12,12,5,5),Scalar.All(200),-1);
        var top=await new ImageTopHatNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("image.topHat","{\"kernelWidth\":15,\"kernelHeight\":15}"),CancellationToken.None);
        Assert.Contains("topHat",(string)top.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ImageTopHatNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("image.topHat","{\"kernelWidth\":4,\"kernelHeight\":15}"),CancellationToken.None));
        var hist=await new GrayHistogramNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("measure.grayHistogram"),CancellationToken.None);
        Assert.True((bool)hist.Outputs["found"].Value!); Assert.Contains("measure.grayHistogram",(string)hist.Outputs["diagnostics"].Value!);
        var morph=await new ImageMorphGradientNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("image.morphGradient","{\"kernelWidth\":3,\"kernelHeight\":3}"),CancellationToken.None);
        Assert.Contains("gradient",(string)morph.Outputs["diagnostics"].Value!);
        var bottom=await new ImageBottomHatNode().ExecuteAsync(Ctx(new(){{"image",Img(img)}}),Def("image.bottomHat","{\"kernelWidth\":15,\"kernelHeight\":15}"),CancellationToken.None);
        Assert.Contains("bottomHat",(string)bottom.Outputs["diagnostics"].Value!);
    }
}
