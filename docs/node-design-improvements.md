# 节点设计改进

这条线只做一件事：把「节点目录 / 参数 / 端口」收敛成一份可校验的契约。
本文件保留**现状、最近轮次与历史索引**；第一 ~ 五十一轮（2026-10-19 → 2026-10-22）的完整过程
已归档到 [`docs/archive/node-design-improvements-rounds-1-51.md`](archive/node-design-improvements-rounds-1-51.md)。

## 现状（2026-11-02）

| 指标 | 当前值 | 守卫 |
| --- | --- | --- |
| 目录条目 | **242** | `scripts/static-audit.py`：目录 ⊆ 执行器、参数契约无未声明读取 |
| 内置执行器 | 256 类型（含 14 个旧类型别名） | `BuiltInNodeExecutors.FindDrift`，不一致则**启动失败** |
| 兼容别名 | 仅 `Nodes/MergedNodeAliases.cs`，只转发不复制算法 | 目录外的执行器必须在 `NodeTypeMigration.ReplacementMap` 里声明 |
| 参数分组守卫 | 通过 | `nodeValidation.test.ts`（47/47） |
| 目录参数契约 | 242 节点，无未声明读取 | `static-audit.py` 的 catalog 检查 |
| 输出端口契约 | 242 节点，**源码目录 = 发布目录（逐端口、逐 Required）** | `NodeOutputContractTests`：任何擦端口/改必需位即失败；执行器缺必填输出即运行时报错 |

节点合并这条线**已经收口**：目录里不再同时存在「通用节点」和「它的某个参数取值」两份条目，
`legacyNodes` 的兼容映射已清空，画布上的「兼容」徽标与配置页提示一并删除。
剩下的重复只有别名执行器——它们没有自己的算法，进不了目录。

## 已关闭的历史遗留

第五十二轮记录过一个遗留：`nodeValidation.test.ts`「参数密集节点至少分两组」失败，
涉及 `feature.ellipse`、`feature.ridge`、`match.homography`、`match.pose3d`、
`measure.bestFitAlign`、`defect.filter` 六个节点。**该项已于 2026-10-29 关闭**：
守卫 47/47 全通过，六个节点的参数均已分组。

第五十三轮的验证写「1138 项中 1137 通过（唯一失败仍是参数分组守卫）」，同一个原因，
现在应为全通过。

> 归档文件里出现的「唯一失败」「已知遗留」都是**当时**的结论，不要当现状引用。

## 第五十二轮：重复节点合并（2026-10-28）

目录长期存在「通用节点 + 它的某个参数取值」两份条目：工具箱隐藏了旧类型，但目录、执行器、
参数表、i18n 和对话框测试都还是两套。本轮把 7 个输出端口完全等价的节点真正合并。

| 变更 | 说明 |
| --- | --- |
| 合并 | `image.blur.gaussian` / `image.blur.median` → `image.filter`（method）；`image.topHat` / `image.bottomHat` / `image.morphGradient` → `image.morphology`（operation）；`region.fromThreshold` → `region.fromRange`（mode=gray）；`measure.perpendicularity` → `measure.parallelism`（新增 mode=parallel/perpendicular） |
| 目录 | 256 → **249** 条；`/api/catalog` 与工具箱只保留规范节点，`catalog.generated.json` 同步重生成 |
| 实现 | 高斯/中值改为 `GaussianBlurFilter` / `MedianBlurFilter` 内部实现，由 `ImageFilterNode` 调用；灰度区间改为 `RegionFromThresholdCore`，由 `RegionFromRangeNode` 调用。**同一算法只有一份代码** |
| 兼容 | 旧类型保留同名别名执行器（`Nodes/MergedNodeAliases.cs`），只转发不复制算法；`NodeTypeMigration` 在 `WorkflowModuleExpander.ExpandAsync` 与编译器入口改写类型并补齐参数，端口不变故无需重写连线 |
| 不变式 | 目录 ⊆ 执行器；目录之外的执行器必须是 `NodeTypeMigration.ReplacementMap` 声明的别名，且每个别名都必须有执行器，否则启动失败（`BuiltInNodeExecutors.FindDrift`） |
| 前端 | `legacyNodes.ts` 拆成两层：仍在目录的兼容映射（画布“兼容”标签）+ 已移除类型的加载期迁移（`migrateNodeType`），`App.restoreWorkflow` 加载时改写 |

### 验证

- Engine 测试 **2092 项通过**（新增 `NodeTypeMigrationTests` 6 项：映射目标存在、参数补齐、显式值不被覆盖、
  未知类型原样返回、旧/新文档命中同一计划缓存键、别名执行器齐全）。
- API 测试 **447 项通过**；前端 `tsc -b` 0 error，全量 **1134 项中 1133 通过**（唯一失败见“已知遗留”，已关闭）。
- 净删除约 820 行（重复实现 + 重复目录条目 + i18n 死键）。

### 已知遗留（均已关闭，见文首「已关闭的历史遗留」）

- ~~`nodeValidation.test.ts`「参数密集节点至少分两组」在本次改动前已失败~~：`feature.ellipse`、
  `feature.ridge`、`match.homography`、`match.pose3d`、`measure.bestFitAlign`、`defect.filter`
  六个节点参数未分组。**2026-10-29 复测 47/47 通过，已关闭。**
