# 流程级金样回归

引擎单测有 1300+ 条，但绝大多数输入是测试里现画的 `new Mat(height, width, MatType.CV_8UC1)`。
这类测试能锁住"函数怎么算"，锁不住"结果整体漂移"：图跟着代码一起变，改了预处理、换了插值方式、
升级了 OpenCvSharp，图也跟着变，断言仍然全绿，而产线上所有测量值已经平移了半个像素。

金样回归补的就是这一层：**输入是提交进仓库的固定 PNG，不再由代码生成**，断言走真实工作流运行器
（`WorkflowCoreVisionRunner` → 编译 → 调度 → 节点），而不是直接调某个算法类。

- 用例：`backend/tests/VisionStudio.Engine.Tests/GoldenWorkflowRegressionTests.cs`
- 夹具：`backend/tests/VisionStudio.Engine.Tests/Fixtures/*.png`（`CopyToOutputDirectory=PreserveNewest`）
- 生成脚本：`scripts/generate-golden-fixtures.py`（纯标准库，不依赖 numpy/OpencvSharp）

## 三条基线覆盖什么

| 夹具 | 流程 | 基线口径 |
|---|---|---|
| `scratch-640x480.png` | `image.load → image.threshold(invert) → measure.blobs` | 划痕条数 = 3（离散口径）；总面积 1454 ± 8% |
| `edge-640x480.png` | `image.load → measure.caliper` | 边缘 x = 312.4 ± 0.35，y = 240 ± 0.35 |
| `ring-640x480.png` | `image.load → measure.caliperCircle` | 拟合半径 120.5 ± 0.4，圆心 (319.6, 240.3) ± 0.5 |

对应三类最容易悄悄漂移的算法路径：阈值+连通域（缺陷检出）、灰度过零点插值（边缘定位）、
卡尺点集拟合（圆测量）。三者任一被改坏，产线的判定结果都会整批变化。

## 为什么用容差而不是哈希

- 亚像素结果逐位比对必然 flaky：同一条抛物线插值，浮点累加顺序变一下就差 1e-15，
  哈希比对会把无害的重构变成红叉；
- 卡太死会诱导"改期望"而不是"查回归"——当容差比真实误差还小时，失败信息不再指向问题。

所以容差按**算法口径**给：边缘定位取 ±0.35 px（约 0.5 个采样步长），面积取 ±8%
（只拦整体尺度变化，不拦边界抖动）。数量是离散量，直接用相等断言——多一条划痕被切碎或漏检
必须变红。

## 改动后的正确动作

| 场景 | 动作 |
|---|---|
| 重构，结果不应变化 | 什么都不改。红了就是真回归，先查改动 |
| 有意改算法精度（如换拟合方式） | 改期望值，并在 `CHANGELOG` 写清"哪条基线变了、预期变化多少" |
| 重新生成夹具 | 像素全变，必须同步更新三条用例的期望值；不要只更新一部分 |
| 加一条新基线 | 在生成脚本里加一张图 + 一段说明，再照着上面三条用例的结构加一个 `[Fact]` |

重新生成：

```bash
python scripts/generate-golden-fixtures.py
```

脚本刻意用固定种子的自写 LCG 而非 `random` 模块：`random` 的实现细节随 Python 版本变，
"同一份脚本生成同一张图"是这套夹具的前提。

## 读盘授权也走真实路径

`image.load` 在本仓库里受 `IReplayInputResolver` 授权（生产环境禁止加载任意路径）。
用例里没有绕过它，而是内置了一个只授权 `Fixtures/` 目录的 `FixtureResolver`——
金样如果能绕过授权，就等于测试覆盖了一条生产上不存在的路径。
