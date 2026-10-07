# Runtime architecture (V0.29)

The product owns the visual-domain model. Workflow Core owns coarse orchestration; the lightweight pipeline owns contiguous hot compute.

```text
VisionWorkflowDefinition
    -> VisionWorkflowCompiler
       -> strong registry/port validation
       -> recursive Structured Workflow IR (Sequence / If / Parallel / Join pairing)
       -> hybrid execution plan
          ├─ Workflow Core VisionNodeStep
          │  └─ camera / device / robot / flow / conservative plugin node
          └─ Workflow Core VisionPipelineStep
             └─ VisionPipelineExecutor
                └─ VisionNodeRuntime (one call per visual node)
                   └─ VisionNodeDispatcher
                      └─ VisionNodeRegistry
                         └─ built-in executor or plugin executor
```

`VisionWorkflowData` is per-run transient memory. It contains the compiled pipeline-segment map plus node outputs, reports, overlays and native resources. OpenCV `Mat` values are never persisted; Workflow Core still runs with `persistSate: false`.

`VisionNodeRuntime` centralizes debug gating, node reports, output/resource tracking, overlays, keyed branch-condition/control-decision updates and disposition updates. Both coarse `VisionNodeStep` and `VisionPipelineExecutor` use this same path, so fusion does not create a second set of execution semantics.

Pipeline segments use deterministic reserved step IDs derived from the first visual node ID. The compiler exposes the mapping in `CompiledWorkflow.PipelineSegments`; the runner attaches that exact map to `VisionWorkflowData` before execution.

Plugins default to Workflow Core unless their catalog capability explicitly opts into `VisionExecutionMode.Pipeline`. This is intentionally conservative until the plugin factory/lifetime model is introduced.

V0.29 still uses execution gating rather than a durable suspended debug session. Full runs support recursively nested If/Parallel. Parallel stepping remains intentionally rejected for non-Full debug modes; nested If-only workflows retain RunNode/RunFromNode/Breakpoint behavior.

## Compiled plan lifetime

The host shares a `WorkflowPlanCache` across all runner instances. Its key includes the execution
fingerprint, full workflow ID, catalog hash and compiler module/schema version; designer labels and
positions remain excluded. Plans are registered once and reused. The cache has a configurable
capacity (`WorkflowPlanCache:Capacity`, default 128), and evicts the least recently used unleased
plan from both the cache and Workflow Core registry. A lease pins a definition until synchronous
execution completes. If every entry is leased, admission waits within the run's cancellation/timeout
budget rather than growing the registry or removing an active definition.

## Production artifacts and trace backpressure

Interactive runs keep immediate JPEG/PNG generation. Production passes internal artifact options:
OK preview/replay sampling uses the same deterministic run-ID hash as trace retention; available NG/error
artifacts are selected without sampling. Only selected image matrices are cloned before per-run
resources are released. JPEG/PNG encoding and trace persistence run on one background consumer.

Each production loop owns a bounded `ProductionTraceWriter` (`ProductionTrace:QueueCapacity`,
default 8). Queue-full admission waits before the next cycle; metadata is never intentionally
dropped. `ProductionTrace:MaxRawArtifactBytesPerRun` (default 32 MiB) bounds selected image copies.
Oversized images are omitted with a persisted note while the run metadata is retained. The raw
image queue is bounded by `(QueueCapacity + 2) * MaxRawArtifactBytesPerRun` including the consumer
and waiting producer, separately from live execution data and encoded artifacts.

Stopping completes and drains the queue before the production loop exits, so the existing stop
timeout also covers trace draining. Persistence failure blocks further cycles, attempts the remaining
accepted records, and faults production; encoding failure preserves metadata with an explanatory
note. The original run start timestamp is carried through background processing. The queue is
in-memory: an abrupt process termination can lose records that have not been persisted yet.
Production status exposes `pendingTraceCount` (queued, being processed or waiting for admission)
and `traceQueueCapacity` so clients can observe backpressure.