- ~~其余 6 个“已声明兼容但仍在目录”的节点（`image.grayscale`、`image.threshold.adaptive/otsu`、
  `text.match/regex`、`region.fromColorRange`、`measure.blob`）暂不能物理删除~~：已在第五十三轮
  补齐缺失输出后物理合并，目录降到 242，`legacyNodes` 兼容映射清空。

## 第五十三轮：补齐规范节点输出后完成剩余兼容节点合并（2026-10-29）

第五十二轮保留了 6 个“输出端口是子集”的兼容节点。本轮先把缺失输出补进规范节点，再物理合并：
目录 **249 → 242**，`legacyNodes` 兼容映射随之清空，画布“兼容”徽标与配置页提示一并删除。

| 旧节点 | 合并到 | 先补的能力 |
| --- | --- | --- |
| `image.grayscale` | `image.channel`（channel=gray/average/max） | 新增 `weights`（BT.601 / BT.709 / 均值），gray 分支按权重求和 |
| `image.threshold.adaptive` / `.otsu` | `image.threshold`（mode） | 新增 `foregroundRatio` 输出；`warningCount`/`preview` 保留为可空端口 |
| `text.match` | `text.validate` | 新增 `mode`/`matchLength` 输出；`mode` 对外仍是 startsWith/endsWith 驼峰取值 |
| `text.regex` | `text.validate`（mode=regex） | 新增 `matchedLength`/`matchIndex`/`matchCount`/`groupCount` 输出与 `singleline`/`minMatchedLength`/`emptyInputPolicy`/`countMatches` 参数 |
| `region.fromColorRange` | `region.fromRange`（mode=color） | 端口本就一致，仅补 mode |
| `measure.blob` | `measure.blobs`（mode=largest） | 新增 `found`/`circularity`/`width`/`height`/`blobs` 输出与 `overlay` 可空端口 |

要点：

- `text.validate` 的正则路径默认 **不统计全部匹配**（`countMatches=false`），避免回溯型 pattern 因为多遍历一次而超时；
  `text.regex` 兼容路径显式打开，保持旧语义与旧超时行为。
- 别名执行器（`Nodes/MergedNodeAliases.cs`）继续只做“参数改写 + 转发”，不复制算法；
  `image.grayscale` 的 `mode→channel` 需要**取值映射**，`NodeTypeMigration` 因此新增 `Remap` 概念。
- 目录删除按条目边界操作时要注意参数数组里也有 `new(`：本轮误删造成两处语法碎片，已修复；后续建议按“条目起始锚点 + 下一个条目锚点”删除。

### 验证

- Engine 2091 项、Api 447 项全通过；`dotnet build` 0 error。
- 前端 `tsc -b` 0 error、生产构建通过、1138 项中 1137 通过（唯一失败是参数分组守卫，**已于 2026-10-29 修复**）。
- `legacyNodes.test.ts` 重写为迁移契约测试：14 个旧类型都能映射到目录中存在的类型、参数补齐不覆盖显式值、灰度 mode→channel 映射。


## 第五十四轮：目录成为输出端口的单一事实源（2026-11-02）

本轮只修一件事：**发布的目录必须逐端口等于源码目录**，不再靠后处理把"声明了但从不产出"的端口擦掉。

`NodeOutputContracts.Apply` 当时在发布前改写契约，一共三条规则：11 条的 `Unimplemented` 名单
（`coordinate.distance` 的 frame/unit/coincident、`geometry.angle` 的 degrees/radians/quadrant、
`flow.parallel` 的 branchCount/joined、`flow.loopEnd` 的 iteration/stopped/reason …）、
`OverlayProducers` 白名单（决定谁能发布 `overlay` 图像端口），以及按节点类型放宽 `Required`
（`image.colorConvert` 的 channel2/3）。后果是**同一条契约有两个真值**：源码目录说一套端口，
`/api/catalog` 说另一套；而且名单会过期——`flow.loopEnd` 的三个端口早已从源码删除，名单还在擦空气。

| 变更 | 说明 |
| --- | --- |
| 目录后处理归零 | `NodeOutputContracts.Apply` 整段删除（不再被 `Items` 调用）：过去它做三件事——擦 `Unimplemented` 端口、按 `OverlayProducers` 白名单丢 `overlay`、按节点类型放宽 `Required` |
| 删除 `Unimplemented` 名单（11 条） | 同时从各目录删掉这些死声明：`coordinate.transformCompose`(valid)、`coordinate.distance`(frame/unit/coincident)、`coordinate.frameTree`(edgeCount/valid)、`coordinate.transformPoint3D`(magnitude/xyMagnitude/azimuthDeg/elevationDeg)、`geometry.angle`(degrees/radians/quadrant)、`measure.concentricity`(deltaX/deltaY/pass)、`collection.detections.count`(finiteCount/invalidCount/maxScore)、`collection.detections.filter`(keptRatio/minKeptScore/maxKeptScore)、`flow.parallel`(branchCount/joined) |
| 清理 118 条死 `overlay` 声明 | 118 个节点声明了 `overlay` 图像端口却从不产出，靠 `OverlayProducers` 白名单在发布时丢弃。逐条删除后白名单一并删除：**发布目录逐字节不变**（hash 前后同为 `585780fe…`） |
| 清理 38 条死 `preview` 声明 | 没有任何执行器产出 `preview`，全靠后处理丢弃；删除后 `Apply` 的最后一条规则也失去意义 |
| Required 条件写回目录 | `image.colorConvert` 的 `channel2/channel3` 在目录里直接声明 `Required: false`（`logic.boolean.b` 早已是 `RequiredWhen`），不再靠按类型名的补丁 |
| `region.toDetections` 补实现 | 三个端口保留并真正产出：`emitted`（=count）、`truncated`（maxCount 是否裁掉了对象）、`totalArea`（已发出检测的面积和）。此前 `maxCount` 静默截断，下游无法区分"只有 3 个"和"被裁到 3 个" |
| 能力位纠正 | 10 个 `jlvision.*` 节点输出 `overlay` 却声明 `EmitsOverlay=false`，改为 true；`image.synthetic` 的 `placementMode=randomEachRun` 每次重采样，声明 `Deterministic=false`（不再进自动重试） |

