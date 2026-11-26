#pragma warning disable CS1591
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ImageViewer.Core.Analysis;

/// <summary>One cancellable step that mutates an analysis context.</summary>
public interface IAnalysisStage<TContext>
{
    ValueTask ExecuteAsync(TContext context, CancellationToken cancellationToken = default);
}

/// <summary>Composable, ordered analysis stages.</summary>
public interface IAnalysisPipeline<TContext>
{
    ValueTask ExecuteAsync(TContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs analysis stages in declaration order and stops promptly when cancellation is requested.
/// The context is deliberately owned by the caller so UI adapters can map results without a Core dependency on WPF.
/// </summary>
public sealed class AnalysisPipeline<TContext> : IAnalysisPipeline<TContext>
{
    private readonly IReadOnlyList<IAnalysisStage<TContext>> _stages;

    public AnalysisPipeline(IEnumerable<IAnalysisStage<TContext>> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = [.. stages];
    }

    public async ValueTask ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (IAnalysisStage<TContext> stage in _stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Adapts a host-specific function into an analysis stage.</summary>
public sealed class DelegateAnalysisStage<TContext> : IAnalysisStage<TContext>
{
    private readonly Func<TContext, CancellationToken, ValueTask> _execute;

    public DelegateAnalysisStage(Func<TContext, CancellationToken, ValueTask> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public ValueTask ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _execute(context, cancellationToken);
    }
}
#pragma warning restore CS1591
