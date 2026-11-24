# 引擎算法性能回归门禁

固定夹具微基准，夜间在固定机器上跑，只拦**大幅回退**。它回答的问题是"同一个算法、同一张图，
今天是不是比上次慢了一个量级"，不回答"这台机器有多快"。

## 为什么单测挡不住这类回退

`VisionStudio.Engine.Tests` 的输入大多是测试里现画的 `new Mat(...)`，断言的是结果对不对。
OpenCvSharp / JLVision 升级、预处理插值改动、循环里多一次 `Clone()`，单测可以全绿，
而节拍已经从 30 ms 掉到 90 ms。性能门禁与 [`golden-regression.md`](golden-regression.md)
共用同一批提交进仓库的 PNG 夹具：输入不变，时间才有可比性。

## 用例

| 用例 | 夹具 | 守什么 |
|---|---|---|
| `preprocess.gaussian.640x480` | `scratch-640x480.png` | 高斯平滑（kernel 5）的基线耗时 |
| `preprocess.threshold.640x480` | `scratch-640x480.png` | 手动阈值 + 统计输出的基线耗时 |
| `preprocess.gaussian.1280x960` | 同一夹具线性放大 2× | 更大画幅下的预处理路径（缓存/分块退化的信号） |
| `match.shape.managedChamfer.640x480` | `edge-640x480.png`（模板=场景） | managed chamfer 建模 + 搜索的完整路径 |

每个用例除了计时还会做**结果自检**：输出尺寸、阈值前景占比、匹配 `found` 与 `score ≥ 0.8`。
算法被改坏时门禁报 `BENCHMARK FAILED`（退出码 11），而不是把一次失败当成"变快了"。

用例不做跨机器绝对比较。基线 JSON 记录 `machine`（默认 `Environment.MachineName`，
可用 `--machine-label` 固定）；机器不一致时输出 `MACHINE_MISMATCH` 并**跳过门禁**，
因为跨机器的毫秒数没有可比性。夜间 runner 第一次运行需要用 `update_baseline` 钉住基线。

## 口径

- 每个用例先热身（默认 2 次，不计时），再取 9 个样本，排序后取**中位数**；
- 计时只包住节点执行，结果校验与输出释放不计入；
- 只有当前中位数比基线中位数**慢超过 25%** 才判回退；新用例没有基线时只记录、不失败；
- 基线里多于当前用例的条目只提示，不失败（用例被删时要在这里同步删）；
- 证据写在 `artifacts/engine-benchmark-result.json`，包含每个用例的 median/min/p90、
  相对基线的 delta 与 `gateStatus`。

25% 是避噪线，不是性能目标：中位数对偶发 GC/调度抖动不敏感，但一次真正翻倍的改动
（例如多了一次全图 `Sobel` 或 `Clone`）不会落在这条线内。

## 怎么跑

```powershell
# 日常比较（基线已提交）
./scripts/engine-benchmark-gate.ps1

# 在专用机器上重新钉基线：只允许在固定 runner 上执行
./scripts/engine-benchmark-gate.ps1 -UpdateBaseline

# 直接调 CLI（可指定夹具/基线/样本数）
dotnet run --project backend/src/VisionStudio.Benchmark.Cli -- --engine-bench `
  --fixtures backend/tests/VisionStudio.Engine.Tests/Fixtures `
  --samples 9 --warmup 2
```

夜间 workflow：[`../.github/workflows/engine-performance-gate.yml`](../.github/workflows/engine-performance-gate.yml)，
cron 每天一次，`runs-on: self-hosted`。手工钉基线时用 `workflow_dispatch` 勾选
`update_baseline`，再把 workflow 产物里的 `ci/engine-benchmark-baseline.json` 提交回仓库——
CI 不直接写仓库内容。

## 红了怎么办

1. 先看 `artifacts/engine-benchmark-result.json` 的 `cases[*].deltaPercent`，确认是单个用例
   还是所有用例一起变慢。所有用例同比例变慢通常是 runner 换了/被别的任务占用，先查机器；
2. 单个用例变慢，按节点实现找最近改动（匹配看 `ChamferShapeMatching`/`ManagedShapeMatch`，
   预处理看 `PreprocessNodes` 与 `JLVisionOperators` 的调用）；
3. 确属有意的算法升级（例如新增了一轮精化），在同一个 PR 里重新生成基线并说明为什么
   接受这笔耗时——**不要**为了让门禁变绿去调高容差；
4. 机器重装/换 runner 后，用 `update_baseline` 重新钉一次，并在提交里写明机器名变了。

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 通过；或基线机器不一致而跳过（证据里 `gateStatus=MACHINE_MISMATCH`） |
| 10 | 有用例中位数回退超过容差 |
| 11 | 基准执行/自检失败（算法结果不对、夹具不再产生预期输出） |
| 12 | 配置或基础设施错误（基线缺失、JSON 损坏、夹具目录不存在等） |

## 边界

- 这是**固定机器上的相对基线**，不是跨平台的性能结论；
- 不做并发/多路吞吐，也不覆盖相机采集和宿主调度——那些由 `soak` 与插件性能门禁负责；
- 用例只放算法本身，避免把 WorkflowCore 的调度开销混进基线。