### 验证

- Engine **2126 项全通过**（`NodeOutputContractTests` 1 项守卫）；`BatchPThinNodeTests` 新增 `emitted/truncated/totalArea` 断言（maxCount=1 时 area=64）。
- `catalog.generated.json` 重新生成：hash `e0224c52…` → `585780fe…`，差异仅为本轮改动；
  同时补齐了快照与源码的既有漂移——`output.notify.source`（Source Tag）是 2026-10-29 加进
  `P0CompletionCatalog` 的参数，快照一直没重生成（`CatalogExporter --check` 当时会失败），
  说明"生成快照"这一步还没有进任何人都会跑的守卫。
- 删 118 条 `overlay` 与 38 条 `preview` 声明、删除 `OverlayProducers` 白名单、删除 `Apply` 之后重新生成，
  快照与删除前**逐字节相同**——这是"纯死声明清理"最直接的证明。
- 发布目录现在 100% 由各 `*Catalog.cs` 决定；新增节点若声明了不产出的端口，
  必填端口会在第一次执行时以 `Executor '…' did not return required output` 失败，而不是被悄悄擦掉。

### 未关闭（下一轮候选）

- `flow.parallel` / `flow.join` 仍发布常量输出（`branches=2`、`joined=true`）。删端口会破坏已保存流程，
  要么让运行时把真实分支数 / 到达情况写进结果，要么按兼容策略保留并在描述里标注"编译期标记"。
- `flow.switch` 的 16 个 `caseN`、`image.stitch/fuse` 的 `image1..8` 仍是固定序号端口；集合类型（`FrameSet` / 列表）改造属于接口变更。


## 第五十五轮：top-N 截断对下游可见（2026-11-02）

三个节点的默认上限很小，却只返回裁剪后的列表——`count` 无法区分"确实只有 N 个"和"被裁到 N 个"：

| 节点 | 参数 | 默认 | 之前 |
| --- | --- | --- | --- |
| `measure.blobs` | `maxCount` | 20 | 候选多于 20 时静默丢弃，`count` 报 20 |
| `feature.ellipse` | `maxEllipses` | 10 | 去重后多于 10 个时静默丢弃 |
| `contour.extract` | `maxCount` | 200 | 候选多于 200 时静默丢弃（`truncated` 只写在 diagnostics 字符串里，没有端口） |

变更：三个节点各新增两个必填输出——`inputCount`（cap 之前的候选数）与 `truncated`（`inputCount > count`），
与 `contour.filter` / `collection.detections.filter` / `region.toDetections` 的口径一致；节点描述同步写明默认上限。

同一轮还收了两处同类问题：

| 节点 | 参数 | 之前 | 现在 |
| --- | --- | --- | --- |
| `inference.detection` / `.obb` / `.pose` / `.instanceSegmentation` | `maxCount`（默认 100） | `maxCount` 是 **NMS 期间**的上限，凑满就 `break`，下游只看到 `count` | 新增 `truncated`，由 `InferenceNmsCap.Fill` 判定 |
| `collection.detections.sort` | `topN`（默认 0=不截断） | 截断后只报 `count` | 新增 `inputCount` + `truncated` |

`InferenceNmsCap.Fill` 的做法：凑满上限后**只多看一个候选**——若下一个候选也存活，说明确实有结果被丢（`truncated=true`）；
若剩余候选全被 NMS 抑制，则没有丢失任何结果（`false`）。因此最坏只多一次 IoU 计算，不需要为统计跑完整 NMS。
推理节点不提供 `inputCount`：NMS 存活数只有在不设上限时才知道，而为统计跑完整 NMS 在密集场景会退化成 O(n²) 阻塞。

### 验证

- 新增 `TruncationVisibilityTests` 5 项：blobs/ellipse/contour.extract 的 cap=1 → `count=1, inputCount=2, truncated=true`，
  放开后 `false`；`collection.detections.sort` 的 `topN=2` 同理；`InferenceNmsCap` 覆盖"上限内 / 还有存活候选 / 剩余全被抑制"三种情况。
- Engine **2131 项全通过**；`CatalogExporter --check` current。
- `catalog.generated.json`：hash `585780fe…` → `83fa6647…`（前三个节点）→ `ca819ec3…`（推理与排序 +6 端口），
  每步 diff 都只包含当轮节点。

### 未关闭

