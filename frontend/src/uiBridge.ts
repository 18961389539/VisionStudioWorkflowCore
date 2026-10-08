/** 画布节点 → 应用外壳的极简单向桥：节点内部需要触发外壳动作（切标签页 / 选中）时使用。
 *  不走 node.data（data 会被撤销快照与草稿序列化），也不引入全局状态库。 */

type NodeDetailsListener = (nodeId: string) => void;

const listeners = new Set<NodeDetailsListener>();

/** 订阅“查看该节点的完整运行结果”；返回取消订阅函数。 */
export function onShowNodeDetails(listener: NodeDetailsListener): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** 节点 footer 的“查看完整详情”入口调用：把节点交给外壳（选中 + 展开运行结果）。 */
export function showNodeDetails(nodeId: string): void {
  for (const listener of [...listeners]) listener(nodeId);
}
