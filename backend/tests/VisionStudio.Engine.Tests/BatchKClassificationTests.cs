using VisionStudio.Engine.Algorithms;
namespace VisionStudio.Engine.Tests;
public sealed class BatchKClassificationTests
{
    [Fact] public void RankClassification_SoftmaxIsStableAndRanked()
    {
        var rank=InferenceStats.RankClassification([1000f, 1001f, 999f], "scores", true, 2, ["cat","dog","bird"]);
        Assert.Equal("dog", rank.Top[0].Label); Assert.Equal(2, rank.Top.Length); Assert.Equal(3, rank.ClassCount);
        Assert.True(rank.Top[0].Score > 0.5); Assert.True(rank.Margin > 0);
        var sum=InferenceStats.RankClassification([0f,0f], "scores", true, 5, []);
        Assert.Equal(2, sum.Top.Length); Assert.Equal(0.5, sum.Top[0].Score, 6);
    }
    [Fact] public void RankClassification_Yolo26ValidatesProbabilities()
    {
        var rank=InferenceStats.RankClassification([0.1f,0.7f,0.2f], "yolo26", true, 3, []);
        Assert.Equal(1, rank.Top[0].Index); Assert.Equal(0.7, rank.Top[0].Score, 5);
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([2f,1f], "yolo26", false, 1, []));
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([0.1f,0.2f], "yolo26", false, 1, []));
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([float.NaN,0f], "scores", true, 1, []));
    }
    [Fact] public void RankClassification_RejectsBadLabelsTopKAndLayout()
    {
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([1f,2f], "scores", true, 2, ["only-one"]));
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([1f,2f], "scores", true, 0, []));
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([1f,2f], "yolo99", true, 1, []));
        Assert.Throws<InvalidOperationException>(()=>InferenceStats.RankClassification([], "scores", true, 1, []));
        var raw=InferenceStats.RankClassification([2f,1f], "scores", false, 1, []);
        Assert.Equal(2d, raw.Top[0].Score, 6);
    }
}