- `feature.line` / `feature.circle` 的 `maxLines` / `maxCircles` 只限制候选预算、输出始终是"最佳一个"，不属于结果截断。
- `inference.classification` 的 `topK` 是显式的"只要前 K 类"，不按截断处理。


## 第五十六轮：FrameSet 能进融合节点（2026-11-02）

`camera.syncCapture` 产出 `FrameSet` 后，只有 `frameset.image` 能拆一帧，多相机/多帧叠加必须把 8 个图像端口逐根接线。
本轮给 `image.fuse` 补一条真正吃 `FrameSet` 的通路：

| 变更 | 说明 |
| --- | --- |
| 新增 `sourceMode` 参数 | `images`（默认，保持原行为）/ `frameSet` |
| 新增 `frameSet` 输入 | 只在 `sourceMode=frameSet` 时必需；整组相机图像按**相机 id** 排序后参与融合（mean/median/min/max 与顺序无关，但跨运行必须稳定、诊断可复现） |
| 两个来源互斥 | `sourceMode=frameSet` 时若还接了 `image1..8`，直接报错而不是悄悄只算一半 |
| 兼容性 | `image1/image2` 改为 `Required: false { RequiredWhen = sourceMode=images }`：新流程/老流程（无 `sourceMode`）仍按默认 `images` 要求这两根线，编译器行为不变 |
| 本地化 | 新增参数名/选项/说明补进 `i18n/catalog.ts`（`Input Source`、两个选项、`image.fuse.sourceMode` 说明），否则本地化覆盖率测试会失败 |

`image.stitch` 没有一起改：全景拼接对输入顺序敏感，`FrameSet` 的相机 id 顺序不一定等于拍摄几何顺序，需要显式的 `cameraOrder` 参数，留给下一轮。

### 关于 `flow.parallel` / `flow.join` 的"常量输出"（结论：不改）

审查时把 `branches=2`、`joined=true`、`merged=true` 记成"假数据"，复核后是**结构性常量**：
`flow.parallel` 只有 branch1/branch2 两个控制端口，分支数必然是 2；`flow.join`/`flow.merge` 只有汇合点被执行才会运行，
所以 `joined/merged` 为 true 是事实而非伪造。它们没有信息量，但删端口会让已保存流程在加载时报
"Output 'joined' does not exist"，收益为零。**按兼容优先保留**，不再作为缺陷项。

### 验证

- 新增 `Fuse_AcceptsASynchronizedFrameSet`：FrameSet 两相机（120/200）→ mean=160；`frameSet` + `image1` 同时接 → 报错。
- Engine **2132 项全通过**；Api 457 项通过；`CatalogExporter --check` current。
- 前端 `tsc -b` 0 error；vitest 90 文件 / 1119 项通过（另有 2 个文件因 vitest worker 启动超时未运行，单独复测通过）。
  新增参数一度让两个本地化覆盖率测试失败（缺中文标签），补齐 `i18n/catalog.ts` 后通过。
- 目录快照：`ca819ec3…` → `88da10e0…`，diff 仅 `image.fuse`（2 个端口必填性 + `frameSet` 端口 + `sourceMode` 参数 + 描述）。


## 第五十七轮：`image.stitch` 的 FrameSet 与相机顺序（2026-11-02）

第五十六轮给 `image.fuse` 补了 FrameSet 通路，但拼接没有照抄：全景/长工件拼接**对输入顺序敏感**，
而 `IVisionFrameSet.Images` 是字典，顺序不可依赖。本轮补上显式顺序：

| 变更 | 说明 |
| --- | --- |
| `sourceMode` | `images`（默认）/ `frameSet`，两条来源互斥 |
| `frameSet` 输入 | 只在 `frameSet` 模式必需 |
| `cameraOrder` 参数 | 逗号分隔的相机 id，按左→右拼接顺序；留空按相机 id 排序；未知 id **在拼接前**报错并列出可用 id |
| 兼容 | `image1/image2` 改 `RequiredWhen(sourceMode=images)`；老流程默认 images，行为不变 |
| 本地化 | 新增参数/选项/说明同步进 `i18n/catalog.ts` |

顺序错误如果留到 OpenCV 才失败，只会拿到一个 Stitcher 状态码；现在在参数校验阶段就指出是哪个 id 不在帧组里。

### 验证

- 新增 2 项：`Stitch_ReadsAFrameSetInCameraOrderAndRejectsUnknownIds`（两相机按 `cameraOrder` 拼接成功、未知 id 报错并列出可用 id）、
  `Stitch_RejectsSingleCameraFrameSetAndMixedSources`（单相机帧组 / 帧组与图像端口混接都明确失败）。
- Engine **2134 项全通过**；Api 457 项通过；`CatalogExporter --check` current。
- 目录快照 `88da10e0…` → `1ca937cb…`，diff 仅 `image.stitch`（`frameSet` 端口、`sourceMode`/`cameraOrder` 参数、2 个端口必填性、描述）。


## 第五十八轮：Switch 的匹配规则可配置（2026-11-02）

第五十七轮把 `flow.switch` 的端口模型排除了：前端已按 `casesJson` 裁剪端口，IR 也会拦未声明端口，
`caseN` 的固定上限只是静态目录的表达限制，不是正确性问题。真正的缺口在**匹配规则**：

