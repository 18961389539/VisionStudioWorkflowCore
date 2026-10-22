/** 画布节点 → 应用外壳的极简单向桥：节点内部需要触发外壳动作（切标签页 / 选中）时使用。
 *  不走 node.data（data 会被撤销快照与草稿序列化），也不引入全局状态库。 */

type NodeDetailsListener = (nodeId: string) => void;

const listeners = new Set<NodeDetailsListener>();
const configurationListeners = new Set<NodeDetailsListener>();

/** 配置和结果详情使用同一弹窗的不同页签。 */
export function onConfigureNode(listener: NodeDetailsListener): () => void {
  configurationListeners.add(listener);
  return () => { configurationListeners.delete(listener); };
}

export function configureNode(nodeId: string): void {
  for (const listener of [...configurationListeners]) listener(nodeId);
}

/** 订阅“查看该节点的完整运行结果”；返回取消订阅函数。 */
export function onShowNodeDetails(listener: NodeDetailsListener): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** 节点 footer 的“详情”入口调用：把节点交给外壳并打开结果弹窗。 */
export function showNodeDetails(nodeId: string): void {
  for (const listener of [...listeners]) listener(nodeId);
}
