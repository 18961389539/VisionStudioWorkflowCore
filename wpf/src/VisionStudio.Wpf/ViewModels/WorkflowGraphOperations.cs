using System.Text.Json;
using System.Windows;
using VisionStudio.Abstractions;

namespace VisionStudio.Wpf.ViewModels;

public sealed record WorkflowHistoryEntry(string ActionName, string SnapshotJson);

/// <summary>
/// 流程编辑器有界撤销/重做历史栈（以序列化快照保证节点参数与连线的无损回退）。
/// </summary>
public sealed class WorkflowHistoryManager
{
    private const int MaxEntries = 50;
    private readonly List<WorkflowHistoryEntry> _undo = [];
    private readonly List<WorkflowHistoryEntry> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].ActionName : null;
    public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].ActionName : null;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Push(string actionName, string snapshotJson)
    {
        if (_undo.Count > 0 && _undo[^1].SnapshotJson == snapshotJson)
            return;

        _undo.Add(new WorkflowHistoryEntry(actionName, snapshotJson));
        if (_undo.Count > MaxEntries)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public WorkflowHistoryEntry? PopUndo(string currentSnapshotJson)
    {
        if (_undo.Count == 0) return null;
        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new WorkflowHistoryEntry(target.ActionName, currentSnapshotJson));
        return target;
    }

    public WorkflowHistoryEntry? PopRedo(string currentSnapshotJson)
    {
        if (_redo.Count == 0) return null;
        var target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new WorkflowHistoryEntry(target.ActionName, currentSnapshotJson));
        return target;
    }
}

/// <summary>
/// 画布选中节点及内部连线的剪贴板，支持跨操作复制/粘贴与克隆。
/// </summary>
public sealed class WorkflowClipboard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private IReadOnlyList<NodeDefinition> _nodes = [];
    private IReadOnlyList<EdgeDefinition> _edges = [];

    public bool HasContent => _nodes.Count > 0;

    public void Copy(IEnumerable<WorkflowNodeViewModel> selectedNodes, IEnumerable<WorkflowConnectionViewModel> allConnections)
    {
        var selected = selectedNodes.ToArray();
        if (selected.Length == 0) return;

        var selectedIds = selected.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clonedNodes = selected.Select(n =>
        {
            var customName = string.Equals(n.Title, n.DefaultDisplayName, StringComparison.Ordinal) ? null : n.Title;
            var paramsCopy = n.Parameters.ToDictionary(
                kv => kv.Key,
                kv => JsonSerializer.Deserialize<JsonElement>(kv.Value.GetRawText(), JsonOptions),
                StringComparer.OrdinalIgnoreCase);
            return new NodeDefinition(
                n.Id,
                n.Type,
                customName,
                new NodePosition(n.Location.X, n.Location.Y),
                paramsCopy);
        }).ToArray();

        var internalEdges = allConnections
            .Where(c => selectedIds.Contains(c.Source.Node.Id) && selectedIds.Contains(c.Target.Node.Id))
            .Select(c => new EdgeDefinition(
                c.EdgeId,
                c.Source.Node.Id,
                c.Source.PortName,
                c.Target.Node.Id,
                c.Target.PortName,
                c.IsControl ? EdgeKinds.Control : EdgeKinds.Data))
            .ToArray();

        _nodes = clonedNodes;
        _edges = internalEdges;
    }

    public (IReadOnlyList<NodeDefinition> Nodes, IReadOnlyList<EdgeDefinition> Edges) CreatePaste(
        IEnumerable<WorkflowNodeViewModel> existingNodes,
        double offsetX = 48,
        double offsetY = 48)
    {
        if (_nodes.Count == 0) return ([], []);

        var usedIds = existingNodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pastedNodes = new List<NodeDefinition>(_nodes.Count);

        foreach (var src in _nodes)
        {
            var shortPrefix = src.Type.Split('.').LastOrDefault() ?? "node";
            var idx = 1;
            string nextId;
            do
            {
                nextId = $"{shortPrefix}-{idx++}";
            } while (!usedIds.Add(nextId));

            idMap[src.Id] = nextId;
            var pos = src.Position is { } p
                ? new NodePosition(Math.Round(p.X + offsetX, 1), Math.Round(p.Y + offsetY, 1))
                : new NodePosition(120, 120);
            var paramsCopy = src.Parameters?.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            pastedNodes.Add(new NodeDefinition(nextId, src.Type, src.Name, pos, paramsCopy));
        }

        var pastedEdges = new List<EdgeDefinition>(_edges.Count);
        foreach (var edge in _edges)
        {
            if (idMap.TryGetValue(edge.SourceNodeId, out var newSrc)
                && idMap.TryGetValue(edge.TargetNodeId, out var newDst))
            {
                pastedEdges.Add(new EdgeDefinition(
                    $"e-{Guid.NewGuid():N}"[..10],
                    newSrc,
                    edge.SourcePort,
                    newDst,
                    edge.TargetPort,
                    edge.Kind));
            }
        }

        return (pastedNodes, pastedEdges);
    }
}

/// <summary>
/// 基于拓扑分层（DAG）的左→右自动排版算法。
/// </summary>
public static class WorkflowAutoLayout
{
    public static IReadOnlyDictionary<string, Point> ComputePositions(
        IReadOnlyList<WorkflowNodeViewModel> nodes,
        IReadOnlyList<WorkflowConnectionViewModel> connections,
        double startX = 60,
        double startY = 80,
        double columnSpacing = 310,
        double rowSpacing = 210)
    {
        if (nodes.Count == 0) return new Dictionary<string, Point>();

        var nodeIds = nodes.Select(n => n.Id).ToArray();
        var indegree = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodeIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var layer = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (var conn in connections)
        {
            var src = conn.Source.Node.Id;
            var dst = conn.Target.Node.Id;
            if (src.Equals(dst, StringComparison.OrdinalIgnoreCase)) continue;
            if (!outgoing.TryGetValue(src, out var list) || !indegree.ContainsKey(dst)) continue;
            list.Add(dst);
            indegree[dst]++;
        }

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            visited.Add(cur);
            foreach (var next in outgoing[cur])
            {
                layer[next] = Math.Max(layer[next], layer[cur] + 1);
                indegree[next]--;
                if (indegree[next] == 0)
                    queue.Enqueue(next);
            }
        }

        // 若图存在环，把未访问节点放在其后层
        var maxAssigned = layer.Values.DefaultIfEmpty(0).Max();
        foreach (var id in nodeIds)
        {
            if (!visited.Contains(id))
                layer[id] = ++maxAssigned;
        }

        var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in nodes.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(n => n.Location.Y).ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var row = 0; row < ordered.Length; row++)
            {
                positions[ordered[row].Id] = new Point(
                    Math.Round(startX + group.Key * columnSpacing, 1),
                    Math.Round(startY + row * rowSpacing, 1));
            }
        }

        return positions;
    }
}