- 过去是 `StringComparison.Ordinal` 精确匹配。扫码/OCR 出来的 `" ok "`、`"Ok"` 会静默落到 `default`，
  现场表现是"偶尔漏判"，排查成本很高；
- 而且没有任何参数能放宽，只能在上游加 trim/upper 节点绕。

| 变更 | 说明 |
| --- | --- |
| `trimValues`（默认 false） | 匹配前同时裁剪输入值与每条分支标签；先裁剪再判 256 字符上限，避免把可用值当超长拒绝 |
| `ignoreCase`（默认 false） | 忽略大小写比较；只差大小写的标签按重复拒绝（编译期报 ambiguous），而不是悄悄匹配第一个 |
| 诊断 | 新增 `comparison=ordinal/ignoreCase`，`value` 记录裁剪后的实际比较值 |
| 兼容 | 两个参数默认关闭，等价于原行为；`ReadCases` 的标签唯一性检查改为按当前比较器判断 |

### 验证

- 新增 `Switch_TrimsAndIgnoresCaseOnlyWhenConfigured`：默认 `" ok "` 不命中；`trimValues+ignoreCase` 命中 case1；
  `["OK","ok"] + ignoreCase` 抛 "ambiguous"。
- Engine **2135 项全通过**；Api 457 项通过；`CatalogExporter --check` current。
- 目录快照 `1ca937cb…` → `1f3fbd47…`，diff 仅 `flow.switch` 的两个参数与描述；i18n 同步（`去除首尾空白`、两条说明）。


## 第五十九轮：默认值对账与基准节点命名（2026-11-02）

**动机**：同一个参数有两条取值路径——新建节点写入目录默认值，旧节点/直接执行走执行器兜底值。
两者不一致时，"新建能用、老节点报错"或反过来，很难在现场定位。

**方法**：脚本抓取每个执行器里的 `node.GetXxx("参数", 字面量默认)`，按"最近的 `public string Type` 声明"归属到节点，
再与目录默认值逐项比对。533 个数值兜底里只剩 1 处真偏差：

| 节点 | 参数 | 目录默认 | 执行器兜底 | 处理 |
| --- | --- | --- | --- | --- |
| `value.statistics` | `usl` | 1 | 0 | 兜底改为 1：兜底 0 会让"只写 lsl"的旧节点在 `LSL < USL` 校验上直接失败 |

同时收掉最后一处命名歧义：`tolerance.datum` 与 `tolerance.drf` 英文名都叫 "Datum Reference Frame"，
现在分别是 **Datum Frame (A + projected B)** 与 **Datum Frame (A|B intersection)**，中文补成
"基准坐标系（A + B 投影）"/"双基准基准系（A|B 交点）"。显示名不是 i18n 键，改动不影响已保存流程。

> 方法学备注：第一次跑出 52 条偏差，其中 51 条是归属错误——类型正则写成 `[a-z0-9.]+`，
> `inference.instanceSegmentation` 这类驼峰类型匹配不到，读取就被算到上一个节点头上。修成 `[a-zA-Z0-9.]+` 后才收敛到 1 条。

### 验证

- 新增 `Statistics_MissingUslUsesTheCatalogDefaultInsteadOfZero`（enableLimits + 仅 lsl 时按 USL=1 正常出 cp/pass）。
- Engine **2136 项全通过**；Api 457 项通过；`CatalogExporter --check` current。
- 目录快照 `1f3fbd47…` → `c8781cb6…`，diff 仅两个 displayName 与 hash。


## 第六十轮：match.shape 的宿主协议纳入契约（2026-11-02）

**先纠正一个判断**：`match.shape` 的 train/run 两阶段其实早就实现了——训练由调优工作区带外触发
（`trainingMode=true`），结果以 base64 存回节点参数 `trainedModelData`（≤16 MB），配 `trainedModelConfig`
参数指纹，`modelMode=trained` 时运行期只反序列化、不再训练。真正缺的不是拆分，而是**契约**：
这 22 个键在 `Algorithms/JLVisionMatching.cs` 里读取，而参数闸门只扫 `Nodes/` 的类块，
所以它们从未进过目录——一个 16 MB 的隐藏参数不受任何校验。

| 变更 | 说明 |
| --- | --- |
| 22 个键声明为 `Internal` | 训练请求（`trainingMode` / `trainingImageData` / `trainingCombine*` / `trainingOriginOverride*` / `trainingReference*`）与训练结果元数据（`trainedModelData` / `trainedModelConfig` / `trainedSourceImage` / `trainedWidth/Height` / `trainedOrigin*` / `trainedPoseOffset*`），Group=`Host Protocol`；不进配置面板、不写新节点默认值，但进入目录契约与生成快照 |
| 描述补充 | `match.shape` 描述写明模型随节点以 base64 保存、16 MB 上限、参数指纹变化必须重训、`modelMode=trained` 未训练前拒绝运行 |
| 审计闸门扩展 | `static-audit.py` 新增两条：① `JLVisionMatching.cs` 的读取必须由 `match.shape`/`match.template` 声明（比"任意节点声明"更严）；② `Algorithms/` 下任何 `node.GetX("键")` 都必须至少被一个目录节点声明 |

