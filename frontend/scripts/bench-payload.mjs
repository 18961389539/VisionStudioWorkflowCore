/**
 * 负载构造基准（2026-10-26）。
 *
 * 用途：给"编辑器每帧重建文档负载"这条链路一个可复现的数字，避免以后靠感觉优化。
 * 运行：node frontend/scripts/bench-payload.mjs
 *
 * 结论（本机 200 节点 / 300 边 / 268 KB 文档）：
 *   - 构造负载旧实现（每条边 nodes.find + 端口 find）0.22ms → 新实现（Map）0.18ms；
 *   - 真正的热点是 JSON.stringify：3.62ms / 次；
 *   - 因此优化的重点不在构造而在"不要每帧序列化"（拖拽期间冻结快照 + 复用调用方指纹）。
 */

const N = Number(process.argv[2] ?? 200);
const E = Number(process.argv[3] ?? 300);
const PORTS = 8;

const nodes = Array.from({ length: N }, (_, i) => ({
  id: `n${i}`,
  position: { x: i * 10, y: i * 5 },
  data: {
    typeKey: 'image.threshold',
    label: `node ${i}`,
    // 真实文档里参数可能很大：这里放一个 64 点的 ROI 多边形
    parameters: { threshold: 128, roi: { type: 'Polygon', points: Array.from({ length: 64 }, (_, k) => ({ x: k, y: k * 2 })) } },
    outputs: Array.from({ length: PORTS }, (_, k) => ({ name: `out${k}`, dataType: 'Image' }))
  }
}));

const edges = Array.from({ length: E }, (_, i) => ({
  id: `e${i}`, source: `n${i % N}`, target: `n${(i + 1) % N}`,
  sourceHandle: `out${i % PORTS}`, targetHandle: 'image', data: { kind: 'data' }
}));

const getPorts = (node, key) => node?.data?.[key] ?? [];

function buildFind() {
  return {
    id: 'wf',
    name: 'demo',
    nodes: nodes.map((node) => ({ id: node.id, type: node.data.typeKey, name: node.data.label, position: node.position, parameters: node.data.parameters })),
    edges: edges.map((edge) => {
      const sourceNode = nodes.find((n) => n.id === edge.source);
      const sourcePort = getPorts(sourceNode, 'outputs').find((p) => p.name === edge.sourceHandle);
      return { id: edge.id, sourceNodeId: edge.source, sourcePort: edge.sourceHandle ?? 'next', targetNodeId: edge.target, targetPort: edge.targetHandle ?? 'exec', kind: edge.data?.kind ?? (sourcePort?.dataType === 'Control' ? 'control' : 'data') };
    })
  };
}

function buildMap() {
  const outputsByNode = new Map();
  for (const node of nodes) {
    const ports = getPorts(node, 'outputs');
    outputsByNode.set(node.id, new Map(ports.map((p) => [p.name, p])));
  }
  return {
    id: 'wf',
    name: 'demo',
    nodes: nodes.map((node) => ({ id: node.id, type: node.data.typeKey, name: node.data.label, position: node.position, parameters: node.data.parameters })),
    edges: edges.map((edge) => {
      const sourcePort = outputsByNode.get(edge.source)?.get(edge.sourceHandle ?? '');
      return { id: edge.id, sourceNodeId: edge.source, sourcePort: edge.sourceHandle ?? 'next', targetNodeId: edge.target, targetPort: edge.targetHandle ?? 'exec', kind: edge.data?.kind ?? (sourcePort?.dataType === 'Control' ? 'control' : 'data') };
    })
  };
}

function measure(fn, runs = 200) {
  for (let i = 0; i < 20; i++) fn();
  const start = process.hrtime.bigint();
  for (let i = 0; i < runs; i++) fn();
  return Number(process.hrtime.bigint() - start) / 1e6 / runs;
}

const buildFindMs = measure(buildFind);
const buildMapMs = measure(buildMap);
const buildAndStringifyMs = measure(() => JSON.stringify(buildMap()));
const bytes = JSON.stringify(buildMap()).length;

console.log(`payload: ${N} nodes / ${E} edges / ${bytes} bytes`);
console.log(`build (find):            ${buildFindMs.toFixed(2)} ms`);
console.log(`build (map):             ${buildMapMs.toFixed(2)} ms`);
console.log(`build + JSON.stringify:  ${buildAndStringifyMs.toFixed(2)} ms  <- 热点`);
console.log(`per-frame saving (2 stringify/frame -> 0 while dragging): ~${(buildAndStringifyMs * 2).toFixed(1)} ms`);
