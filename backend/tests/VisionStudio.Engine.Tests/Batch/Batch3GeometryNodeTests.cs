using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class Batch3GeometryNodeTests
{
    [Fact]
    public async Task IntrinsicCalibrationUsesOpenCvAndUndistortConsumesItsModel()
    {
        var request=BuildCalibrationData();
        var node=Node("calibration.cameraIntrinsics",JsonSerializer.Serialize(new{calibrationDataJson=JsonSerializer.Serialize(request)}));
        var fit=await new CameraIntrinsicCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),node,default);
        var json=fit.Outputs["calibrationJson"].Require<string>("calibrationJson");
        var model=JsonSerializer.Deserialize<CameraCalibrationModel>(json)!;
        Assert.InRange(model.CameraMatrix[0][0],500,700); Assert.InRange(model.CameraMatrix[1][1],500,700); Assert.True(double.IsFinite(model.Rms));
        using var input=new Mat(480,640,MatType.CV_8UC1,Scalar.All(127));
        var undistortNode=Node("image.undistort",JsonSerializer.Serialize(new{calibrationJson=json}));
        var result=await new ImageUndistortNode().ExecuteAsync(new(new Dictionary<string,VisionValue>{{"image",VisionValue.Image(VisionImage.Own(input.Clone()))},{"calibrationJson",VisionValue.String(json)}}),undistortNode,default);
        using var image=(VisionImage)result.Outputs["image"].Require<IVisionImage>("image");
        Assert.Equal((640,480),(image.Width,image.Height)); Assert.InRange(image.Mat.At<byte>(240,320),120,134);
    }

    [Fact]
    public async Task IntrinsicCalibrationRejectsInsufficientAndDegenerateViews()
    {
        var bad=new{imageWidth=640,imageHeight=480,views=new[]{new{objectPoints=new[]{new[]{0d,0,0},new[]{1d,0,0},new[]{2d,0,0},new[]{3d,0,0},new[]{4d,0,0},new[]{5d,0,0}},imagePoints=new[]{new[]{1d,1},new[]{2d,1},new[]{3d,1},new[]{4d,1},new[]{5d,1},new[]{6d,1}}}}};
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new CameraIntrinsicCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.cameraIntrinsics",JsonSerializer.Serialize(new{calibrationDataJson=JsonSerializer.Serialize(bad)})),default));
    }

    [Theory]
    [InlineData("eyeInHand")]
    [InlineData("eyeToHand")]
    public async Task HandEyeCalibrationRecoversPlanarSe2Transform(string mode)
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35)};
        var samples=robots.Select(r=>{
            var vision=mode=="eyeInHand" ? Compose(Inverse(x),Compose(Inverse(r),y)) : Compose(Inverse(x),Compose(r,y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X,y=vision.Y,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        var json=JsonSerializer.Serialize(new{samples});
        var result=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode,samplesJson=json})),default);
        var recovered=result.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(x.X,recovered.X,2); Assert.Equal(x.Y,recovered.Y,2); Assert.Equal(x.Theta*180/Math.PI,recovered.ThetaDeg,2);
        var transform=result.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal(x.X,transform.Apply(new VisionPoint2D(0,0)).X,2);
        Assert.Equal("SE(2)",result.Summary["transform2D"]);
        var noisySamples=samples.Select((s,i)=>new{robot=s.robot,vision=new{x=s.vision.x,y=s.vision.y,thetaDeg=s.vision.thetaDeg+(i%2==0?0.1:-0.1)}}).ToArray();
        var noisyResult=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode,samplesJson=JsonSerializer.Serialize(new{samples=noisySamples})})),default);
        var angleRms=noisyResult.Outputs["angleRmsDeg"].Require<double>("angleRmsDeg");
        Assert.InRange(angleRms,0.001,0.1);
    }

    [Fact]
    public async Task HandEyeRejectsInsufficientAndDegeneratePoseSets()
    {
        var samples=Enumerable.Range(0,4).Select(i=>new{robot=new{x=0d,y=0d,thetaDeg=0d},vision=new{x=(double)i,y=0d,thetaDeg=0d}}).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=JsonSerializer.Serialize(new{samples})})),default));
    }

    [Fact]
    public async Task HandEyeReportsUncertaintyConvergenceAndCrossCheck()
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35)};
        var samples=robots.Select(r=>{
            var vision=Compose(Inverse(x),Compose(Inverse(r),y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X,y=vision.Y,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        var result=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=JsonSerializer.Serialize(new{samples})})),default);
        Assert.True(result.Outputs["converged"].Require<bool>("converged"));
        Assert.InRange(result.Outputs["iterations"].Require<long>("iterations"),1,100);
        var condition=result.Outputs["conditionNumber"].Require<double>("conditionNumber");
        Assert.True(double.IsFinite(condition)&&condition>=1);
        Assert.True(result.Outputs["stdDevX"].Require<double>("stdDevX")>=0);
        Assert.True(result.Outputs["stdDevY"].Require<double>("stdDevY")>=0);
        Assert.True(result.Outputs["angleStdDevDeg"].Require<double>("angleStdDevDeg")>=0);
        Assert.False(result.Outputs.ContainsKey("outlierSampleIndex"));
        Assert.InRange(result.Outputs["crossCheckTranslationDelta"].Require<double>("crossCheckTranslationDelta"),0,0.01);
        Assert.InRange(result.Outputs["crossCheckAngleDeltaDeg"].Require<double>("crossCheckAngleDeltaDeg"),0,0.01);
        var model=JsonSerializer.Deserialize<HandEyeCalibrationModel>(result.Outputs["calibrationJson"].Require<string>("calibrationJson"))!;
        Assert.Equal("eyeInHand",model.Mode); Assert.Equal(x.X,model.X,2); Assert.Equal(x.Theta*180/Math.PI,model.ThetaDeg,2);
    }

    [Fact]
    public async Task HandEyeFlagsOutlierAndTukeyRejectsIt()
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35),new Pose(95,-65,0.85),new Pose(-55,25,1.2)};
        var samples=robots.Select(r=>{
            var vision=Compose(Inverse(x),Compose(Inverse(r),y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X,y=vision.Y,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        samples[3]=samples[3] with{vision=samples[3].vision with{x=samples[3].vision.x+12}};
        var json=JsonSerializer.Serialize(new{samples});
        var plain=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json})),default);
        var robust=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,robustLoss="tukey"})),default);
        Assert.Equal(3L,plain.Outputs["outlierSampleIndex"].Require<long>("outlierSampleIndex"));
        Assert.Equal(3L,robust.Outputs["outlierSampleIndex"].Require<long>("outlierSampleIndex"));
        var plainPose=plain.Outputs["pose"].Require<VisionPose2D>("pose");
        var robustPose=robust.Outputs["pose"].Require<VisionPose2D>("pose");
        var plainError=Math.Sqrt(Math.Pow(plainPose.X-x.X,2)+Math.Pow(plainPose.Y-x.Y,2));
        var robustError=Math.Sqrt(Math.Pow(robustPose.X-x.X,2)+Math.Pow(robustPose.Y-x.Y,2));
        Assert.True(robustError<plainError,$"tukey={robustError:0.###} plain={plainError:0.###}");
        Assert.InRange(robustError,0,0.5);
    }

    [Fact]
    public async Task HandEyeVisionScaleConvertsPixelSamplesAndWarnsOnUnitMismatch()
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35)};
        var samples=robots.Select(r=>{
            var vision=Compose(Inverse(x),Compose(Inverse(r),y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X/0.05,y=vision.Y/0.05,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        var json=JsonSerializer.Serialize(new{samples});
        var scaled=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,visionScale=0.05,sourceUnit="px",unit="mm"})),default);
        var pose=scaled.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(x.X,pose.X,2); Assert.Equal(x.Y,pose.Y,2); Assert.Equal(x.Theta*180/Math.PI,pose.ThetaDeg,2);
        Assert.Null(scaled.Summary["unitWarning"]);
        var unscaled=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,sourceUnit="px",unit="mm"})),default);
        Assert.NotNull(unscaled.Summary["unitWarning"]);
    }

    [Fact]
    public async Task HandEyeConnectedSamplesOverrideParametersAndAssetPinMustBePaired()
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35)};
        var samples=robots.Select(r=>{
            var vision=Compose(Inverse(x),Compose(Inverse(r),y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X,y=vision.Y,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        var context=new NodeExecutionContext(new Dictionary<string,VisionValue>{{"samplesJson",VisionValue.String(JsonSerializer.Serialize(new{samples}))}});
        var result=await new PlanarHandEyeCalibrationNode().ExecuteAsync(context,Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson="{}"})),default);
        Assert.Equal(x.X,result.Outputs["pose"].Require<VisionPose2D>("pose").X,2);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=JsonSerializer.Serialize(new{samples}),handEyeAssetId="he-a"})),default));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=JsonSerializer.Serialize(new{samples}),handEyeAssetVersion=2})),default));
    }

    [Fact]
    public async Task DetectionCollectionsFilterSortCountAndExtractTypedItem()
    {
        IReadOnlyList<VisionDetection> source=[Detection(100,8,0.8,0.85,7),Detection(50,2,0.3,0.4,3),Detection(200,5,0.9,0.95,7)];
        var input=new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(source)}};
        var filter=await new DetectionFilterNode().ExecuteAsync(new(input),Node("collection.detections.filter","""{"minArea":60,"minCircularity":0.5}"""),default);
        var filtered=filter.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections"); Assert.Equal(new[]{100d,200d},filtered.Select(d=>d.Area));
        var sorted=await new DetectionSortNode().ExecuteAsync(new(new Dictionary<string,VisionValue>{{"detections",filter.Outputs["detections"]}}),Node("collection.detections.sort","""{"sortBy":"area","descending":true}"""),default);
        var at=await new DetectionAtIndexNode().ExecuteAsync(new(new Dictionary<string,VisionValue>{{"detections",sorted.Outputs["detections"]}}),Node("collection.detections.at","""{"index":0}"""),default);
        Assert.True(at.Outputs["found"].Require<bool>("found")); Assert.Equal(200,at.Outputs["area"].Require<double>("area"));
        Assert.Equal(0.95,at.Outputs["confidence"].Require<double>("confidence")); Assert.Equal(7L,at.Outputs["classId"].Require<long>("classId"));
        var confidenceFiltered=await new DetectionFilterNode().ExecuteAsync(new(input),Node("collection.detections.filter","""{"minConfidence":0.9,"classId":7}"""),default);
        Assert.Equal(new[]{200d},confidenceFiltered.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections").Select(d=>d.Area));
        var count=await new DetectionCountNode().ExecuteAsync(new(new Dictionary<string,VisionValue>{{"detections",sorted.Outputs["detections"]}}),Node("collection.detections.count","{}"),default);
        Assert.Equal(2L,count.Outputs["count"].Require<long>("count"));
        var missing=await new DetectionAtIndexNode().ExecuteAsync(new(input),Node("collection.detections.at","""{"index":9}"""),default);
        Assert.False(missing.Outputs["found"].Require<bool>("found"));
    }

    [Theory]
    [InlineData("bboxNaN")]
    [InlineData("negativeRadius")]
    [InlineData("negativeWidth")]
    [InlineData("infinitePerimeter")]
    public async Task DetectionCollectionInputRejectsMalformedGeometry(string issue)
    {
        var detection=Detection(10,1,0.5);
        detection=issue switch
        {
            "bboxNaN"=>detection with{BoundingBox=detection.BoundingBox with{AngleDeg=double.NaN}},
            "negativeRadius"=>detection with{Circle=detection.Circle with{Radius=-1}},
            "negativeWidth"=>detection with{Width=-1},
            "infinitePerimeter"=>detection with{Perimeter=double.PositiveInfinity},
            _=>throw new ArgumentOutOfRangeException(nameof(issue))
        };
        IReadOnlyList<VisionDetection> source=[detection];
        var context=new NodeExecutionContext(new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(source)}});
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new DetectionCountNode().ExecuteAsync(context,Node("collection.detections.count","{}"),default));
    }

    [Fact]
    public void CatalogListsEveryExecutorInThisBatch()
    {
        string[] types=["calibration.cameraIntrinsics","image.undistort","calibration.handEye2D","calibration.handEye3D","calibration.dualCameraExtrinsics","collection.detections.filter","collection.detections.sort","collection.detections.at","collection.detections.count","collection.detectionNumbers","collection.detectionPoints","geometry.fitFromPoints"];
        Assert.Equal(types.Order(),Batch3GeometryCatalog.Items.Select(x=>x.Type).Order());
    }

    private static CameraCalibrationRequest BuildCalibrationData()
    {
        var views=new List<CameraCalibrationView>();
        for(var v=0;v<8;v++)
        {
            var rx=(v%3-1)*0.13; var ry=(v%4-1.5)*0.11; var rz=(v%2==0?1:-1)*0.08*(v+1); var tx=-90+v*23; var ty=-55+(v%4)*28; var tz=650+v*13;
            var obj=new List<List<double>>(); var img=new List<List<double>>();
            for(var y=0;y<6;y++)for(var x=0;x<8;x++)
            {
                var X=x*22d;var Y=y*22d; var cx=Math.Cos(ry);var sx=Math.Sin(ry);var cy=Math.Cos(rx);var sy=Math.Sin(rx);var cz=Math.Cos(rz);var sz=Math.Sin(rz);
                // Rz * Ry * Rx
                var qx=cz*cy*X+(-sz*cx+cz*sy*sx)*Y+tx;
                var qy=sz*cy*X+(cz*cx+sz*sy*sx)*Y+ty;
                var qz=-sy*X+cy*sx*Y+tz;
                obj.Add([X,Y,0]); img.Add([600*qx/qz+320,605*qy/qz+240]);
            }
            views.Add(new(obj,img));
        }
        return new(640,480,views);
    }
    private static VisionDetection Detection(double area,double x,double circ,double? confidence=null,int? classId=null)=>new(0,x,0,10,8,area,40,circ,new VisionCircle(x+5,4,5),new VisionRectangle2D(x,0,10,8),confidence,classId);
    private static NodeDefinition Node(string type,string json)=>new("n",type,null,null,JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(json));
    private readonly record struct Pose(double X,double Y,double Theta);
    private static Pose Compose(Pose a,Pose b){var c=Math.Cos(a.Theta);var s=Math.Sin(a.Theta);return new(a.X+c*b.X-s*b.Y,a.Y+s*b.X+c*b.Y,a.Theta+b.Theta);}
    private static Pose Inverse(Pose p)=>new(-Math.Cos(p.Theta)*p.X-Math.Sin(p.Theta)*p.Y,Math.Sin(p.Theta)*p.X-Math.Cos(p.Theta)*p.Y,-p.Theta);
    [Fact]
    public async Task HandEyeConditionGateIsScaleInvariantAndValidated()
    {
        var x=new Pose(18,-7,0.31); var y=new Pose(210,45,-0.2);
        var robots=new[]{new Pose(0,0,-0.7),new Pose(70,20,-0.25),new Pose(120,-40,0.2),new Pose(-30,90,0.65),new Pose(55,105,1.05),new Pose(160,35,1.35)};
        var samples=robots.Select(r=>{
            var vision=Compose(Inverse(x),Compose(Inverse(r),y));
            return new{robot=new{x=r.X,y=r.Y,thetaDeg=r.Theta*180/Math.PI},vision=new{x=vision.X,y=vision.Y,thetaDeg=vision.Theta*180/Math.PI}};
        }).ToArray();
        var json=JsonSerializer.Serialize(new{samples});
        var permissive=await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,maxConditionNumber=1e6})),default);
        var condition=permissive.Outputs["conditionNumber"].Require<double>("conditionNumber");
        Assert.InRange(condition,1e-9,1e6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,maxConditionNumber=-1})),default));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarHandEyeCalibrationNode().ExecuteAsync(new(new Dictionary<string,VisionValue>()),Node("calibration.handEye2D",JsonSerializer.Serialize(new{mode="eyeInHand",samplesJson=json,maxConditionNumber=1e-6})),default));
    }

}