**没有拆成两个节点**，理由：模型是高至 16 MB 的二进制块，走端口要么塞进 `String`（污染追踪/制品），
要么需要一套 modelId 资产库；而一旦有了资产库，一个 run 节点 + `modelId` 就够了，两个节点反而多余。
资产库（训练一次、多工位复用）是独立平台项目，设计草案见 [model-asset-store-design.md](model-asset-store-design.md)。

### 验证

- `static-audit.py`：`Algorithm-layer parameter contract: 75 reads covered by match.shape/match.template`；
  `Algorithm layer: 77 distinct parameter reads all declared in the catalog`。
- Engine **2136 项全通过**；Api 457 项通过；前端 `tsc -b` 0 error、vitest 92/92 文件通过。
- 目录快照 `c8781cb6…` → `fcc87040…`，diff 仅 `match.shape` 的 22 个 internal 参数与描述。
  前端 `editableParameters` / `makeDefaultParameters` 均过滤 internal，面板与新建节点默认值不受影响；
  保存走 `cloneParameters(draft)`，不会丢弃已存在的 `trainedModelData`。
- 过程记录：22 条说明第一版写成了中文，被 `NodeCatalogContractTests.Catalog_UserFacingTextIsAuthoredInEnglish`
  挡下——目录文案必须是英文，中文由前端 i18n 覆盖（internal 参数不参与覆盖率统计）。


## 第六十一轮：把两次一次性审计固化成闸门（2026-11-02）

第五十九轮抓 `usl` 用的是临时脚本，第六十轮的输入端口类型核对也是临时脚本——这类一次性调查如果不落成闸门，
下一轮改代码时同样的问题会重新长回来。本轮并入 `scripts/static-audit.py`：

| 闸门 | 规则 | 当前覆盖 |
| --- | --- | --- |
| 参数默认值对账 | `node.GetX("参数", 字面量兜底)` 必须等于目录 `defaultValue`；不一致即失败 | **531** 处数值兜底 |
| 输入端口类型对账 | `context.Require<T>("端口")` / `RequireImage("端口")` 的期望类型必须与目录 `dataType` 一致；端口不存在也报错 | **171** 处读取 |
| 算法层参数归属 | `JLVisionMatching.cs` 的读取必须由 `match.shape`/`match.template` 声明 | 75 处读取 |
| 算法层孤儿键 | `Algorithms/` 下任何读取至少被一个目录节点声明 | 77 个键 |

归属按类块切分（与既有的参数契约检查同一套切法）：辅助类没有 `Type` 声明，其内部读取不会被算到上一个节点头上。
这正是 2026-11-02 一次性审计里 52 条报错中 51 条误报的根因——`region.fromRoi` 的那条就是 `RegionShapeProcessing` 辅助类被误算。

### 验证

- 四条闸门全绿：`531 numeric fallbacks match the catalog` / `171 reads match the catalog` /
  `75 reads covered by match.shape/match.template` / `77 distinct parameter reads all declared`。
- **负向验证**：把 `value.statistics.usl` 的兜底临时改回 0，审计立即报
  `Catalog/executor default drift: value.statistics.usl: catalog=1 executor=0`；还原后恢复绿色，
  确认闸门不是"永远通过"的摆设。
- 输入端口类型核对另抓到的两条都是误报（`region.fromRoi` 的辅助类归属、`FrameTree` 与 `FrameTree2D` 的映射写法），
  修正归属与映射表后为 0——所以端口类型本身就是一致的。


## 第六十二轮：EmitsOverlay 的语义澄清（2026-11-02）

**事实**：82 个节点声明 `EmitsOverlay=true`，但只有 16 个发布 `overlay` 图像端口。该能力位唯一的消费方是
配置对话框的"技术信息"标签，文案写的是**"输出覆盖层"**——于是 66 个只画 viewer 叠加（`result.Overlays`）、
并不发布图像端口的节点，看起来像"少了一个输出端口"。

**结论：不分裂字段**。`VisionToolCapabilities` 是插件 SDK 的公开记录，拆字段会动序列化与第三方插件；
真正的问题在语义没写清、文案没说准。

| 变更 | 说明 |
| --- | --- |
| 语义写进代码 | `VisionToolCapabilities` 补 XML 文档，逐字段说明；`EmitsOverlay` 明确定义为**可视化产物**——viewer 叠加和/或 `overlay` 图像端口，两者独立 |
| 文案拆成两个真实标签 | 发布 `overlay` 端口的节点显示「overlay 图像端口」；只有能力位的显示「可视化叠加」。端口存在性一律查 `outputs`，不再从能力位推断 |
| 新增闸门 | `static-audit.py`：发布 `overlay` 图像端口的节点必须声明 `EmitsOverlay`；反方向不要求（viewer 叠加不产生端口） |

### 验证

- 审计：`Overlay capability: 16 overlay image ports all declare EmitsOverlay`，exit 0。
- 前端：`tsc -b` 0 error；全量 91 文件 / 1138 项通过（1 个文件因 vitest worker 启动超时未跑，单独复测通过）；
  8 个配置对话框测试全通过。
- 无目录变更：本轮只改能力位文档、UI 文案与审计脚本，`catalog.generated.json` 保持 `fcc87040…`。


## 第六十五轮：模型资产 P0 · M2（引擎解析）（2026-11-09）

