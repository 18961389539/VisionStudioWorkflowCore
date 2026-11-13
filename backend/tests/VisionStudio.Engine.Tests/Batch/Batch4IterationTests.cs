using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch4IterationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task ForEachRunsBodyExactlyOncePerItemAndRunsDoneRoute(int count)
    {
        await using var provider=Provider();
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(count));
        Assert.True(result.Success,result.Error);
        var body=result.NodeReports.Where(r=>r.NodeId=="body").ToArray();
        Assert.Equal(count,body.Length);
        Assert.Equal(count,body.Select(r=>r.ExecutionSequence).Distinct().Count());
        Assert.Contains(result.NodeReports,r=>r.NodeId=="after");
        Assert.Equal(count,result.NodeReports.Count(r=>r.NodeId=="loopEnd"));
        if(count==0) Assert.DoesNotContain(result.NodeReports,r=>r.NodeId=="body");
        else
        {
            Assert.Equal(Enumerable.Range(0,count).Select(x=>(double)(x+1)),body.Select(r=>Convert.ToDouble(r.Summary["x"])));
            Assert.All(body,r=>Assert.Equal(0.85,Convert.ToDouble(r.Summary["confidence"])));
            Assert.All(body,r=>Assert.Equal(9L,Convert.ToInt64(r.Summary["classId"])));
        }
    }

    [Fact]
    public async Task ForEachRejectsMoreItemsThanConfiguredBound()
    {
        await using var provider=Provider();
        var workflow=Workflow(3) with { Nodes=Workflow(3).Nodes.Select(n=>n.Id=="loop"?n with{Parameters=Params("{\"maxItems\":2}")}:n).ToArray() };
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow);
        Assert.False(result.Success); Assert.Contains("exceeding maxItems=2",result.Error,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.NodeReports,r=>r.NodeId=="body");
    }

    [Fact]
    public async Task NestedForEachRunsSequentiallyAndReportsEveryIteration()
    {
        await using var provider=Provider();
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(NestedWorkflow());
        Assert.True(result.Success,result.Error);
        Assert.Equal(2,result.NodeReports.Count(r=>r.NodeId=="innerSource"));
        Assert.Equal(4,result.NodeReports.Count(r=>r.NodeId=="body"));
        Assert.Equal(4,result.NodeReports.Count(r=>r.NodeId=="innerEnd"));
        Assert.Equal(2,result.NodeReports.Count(r=>r.NodeId=="outerEnd"));
        Assert.Equal(result.NodeReports.Select(r=>r.ExecutionSequence).Order(),result.NodeReports.Select(r=>r.ExecutionSequence));
    }

    [Fact]
    public void ParallelInsideForEachKeepsItemBindingAndAvoidsPipelineFusion()
    {
        var compiled=new VisionWorkflowCompiler(Registry()).Compile(ForEachWithParallel());
        Assert.DoesNotContain(compiled.PipelineSegments.Keys,id=>id.Contains("branch",StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LoopNodeId",compiled.DslJson,StringComparison.Ordinal);
        Assert.Contains("\"loop\"",compiled.DslJson,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DebugRunMode.RunNode)]
    [InlineData(DebugRunMode.RunFromNode)]
    [InlineData(DebugRunMode.Breakpoints)]
    public async Task NodeDebugModesRejectIterationBeforeRunningAnyNode(DebugRunMode mode)
    {
        await using var provider=Provider();
        var options=new VisionRunOptions(mode,TargetNodeId:"body",Breakpoints:mode==DebugRunMode.Breakpoints?["body"]:null);
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(3),options);
        Assert.False(result.Success);
        Assert.Empty(result.NodeReports);
        Assert.Contains("flow.forEach",result.Error,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BreakpointSessionRejectsIterationBeforeStarting()
    {
        await using var provider=Provider();
        var service=provider.GetRequiredService<WorkflowDebugSessionService>();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.StartAsync(Workflow(3),new VisionRunOptions(DebugRunMode.Breakpoints,Breakpoints:["body"])));
    }

    [Theory]
    [InlineData(3,true,"NG")]
    [InlineData(3,false,"OK")]
    [InlineData(0,true,null)]
    public async Task LoopResultsAggregateAnyNgAcrossItemsWithoutEmptyContribution(int count,bool failFirst,string? expected)
    {
        await using var provider=Provider();
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(ResultLoopWorkflow(count,failFirst));
        Assert.True(result.Success,result.Error);
        Assert.Equal(expected,result.QualityDisposition);
    }

    [Fact]
    public async Task CancellationInsideLoopStopsRemainingItems()
    {
        await using var provider=Provider(); using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var workflow=Workflow(10) with{Nodes=Workflow(10).Nodes.Select(n=>n.Id=="body"?n with{Parameters=Params("{\"delayMs\":5000}")}:n).ToArray()};
        var result=await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow,cancellationToken:cts.Token);
        Assert.False(result.Success); Assert.Equal(VisionRunErrorCodes.Cancelled,result.ErrorCode);
        Assert.True(result.NodeReports.Count(r=>r.NodeId=="body")<10);
    }

    [Fact]
    public void ForEachRejectsUnpairedLoopEnd()
    {
        var workflow=Workflow(2); var nodes=workflow.Nodes.Where(n=>n.Id!="loopEnd").ToArray();
        var noEnd=workflow with{Nodes=nodes,Edges=workflow.Edges.Where(e=>e.Id!="body-end"&&e.Id!="end-loop").ToArray()};
        Assert.Throws<InvalidOperationException>(()=>new VisionWorkflowCompiler(Registry()).Compile(noEnd));
    }

    [Theory]
    [InlineData("center")]
    [InlineData("confidence")]
    [InlineData("classId")]
    public void ForEachRejectsDynamicGeometryOutsideItsBody(string port)
    {
        var workflow=Workflow(2);
        var nodes=workflow.Nodes.Select(n=>n.Id=="after"?Node("after","test.dynamicSink"):n).ToArray();
        var edges=workflow.Edges.Append(D("external-output","loop",port,"after",port)).ToArray();
        var error=Assert.Throws<InvalidOperationException>(()=>new VisionWorkflowCompiler(Registry()).Compile(workflow with{Nodes=nodes,Edges=edges}));
        Assert.Contains($"dynamic output '{port}'",error.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuredWorkflowRejectsAnArbitraryControlCycle()
    {
        var nodes=new[]{Node("source","test.detections"),Node("mark","test.mark")};
        var edges=new[]{C("to-mark","source","next","mark","exec"),C("to-source","mark","next","source","exec")};
        Assert.Throws<InvalidOperationException>(()=>new VisionWorkflowCompiler(Registry()).Compile(new WorkflowDefinition("cycle","Cycle",nodes,edges)));
    }

    [Fact]
    public void CatalogDeclaresForEachAndLoopEnd()
    {
        Assert.Equal(new[]{"flow.forEach","flow.loopEnd","flow.while","flow.repeat","flow.break"}.Order(),Batch4IterationCatalog.Items.Select(x=>x.Type).Order());
    }

    private static WorkflowDefinition Workflow(int count)=>new("foreach-"+count,"ForEach",
        [Node("source","test.detections",$"{{\"count\":{count}}}"),Node("loop","flow.forEach"),Node("body","test.capture"),Node("loopEnd","flow.loopEnd"),Node("after","test.mark")],
        [C("source-loop","source","next","loop","exec"),D("detections","source","detections","loop","detections"),C("loop-body","loop","body","body","exec"),D("target","loop","center","body","center"),D("confidence","loop","confidence","body","confidence"),D("class-id","loop","classId","body","classId"),C("body-end","body","next","loopEnd","exec"),C("end-loop","loopEnd","repeat","loop","repeat"),C("loop-after","loop","done","after","exec")]);

    private static WorkflowDefinition NestedWorkflow()=>new("foreach-nested","Nested ForEach",
        [Node("rootSource","test.detections","{\"count\":2}"),Node("outer","flow.forEach"),Node("innerSource","test.detections","{\"count\":2}"),Node("inner","flow.forEach"),Node("body","test.capture"),Node("innerEnd","flow.loopEnd"),Node("outerEnd","flow.loopEnd"),Node("after","test.mark")],
        [C("root-outer","rootSource","next","outer","exec"),D("root-list","rootSource","detections","outer","detections"),C("outer-body","outer","body","innerSource","exec"),C("inner-start","innerSource","next","inner","exec"),D("inner-list","innerSource","detections","inner","detections"),C("inner-body","inner","body","body","exec"),D("target","inner","center","body","center"),D("confidence","inner","confidence","body","confidence"),D("class-id","inner","classId","body","classId"),C("body-inner-end","body","next","innerEnd","exec"),C("inner-back","innerEnd","repeat","inner","repeat"),C("inner-done","inner","done","outerEnd","exec"),C("outer-back","outerEnd","repeat","outer","repeat"),C("outer-done","outer","done","after","exec")]);

    private static WorkflowDefinition ForEachWithParallel()=>new("foreach-parallel","ForEach Parallel",
        [Node("source","test.detections"),Node("loop","flow.forEach"),Node("parallel","flow.parallel"),Node("branchA","test.capture"),Node("branchB","test.capture"),Node("join","flow.join"),Node("loopEnd","flow.loopEnd"),Node("after","test.mark")],
        [C("source-loop","source","next","loop","exec"),D("source-list","source","detections","loop","detections"),C("loop-body","loop","body","parallel","exec"),C("parallel-a","parallel","branch1","branchA","exec"),C("parallel-b","parallel","branch2","branchB","exec"),D("target-a","loop","center","branchA","center"),D("target-b","loop","center","branchB","center"),D("confidence-a","loop","confidence","branchA","confidence"),D("confidence-b","loop","confidence","branchB","confidence"),D("class-a","loop","classId","branchA","classId"),D("class-b","loop","classId","branchB","classId"),C("a-join","branchA","next","join","branch1"),C("b-join","branchB","next","join","branch2"),C("join-end","join","next","loopEnd","exec"),C("end-loop","loopEnd","repeat","loop","repeat"),C("loop-after","loop","done","after","exec")]);

    private static WorkflowDefinition ResultLoopWorkflow(int count,bool failFirst)=>new("foreach-result","ForEach Result",
        [Node("source","test.detections",$"{{\"count\":{count}}}"),Node("loop","flow.forEach"),Node("gate","test.passByIndex",$"{{\"firstFails\":{failFirst.ToString().ToLowerInvariant()}}}"),Node("result","flow.result"),Node("loopEnd","flow.loopEnd"),Node("after","test.mark")],
        [C("source-loop","source","next","loop","exec"),D("source-list","source","detections","loop","detections"),C("loop-body","loop","body","gate","exec"),D("current-index","loop","index","gate","index"),C("gate-result","gate","next","result","exec"),D("result-pass","gate","pass","result","pass"),C("result-end","result","next","loopEnd","exec"),C("end-loop","loopEnd","repeat","loop","repeat"),C("loop-after","loop","done","after","exec")]);

    private static ServiceProvider Provider()
    {
        var s=new ServiceCollection();s.AddLogging();s.AddWorkflow();s.AddWorkflowDSL();var registry=Registry();s.AddSingleton(registry);s.AddSingleton<VisionNodeDispatcher>();s.AddSingleton<VisionNodeRuntime>();s.AddSingleton<VisionPipelineExecutor>();s.AddSingleton<VisionWorkflowCompiler>();s.AddTransient<VisionNodeStep>();s.AddTransient<VisionPipelineStep>();s.AddSingleton<WorkflowPlanCache>();s.AddTransient<WorkflowCoreVisionRunner>();s.AddSingleton<WorkflowDebugSessionService>();return s.BuildServiceProvider();
    }
    private static VisionNodeRegistry Registry()
    {
        var r=new VisionNodeRegistry();r.Register(Batch4IterationCatalog.Items.Single(x=>x.Type=="flow.forEach"),new DetectionForEachNode(),"batch4");r.Register(Batch4IterationCatalog.Items.Single(x=>x.Type=="flow.loopEnd"),new LoopEndNode(),"batch4");r.Register(BuiltInNodeCatalog.Require("flow.parallel"),new ParallelMarkerNode(),"builtin");r.Register(BuiltInNodeCatalog.Require("flow.join"),new JoinNode(),"builtin");r.Register(BuiltInNodeCatalog.Require("flow.result"),new ResultDispositionNode(),"builtin");r.Register(SourceCatalog,new DetectionSource(),"test");r.Register(CaptureCatalog,new CaptureNode(),"test");r.Register(GateCatalog,new PassByIndexNode(),"test");r.Register(DynamicSinkCatalog,new DynamicSinkNode(),"test");r.Register(MarkCatalog,new MarkNode(),"test");return r;
    }
    private static NodeCatalogItem SourceCatalog {get;}=new("test.detections","Detection Source","Test",[new("exec",VisionDataType.Control)],[new("detections",VisionDataType.DetectionList),new("next",VisionDataType.Control)],[new("count","Count","number",3)],Capabilities:new VisionToolCapabilities(ExecutionMode:VisionExecutionMode.WorkflowCore));
    private static NodeCatalogItem CaptureCatalog {get;}=new("test.capture","Capture Target","Test",[new("exec",VisionDataType.Control),new("center",VisionDataType.Point2D),new("confidence",VisionDataType.Double,Required:false),new("classId",VisionDataType.Integer,Required:false)],[new("next",VisionDataType.Control)],[new("delayMs","Delay","number",0)],Capabilities:new VisionToolCapabilities(ExecutionMode:VisionExecutionMode.Pipeline));
    private static NodeCatalogItem MarkCatalog {get;}=new("test.mark","Mark","Test",[new("exec",VisionDataType.Control)],[new("next",VisionDataType.Control)],[],Capabilities:new VisionToolCapabilities(ExecutionMode:VisionExecutionMode.WorkflowCore));
    private static NodeCatalogItem GateCatalog {get;}=new("test.passByIndex","Pass By Index","Test",[new("exec",VisionDataType.Control),new("index",VisionDataType.Integer)],[new("pass",VisionDataType.Boolean),new("next",VisionDataType.Control)],[new("firstFails","First Index Fails","boolean",true)],Capabilities:new VisionToolCapabilities(ExecutionMode:VisionExecutionMode.WorkflowCore));
    private static NodeCatalogItem DynamicSinkCatalog {get;}=new("test.dynamicSink","Optional Dynamic Sink","Test",[new("exec",VisionDataType.Control),new("center",VisionDataType.Point2D,Required:false),new("confidence",VisionDataType.Double,Required:false),new("classId",VisionDataType.Integer,Required:false)],[new("next",VisionDataType.Control)],[],Capabilities:new VisionToolCapabilities(ExecutionMode:VisionExecutionMode.WorkflowCore));
    private sealed class DetectionSource:IVisionNodeExecutor
    {
        public string Type=>"test.detections";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext c,NodeDefinition n,CancellationToken ct){var count=n.GetInt("count",3);IReadOnlyList<VisionDetection> list=Enumerable.Range(0,count).Select(i=>new VisionDetection(i, i+1,0,1,1,10,4,1,new VisionCircle(i+1,0,1),new VisionRectangle2D(i+1,0,1,1),0.85,9)).ToArray();return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(list)}},new Dictionary<string,object?>{{"count",count}}));}
    }
    private sealed class CaptureNode:IVisionNodeExecutor
    {
        public string Type=>"test.capture";
        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext c,NodeDefinition n,CancellationToken ct){var p=c.Require<VisionPoint2D>("center");var confidence=c.Require<double>("confidence");var classId=c.Require<long>("classId");var delay=n.GetInt("delayMs",0);if(delay>0)await Task.Delay(delay,ct);return new NodeExecutorResult(new Dictionary<string,VisionValue>(),new Dictionary<string,object?>{{"x",p.X},{"y",p.Y},{"confidence",confidence},{"classId",classId}});}
    }
    private sealed class MarkNode:IVisionNodeExecutor
    {
        public string Type=>"test.mark";public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext c,NodeDefinition n,CancellationToken ct)=>ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>(),new Dictionary<string,object?>()));
    }
    private sealed class PassByIndexNode:IVisionNodeExecutor
    {
        public string Type=>"test.passByIndex";public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext c,NodeDefinition n,CancellationToken ct){var index=c.Require<long>("index");var pass=!n.GetBool("firstFails",true)||index!=0;return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"pass",VisionValue.Boolean(pass)}},new Dictionary<string,object?>{{"index",index},{"pass",pass}}));}
    }
    private sealed class DynamicSinkNode:IVisionNodeExecutor
    {
        public string Type=>"test.dynamicSink";public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext c,NodeDefinition n,CancellationToken ct)=>ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>(),new Dictionary<string,object?>()));
    }
    private static NodeDefinition Node(string id,string type,string? json=null)=>new(id,type,id,null,json is null?null:Params(json));
    private static Dictionary<string,JsonElement> Params(string json)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(json)!;
    private static EdgeDefinition C(string id,string source,string sourcePort,string target,string targetPort)=>new(id,source,sourcePort,target,targetPort,"control");
    private static EdgeDefinition D(string id,string source,string sourcePort,string target,string targetPort)=>new(id,source,sourcePort,target,targetPort);
}