M1 只让资产能存能取，节点不认识 `modelAssetId`。M2 把这条链路打通，关键约束是**失败语义**：
资产缺失、哈希不符、宿主没提供资产源，都必须明确失败——静默退回内联模型等于偷偷换模型。

| 层 | 变更 |
| --- | --- |
| 契约（Abstractions） | 新增 `IModelAssetSource.TryRead(assetId, version)`；`NodeExecutionContext` 增加 `ModelAssets`（可选，null 表示宿主未提供） |
| 运行时（Engine） | `VisionRunOptions.ModelAssets` → `VisionWorkflowData.ModelAssets` → 派发器 → 执行上下文逐层传递；`WorkflowCoreVisionRunner` 与 `WorkflowDebugSessionService` 支持宿主注入默认实现 |
| 宿主（Api） | `ShapeModelAssetSource` 适配器 + `IModelAssetSource` 注册；`ShapeModelAssetStore.ReadBytesSync`（同步 ADO.NET，`version=0` 解析 latest），避免把 async 传染到匹配算法 |
| 节点 | `match.shape` 新增 `modelAssetId` / `modelAssetVersion` / `modelAssetSha256`；资产优先于内联；运行摘要增加 `modelSource=model-asset` 与 `modelSha256` |

失败语义（都有测试锁定）：

- 有 `modelAssetId` 但 `context.ModelAssets == null` → "host did not provide a model asset source"，并提示清除该参数走内联；
- 资产/版本不存在或已删除 → 报出 id 与请求的版本（`v3` / `latest`）；
- 16 MB 上限与空字节单独报错；
- `modelAssetSha256` 非空时逐字节校验，不符报出 pinned 与实际哈希。

### 验证

- 新增 `ShapeModelAssetResolutionTests` 5 项：无宿主源、资产缺失、`version=0` 原样传递（由资产源解析 latest）、哈希不符、哈希吻合后继续走到模型反序列化。
- Engine **2145 项通过**；Api 467 项通过；前端 `tsc -b` 0 error、本地化 2 项通过。
- 目录快照 `b21f4831…` → `93e8cc2b…`，新增 3 个参数与中文标签/说明。
- 仍未做（M3/M4）：训练工作区的「保存为资产」按钮（调 `extract`）、端到端集成测试与 `modelAssets.enabled` 开关。


## 第六十六轮：模型资产 P0 · M3（训练工作区入口）（2026-11-09）

按"放在训练工作区按钮旁"的决策实现。难点不是按钮，而是**应用时剥离内联模型后，工作区不能因此变成"未训练"**。

| 变更 | 说明 |
| --- | --- |
| 入口 | 训练完成提示下方、`下一步：测试搜索` 旁新增「保存为资产」；已有 `modelAssetId` 时按钮变为「追加为资产版本」 |
| 保存 | 首次 `POST /api/model-assets`，之后 `POST /api/model-assets/{id}/versions`；成功后写入 `modelAssetId` / `modelAssetVersion` / `modelAssetSha256`，草稿里的内联模型**本次会话仍保留**，搜索可继续 |
| 应用 | `stripInlineModelWhenAssetBound`：绑定资产时从写回参数里删掉 `trainedModelData`，文档体积从 MB 级降到 KB 级；未绑定资产或非形状节点原样返回 |
| 回读 | 打开已绑定资产的节点时按 version 拉取字节回填草稿，并把"回填后的参数"同时作为基线，避免刚打开就显示未保存修改 |
| 客户端 | 新增 `modelAssets.ts`（创建/追加/取字节 + 进程内缓存）；路径统一收进 `endpoints.ts` 的 `modelAssets` 常量 |

回读刻意做成"会话内回填、应用时再剥离"：既让训练/搜索工作区照常可用，又不让文档把模型字节带回去。

### 验证

- 前端 `tsc -b` 0 error；vitest **93 文件全通过**（+`modelAssets` 客户端 4 项、`stripInlineModelWhenAssetBound` 4 种边界）。
- 路径守卫（`apiUsage` ratchet）抓到过一次硬编码 `/api/`，已改为 `endpoints.ts` 常量后通过。
- 静态审计 exit 0；后端本轮无改动（Engine 2145 / Api 467 沿用 M2 结果）。


## 第六十七轮：模型资产 P0 · M4（开关与迁移验证）（2026-11-09）

P0 的最后一块：一个可控的回滚开关，加上"内联 → 资产"迁移的可测实现。

| 变更 | 说明 |
| --- | --- |
| 回滚开关 | `ModelAssets:Enabled`（默认 true）。关闭时：有内联模型 → 继续用内联并在运行摘要写 `modelAssetDisabled=true`；没有内联模型 → 明确失败，提示启用开关或恢复内联模型。**不把"关闭"伪装成"资产缺失"** |
| 接口 | `IModelAssetSource.IsEnabled` 带默认实现（`=> true`），既有实现与测试替身无需改动 |
| 迁移 | 抽取逻辑从端点抽成 `ShapeModelAssetExtraction` 服务：只改 `modelAssetId` / `modelAssetVersion` 两个键、删除 `trainedModelData`，其余参数与字节原样保留（可逆）；保存带 `expectedRevision` |
| 测试 | 抽取服务 3 项（成功切换/二次抽取失败/非形状节点与未知目标）+ 开关 2 项（无内联失败、有内联不读资产） |

### 验证

- Engine **2147 项通过**；Api **470 项通过**；`static-audit.py` exit 0；目录快照不变（`93e8cc2b…`）。
- P0 验收项对照：体积（M3 应用时剥离）、复用（同一资产多节点引用）、哈希校验（M2 pinned 校验 + 服务端 sha）、
  失败语义（M2/M4 共 7 项）、回滚（本轮的全局开关）。


## 历史轮次索引（第一 ~ 五十一轮）

完整记录（含每轮的验证数字与当轮的兼容说明）见
[`archive/node-design-improvements-rounds-1-51.md`](archive/node-design-improvements-rounds-1-51.md)。

| 轮次 | 主题 | 日期 |
| --- | --- | --- |
| 第二轮 | 端口、坐标与执行契约 | 2026-10-19 |
| 第三轮 | 清除评分脚手架与契约修复 | 2026-10-19 |
| 第四轮 | 移除无实现控件，补齐实际语义 | 2026-10-19 |
| 第五轮 | 全节点中文本地化收口 | 2026-10-19 |
| 第六轮 | 默认值与空输入策略收敛 | 2026-10-19 |
| 第七轮 | 高级 JLVision 节点契约、图标与帮助文案收口 | 2026-10-19 |
| 第八轮 | 全量节点排除（171 节点） | 2026-10-19 |
| 第九轮 | 推理契约与标签来源收口 | 2026-10-19 |
| 第十轮 | 配置预检与条件端口统一 | 2026-10-20 |
| 第十一轮 | 二元输入、文本运行与列表执行修复 | 2026-10-20 |
| 第十二轮 | 距离、交点与计量结果的数值健壮性 | 2026-10-20 |
| 第十三轮 | 透视插值、阈值输入与图像生命周期 | 2026-10-20 |
| 第十四轮 | 轮廓提取、筛选与属性计算 | 2026-10-20 |
| 第十五轮 | 条件分支参数与坐标输入覆盖 | 2026-10-20 |
| 第十六轮 | 圆间关系与多点拟合支持集 | 2026-10-20 |
| 第十七轮 | 取图租约与区域结果的资源释放 | 2026-10-20 |
| 第十八轮 | 设备标签精度、转换状态与等待计时 | 2026-10-20 |
| 第十九轮 | 节点配置易用性与参数信息架构 | 2026-10-20 |
| 第二十轮 | 透视矫正的四点拾取与自动输出尺寸 | 2026-10-20 |
| 第二十一轮 | 格式相关控件与剩余分组 | 2026-10-20 |
| 第二十二轮 | 归档路径语义与节点说明可读性 | 2026-10-20 |
| 第二十三轮 | 标定模型与重试参数的条件显示 | 2026-10-20 |
| 第二十四轮 | Switch 分支端口按配置显示 | 2026-10-20 |
| 第二十五轮 | Switch 分支列表编辑器 | 2026-10-20 |
| 第二十六轮 | 旧版兼容节点的替换指引 | 2026-10-20 |
| 第二十七轮 | 匹配节点同名步骤参数的可读性 | 2026-10-20 |
| 第二十八轮 | 原始 JSON 参数的格式化与解析反馈 | 2026-10-20 |
| 第二十九轮 | 阵列卡尺接入交互式搜索线与变换预览 | 2026-10-20 |
| 第三十轮 | 测宽节点的标称值语义与像素单位 | 2026-10-20 |
| 第三十一轮 | JSON 字段直接显示结构说明 | 2026-10-20 |
| 第三十二轮 | 测量输出端口补充单位 | 2026-10-20 |
| 第三十三轮 | 推理节点不可用预处理参数的隐藏 | 2026-10-20 |
| 第三十四轮 | 语义分割与异常检测的裁剪参数收口 | 2026-10-20 |
| 第三十五轮 | 形态学核参数说明纠正 | 2026-10-20 |
| 第三十六轮 | 模式互斥参数的进一步收敛 | 2026-10-20 |
| 第三十七轮 | 参数分组覆盖与 If 迟滞条件 | 2026-10-20 |
| 第三十八轮 | 机器人/流程节点分组与标定输出语义 | 2026-10-20 |
| 第三十九轮 | 机器人执行动作的参数条件显示 | 2026-10-20 |
| 第四十轮 | 标定输入的示例与条件显示 | 2026-10-20 |
| 第四十一轮 | 标定参数适用性、帮助纠错与输出收敛 | 2026-10-20 |
| 第四十二轮 | 清理永不产出的 overlay/preview 输出 | 2026-10-20 |
| 第四十三轮 | 采集与文本校验的适用性 | 2026-10-20 |
| 第四十四轮 | 计量与平面映射的 JSON 编辑 | 2026-10-20 |
| 第四十五轮 | 推理输出的适用性 | 2026-10-20 |
| 第四十六轮 | 收口审查 | 2026-10-20 |
| 第四十七轮 | 多路分支的默认值与编辑一致性 | 2026-10-22 |
| 第四十八轮 | 画布端口提示与连线校验一致性 | 2026-10-22 |
| 第四十九轮 | 列表节点的整数参数契约 | 2026-10-22 |
| 第五十轮 | 全量节点审查与目录源语言收口 | 2026-10-22 |
| 第五十一轮 | 高级算子选项中文补齐 | 2026-10-22 |
