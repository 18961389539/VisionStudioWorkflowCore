# 节点设计改进

这条线只做一件事：把「节点目录 / 参数 / 端口」收敛成一份可校验的契约。
本文件保留**现状、最近轮次与历史索引**；第一 ~ 五十一轮（2026-10-19 → 2026-10-22）的完整过程
已归档到 [`docs/archive/node-design-improvements-rounds-1-51.md`](archive/node-design-improvements-rounds-1-51.md)。

## 现状（2026-11-10）

| 指标 | 当前值 | 守卫 |
| --- | --- | --- |
| 目录条目 | **242** | `scripts/static-audit.py`：目录 ⊆ 执行器、参数契约无未声明读取 |
| 内置执行器 | 256 类型（含 14 个旧类型别名） | `BuiltInNodeExecutors.FindDrift`，不一致则**启动失败** |
| 兼容别名 | 仅 `Nodes/MergedNodeAliases.cs`，只转发不复制算法 | 目录外的执行器必须在 `NodeTypeMigration.ReplacementMap` 里声明 |
| 参数分组守卫 | 通过 | `nodeValidation.test.ts`（47/47） |
| 目录参数契约 | 242 节点，无未声明读取 | `static-audit.py` 的 catalog 检查 |
| 输出端口契约 | 242 节点，**源码目录 = 发布目录（逐端口、逐 Required）** | `NodeOutputContractTests`：任何擦端口/改必需位即失败；执行器缺必填输出即运行时报错 |
| 前端契约 | 生成 `api-types.ts` + **54 个同名 DTO 编译期对账**；目录/报告 DTO 已从 schema 派生 | `apiContractParity.ts` / `apiContract.test.ts` / `npm run build` |
| 交付闸门 | 全绿（覆盖率、警告、发布回归、e2e 见第九十四轮） | `scripts/quality-gate.ps1` |

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


## 第六十八轮：P1 模型资产进入运行时依赖清单（2026-11-10）

P1 的第一块不是新造一套 pin 格式，而是复用已有的 `RuntimeDependencyManifest`——它已经能锁定
引擎、插件、相机、设备、机器人、标定，模型资产只是同一张清单里的一类依赖。

| 变更 | 说明 |
| --- | --- |
| 清单项 | 新增 `ModelAssetRuntimeDependency(AssetId, Version, Sha256)`，`RuntimeDependencyManifest` 末尾追加 `ModelAssets`（老清单反序列化为 null） |
| 发布时锁定 | 扫描 `match.shape` 的 `modelAssetId`；`modelAssetVersion=0`（latest）**在发布时就地落成具体版本号**，之后追加新版本不会改变已发布配方的锁定；资产/版本不存在直接拒绝发布 |
| schema | 仅当出现模型资产锁定时用 **schema 8**；不含资产的流程继续走 3/4/5/7，历史 `ManifestHash` 逐字节不变 |
| 运行前校验 | 复用 `ValidateAsync` / `ValidateOrThrowAsync`（生产运行路径已在用）：按 `id:vN` 比对 sha，漂移即拒绝启动，kind=`ModelAsset` |

### 验证

- 新增 `ShapeModelAssetManifestTests` 4 项：latest 落成具体版本且哈希稳定、无资产流程仍走旧 schema、
  发布后改成 v2 会报 `ModelAsset` 漂移、引用不存在的资产拒绝发布。
- Engine **2147 项通过**；Api **474 项通过**；`static-audit.py` exit 0。
- 未做（P1 剩余）：资产列表管理页、未引用版本 GC、`.vsasset` 导入导出（P2）。


## 第六十九轮：模型资产管理页（2026-11-10）

P1 的第二块：让人能看见资产、改得动元数据、删得安全。**先做管理页再做 GC**——看不见引用关系就删版本，容易误删。

| 层 | 变更 |
| --- | --- |
| 存储 | `UpdateAsync`：只改名称/描述，版本字节与版本号一律不动；空白名字拒绝、资产不存在返回 null |
| 端点 | `PATCH /api/model-assets/{id}`（重命名，`RequireEngineer` 审计）；`GET /api/model-assets/{id}/references` 返回 `{workflowId,nodeId}`；删除时的 409 文案复用同一份引用扫描 |
| 前端 | 新增 `ModelAssetPanel`：列表（名称/最新版本/大小/版本数/更新时间）、行展开看版本（SHA/大小/训练指纹/说明/时间）、重命名、引用清单、删除；接入命令面板、工具菜单与懒加载面板 |
| 守卫 | 路由清单 279 → 281 |

前端不重复实现引用判断：删除返回 409 时直接展示服务端文案（含节点清单），避免两套规则漂移。

### 验证

- 存储测试 9 项（+`Update_RenamesMetadataWithoutTouchingVersions`：改元数据不动字节、只改描述、空白名/不存在资产明确失败）。
- 面板测试 3 项：列表渲染、引用弹窗、删除被拒时展示服务端引用文案。
- Api **475 项通过**；前端 `tsc -b` 0 error、94 文件里 93 通过（`propertyPanel` 全量运行偶发失败、单独复跑通过，与既有 flake 一致）；审计 exit 0。


## 第七十轮：未引用版本回收（P1）（2026-11-10）

回收的难点不在删除，而在**证明没人用**。这轮把"保护规则"写成代码，并让预览与执行分离。

| 保护对象 | 理由 |
| --- | --- |
| 每个资产的 `latest_version` | 节点里 `modelAssetVersion=0` 指向它 |
| 活工作流节点显式引用的 `(assetId, version)` | 正在用的模型 |
| 任一配方版本的工作流引用 | 配方可被回滚重新发布 |
| 任何历史发布清单里锁定的 `(assetId, version)` | 含发布时由 0 解析出的具体版本；追溯与回放要用 |

| 机制 | 说明 |
| --- | --- |
| `GET /api/model-assets/gc/preview` | 只算候选：`candidates` + 可释放字节 + 被保护计数，不改数据 |
| `POST /api/model-assets/gc` | 必须 `confirm=true`，否则 400；`RequireEngineer` 审计 |
| 内容寻址回收 | 先删版本行，再删"没有任何版本引用"的 blob——共享字节不会被误删 |
| 界面 | 管理页「清理未引用版本」→ 预览弹窗（候选表 + 保护说明）→ 显式确认 |

### 验证

- 后端 5 项：只有旧版本成为候选、latest 保护、活工作流保护、历史清单保护、共享字节不被误删、无候选时幂等。
- 面板测试 4 项（+预览与 confirm 请求体断言）；Api **480 项通过**；前端 `tsc -b` 0 error、
  全量 91 文件通过（`modelAssetPanel` / `requestTimeout` 因 vitest worker 启动超时未计入，单独复跑均通过）。
- 路由清单 281 → 283；审计 exit 0。


## 第七十一轮：.vsasset 导入导出（P2 第一块）（2026-11-10）

跨项目分发需要"一个文件说清一个模型"。`match.template` / `inference.*` 的复用牵涉资产类型扩展，
先做自包含、可验证的包格式。

| 项 | 决定 |
| --- | --- |
| 包格式 | `visionstudio.model-asset.v1`：源资产 id/名称、版本、sha256、字节大小、训练配置指纹、源图哈希、说明、base64 字节 |
| 自校验 | 导入时重算 sha256 并与声明比对，不符直接拒绝——分发链路上任何一层改动都会被发现 |
| 导入语义 | **永远创建新资产 id**，名称追加来源（`from <id> v<n>`）；不覆盖本机同名资产，避免跨库 id 撞车 |
| 端点 | `GET /api/model-assets/{id}/versions/{v}/export`（下载 `.vsasset`）；`POST /api/model-assets/import`（`RequireEngineer` 审计） |
| 界面 | 版本行「导出」；顶部「导入 .vsasset」选文件后解析提交，成功后刷新列表 |

### 验证

- 后端 4 项：导出包自洽（format/哈希/大小/JSON 可反序列化）、往返导入生成新资产且字节一致、
  篡改字节被哈希拒绝、未知格式与缺失哈希被拒绝。
- 面板 5 项（+导入解析与请求体断言）；Api **484 项通过**；前端 `tsc -b` 0 error、
  全量 92 文件通过（`apiClient` / `featureRegionCatalog` 因 worker 启动超时未计入，单独复跑通过）。
- 路由清单 283 → 285；审计 exit 0。


## 第七十二轮：通用 blob 资产层 + 类型适配器（P2）（2026-11-10）

三种资产（形状模型 / 模板图 / ONNX 包）各养一套表和一套 GC 是明显的重复。这轮把存储抽成通用层，
**形状退化为适配器**，对外 API 与行为不变。

| 层 | 变更 |
| --- | --- |
| schema v25 | 新增 `assets(kind)` + `asset_versions(metadata_json)` + `asset_blobs(sha256)`；把 v24 的 `shape_model_*` 数据**幂等搬迁**（`INSERT OR IGNORE` + `json_object`）；旧表保留一个版本用于回退 |
| 通用存储 | `AssetStore`：kind 校验、metadata 必须是 JSON 对象、16 MB 上限、内容寻址去重、版本并发重试、同步读取（latest 解析） |
| 适配器 | `ShapeModelAssetStore` 只做 `kind="shape"` 与 `trainingConfig/sourceImageSha256 ↔ metadata_json` 的映射；签名、异常文案、16 MB 限制全部保持原样 |
| GC | 改为扫 `asset_versions`；**并写下前置警告**：候选覆盖所有 kind，但引用扫描目前只认 shape，新增类型必须同步扩展，否则会误回收 |
| 隔离 | 形状适配器读不到别的 kind：`GetAsync` 返回 null、`AppendVersionAsync` 报 "not 'shape'" |

### 验证

- **回归护栏**：shape 既有 25 项测试（存储 9 / 回收 5 / 包 4 / 清单 4 / 抽取 3）在零改动下全部通过——
  这是"抽取没有改变行为"的证据。
- 新增通用层 4 项：kind 隔离、任意 JSON metadata 往返、同步读取解析 latest、非法 kind/metadata 被拒绝。
- Api **488 项通过**；`static-audit.py` exit 0；`shape` 相关路由与前端无改动。

### 后续接入新 kind 的检查项

1. 适配器（kind + metadata 映射）；
2. 引用扫描：`ShapeModelAssetGc.Collect` 与 `RuntimeDependencyManifestService` 捕获；
3. 发布清单的 pin 类型（若要参与生产运行校验）。


## 第七十三轮：模板图资产（第二种 kind）（2026-11-10）

通用资产层是否成立，要用第二种类型来验证。这轮接 `match.template` 的模板图，只动了四处：

| 层 | 变更 |
| --- | --- |
| 适配器 | `TemplateAssetStore`（kind=template）：metadata 存 `width/height/contentType`，没有训练指纹；content-type 白名单、宽高 1..16384 |
| 节点 | `match.template` 新增 `templateAssetId` / `templateAssetVersion`；优先级 **端口 > 资产 > 模板文件**；资产缺失或解码失败都明确报错，不回退到文件 |
| 发布清单 | 模板资产与模型资产**共用同一份 pin 列表**（`assetId/version/sha` 与 kind 无关），schema 仍是 8，历史哈希不变 |
| 回收 | GC 的引用扫描补上 `match.template`，并继续保留"新增 kind 必须同步扩展"的前置警告 |
| 端点 | 6 个：list/create/get/versions/blob/delete（删除前扫描引用，409 列节点）；路由 285 → 291 |

### 验证

- Api 4 项模板资产测试：元数据与字节往返、两种 kind 读隔离（互相读不到、串用报 `not 'template'`）、非法 content-type 与宽高拒绝、
  引擎读取走同一条 `AssetStore.ReadBytesSync`。
- 清单 1 项：`match.template` 引用模板资产时，`version=0` 在发布时落成具体版本并写入 sha。
- Engine 2 项：无资产源 → 明确失败；字节无法解码 → 在解码处失败（不误报"资产不存在"）。
- Api **493 项通过**；Engine **2149 项通过**；前端 `tsc -b` 0 error、93 文件通过
  （`useTuningPreview` 因 worker 启动超时未计入，单独复跑通过）；审计 exit 0。

### 未做

模板资产目前只能通过 API 创建，管理页还没有上传入口；`.vsasset` 导出也只覆盖模型资产。
这两块是后续小批次，不影响节点侧使用。


## 第七十四轮：`match.template` 的「从资产选择」（2026-11-10）

上一轮模板资产只能通过 API 创建、id 靠手填，节点侧实际不可用。这轮补上选择入口。

| 项 | 说明 |
| --- | --- |
| 位置 | 配置弹窗顶部（外部变更提示下方），`match.template` 专属；不在参数分组里，避免被折叠或筛选藏起来 |
| 组件 | `TemplateAssetPicker`：显示当前绑定（`id · vN` 或"未绑定"）、「从资产选择」「清除」 |
| 列表 | 名称/ID、最新版本、图像尺寸、字节大小；点「选用」把 `templateAssetId` + `templateAssetVersion=latest` 写进草稿 |
| 边界 | 不改变执行优先级（端口 > 资产 > 模板文件）；面板里明确写出这一点 |

### 验证

- 选择器 2 项：列表渲染 + 选用回调 `(id, latestVersion)`；清除回调 `('', 0)`。
- 8 个配置对话框测试通过；前端全量 **95 文件通过**、`tsc -b` 0 error；审计 exit 0（无路由变化）。
- 仍未做：模板图资产的上传入口（管理页），目前创建仍需调用 `POST /api/template-assets`。


## 第七十五轮：模板图上传入口（2026-11-10）

「上传 → 选用 → 运行」这条链路最后缺的是创建入口。这轮补上，并把尺寸判断挪到服务端。

| 层 | 变更 |
| --- | --- |
| 后端尺寸 | `ResolveDimensions`：用 `Cv2.ImDecode` 解码，尺寸**以解码结果为准**；声明值与实际不符直接拒绝（静默纠正会掩盖调用方 bug）；无法解码同样拒绝 |
| 前端上传 | 管理页新增「模板图资产」弹窗：`TemplateAssetTable` 上传本地图片（只传字节，不猜尺寸）、列表（尺寸/大小/版本）、删除（409 引用文案） |
| 命名 | 文件名去掉扩展名作为资产名；`contentType` 取 `file.type`，服务端白名单校验 |

### 验证

- 后端 9 项模板/清单测试全部改用**真实 PNG**（`ImEncode` 生成）：随机字节不再算合法输入，正好覆盖新校验；
  新增"声明尺寸不符"与"无法解码"两条拒绝路径。
- 前端 2 项：列表显示解码尺寸；上传只发 `width:0,height:0` 并带 base64（尺寸由服务端决定）。
- Api **493 项通过**；前端 `tsc -b` 0 error、96 文件中 95 通过
  （`securityGate` 单独复跑通过，属既有偶发）；审计 exit 0。

### 说明

工作区里还有两处**非本轮**的改动（`BarcodeQualityNode` 的分位对比度、`FeatureMatchNode` 一行修正），
未打包进本次提交，留给作者确认后再提交。


## 第七十六轮：组合变换顺序与交付闸门修复（2026-11-10）

第七十六轮设计审查发现 `coordinate.transformCompose` 的编译期帧校验没有读取 `order`：
编译器始终按 A×B 校验，而运行时 `order=innerOuter` 执行 `Compose(B, A)`。结果是合法的反向链路
被编译期误拒，不合法的链路通过校验后在运行期才抛帧不匹配。

| 层 | 变更 |
| --- | --- |
| 编译器 | `ResolveTransformFrames` 的 Compose 分支按 `order` 交换 primary/secondary 后再校验中间帧衔接，与 `ComposeTransformNode` 的 `Compose(B, A)` 对齐 |
| 目录 | `coordinate.transformCompose` 长描述写明两种顺序的衔接要求；`catalog.generated.json` 重新生成（hash `5bba52d1…`） |
| i18n | 节点描述与 `order` 参数说明同步补充 outerInner / innerOuter 的语义 |

### 交付闸门修复

- 上一批改动引入的 6 条新告警在源码修复（`GetGenericIndexer` → `GetArray` 单次取像素、
  循环外缓存 `Rows`/`Cols`），没有靠改基线放行；基线由 28 条裁剪到 22 条。
- 修复 `VisionStudioServiceRegistration.cs` 中 4 处 GBK 乱码注释（含监听优先级、维护互斥、构造环说明）。

### 验证

- 新增 2 项编译期用例：innerOuter 反向链路通过；同一接线在 outerInner 下被拒绝。加上既有 compose 用例共 4 项通过。
- Engine **2206** 项、Api **496** 项通过；合并行覆盖率 **84.29%**、分支 **62.12%**（阈值 81/60）。
- 前端 **96 文件 / 1162 项**通过；覆盖率 语句 69.43 / 分支 66.02 / 函数 59.82 / 行 73.97（阈值 68/65/59/73）；
  `npm run build` 通过。
- `static-audit.py` exit 0（Fact=1611）、metric floor、warning baseline、publish data separation、
  data migration、Bash publish 回归 23 项、`CatalogExporter --check` 均通过。
- e2e 7 项通过。本机 Playwright Chromium 缓存缺失且下载无进展，临时改用系统 Edge（同 Chromium 内核）验证后已还原配置；
  CI 会执行 `npx playwright install chromium`。

## 第七十七轮：面板请求竞态、机器人追踪保留与 e2e 稳定性（2026-11-10）

### 数据集 / 回放面板

| 场景 | 变更 |
| --- | --- |
| 数据集面板 | 切换数据集立即清空旧明细、运行列表与待添加选择；明细与运行详情请求带代次 + `AbortController`，迟到的旧响应直接丢弃；网络失败落到 `DataErrorAlert`，不再产生未处理拒绝 |
| 回放调试器 | `runId` 变化时清空上下文/结果并中止在途的上下文与回放请求；回放结果按发起时的 runId 校验，迟到的旧结果不会显示在新运行标题下；`destroyOnClose` → `destroyOnHidden`（antd 6 弃用） |

新增 4 项回归用例（数据集 2、回放 2）：用 deferred fetch 复现"旧请求后到"，断言旧数据不会覆盖新选择。

### 机器人命令追踪保留

- 新增 `RobotTraceRetentionOptions`（默认 30 天 / 20000 文件 / 每日清理）与 `RobotTraceRetentionHostedService`；
  存储维护激活时让出，24 小时内文件不按数量删除，避免清理正在排查的证据。
- `CleanupAsync` 按 `SelectForDeletion` 纯函数删除过期/超额文件并回收空日期目录；
  新增 5 项测试（时间过期、数量上限、一天内保护、禁用开关、重复执行幂等）。

### e2e 稳定性

- Ctrl+K 用例在 7 worker 并发时可能抢在全局键盘监听挂载前按键；补画布可见性等待后复跑 7/7 通过。
  本机仍无 Playwright Chromium 缓存，验证用系统 Edge（同内核），配置已还原。

### 验证

- Engine **2206**、Api **501**、前端 **98 文件 / 1166 项** 通过；合并行覆盖率 **84.26%**、分支 **62.15%**（阈值 81/60）。
- 前端覆盖率：语句 68.95 / 分支 65.15 / 函数 59.27 / 行 73.57（阈值 68/65/59/73）。
- `static-audit.py` exit 0（Fact=1616）、metric floor（Api 460 / 前端 98）、warning baseline 22 条无新增、
  `npm run build`、e2e 7 项通过。

## 第七十八轮：并行降级显式披露（2026-11-10）

`flow.parallel` 在分支含设备/控制节点、或位于循环体时，编译产物会退化为 Sequence 顺序执行。
此前编译响应与界面没有任何标记，与"不静默降级"原则冲突。本轮把退化变成**可见的编译提示**，
不改变执行语义，也不把提示当成校验失败。

| 层 | 变更 |
| --- | --- |
| 编译器 | `CompiledWorkflow` 新增 `Warnings`；`CompileParallel` 未融合时发出 `flow.parallel.sequential`，消息点名阻塞项（具体节点/嵌套控制结构/循环体）；`TryCollectFusedBranchNodes` 返回 blocker 供提示复用 |
| API | `/api/validate`、`/api/compile` 响应新增 `warnings`（code / message / nodeId），可融合流程仍为空数组 |
| 前端 | 校验链解析 warnings，格式非法按"响应无效"拒绝；提示进入"校验问题"列表（金色「提示」标签）并可点击定位节点；摘要追加"N 条编译提示"；`validate()` 仍返回通过 |

### 验证

- 新增 3 项编译器用例：可融合分支无提示且确实进入同一流水线段；不可融合分支提示点名 `flow.result`；
  循环体内的 Parallel 提示融合被禁用。
- 新增 2 项前端用例：提示不阻断校验并进入问题列表；非法 warnings 载荷被拒绝而不是静默丢弃。
- Engine **2209**、Api **501**、前端 **98 文件 / 1168 项** 通过；合并行覆盖率 **84.25%**、分支 **62.15%**（阈值 81/60）。
- 前端覆盖率：语句 68.98 / 分支 65.2 / 函数 59.31 / 行 73.6（阈值 68/65/59/73）；
  `static-audit.py` exit 0（Fact=1619）、metric floor、warning baseline 22 条、`npm run build` 通过。

## 第七十九轮：节点超时诊断接入配置页（2026-11-10）

`/api/diagnostics/node-durations` 早已存在（回答"`__timeoutMs` 该填多少"），但此前只能 curl。
本轮把它接到节点配置页的超时输入下方，工程师不用离开画布即可按实测 P95 选值。

| 场景 | 展示 |
| --- | --- |
| 样本 < 20 | `耗时样本 N/20，P95 X 毫秒（样本不足，暂不建议超时值）` |
| 样本 ≥ 20 | `P95 X 毫秒 · 建议超时 Y 毫秒` |
| 最近运行触发过超时 | 红色「已触发超时 N 次」 |
| P95 超过最近配置的 80% | 橙色「最近运行配置偏紧」 |

- 按节点类型匹配；端点要求 Operator——无权限、离线或载荷格式异常都只是不显示提示，不打断配置与保存。
- 端点常量收进 `endpoints.ts`（`diagnostics.nodeDurations`），符合前端"禁止硬编码 /api/"棘轮。

### 验证

- 新增 2 项配置页用例：P95 / 建议值 / 超时次数 / 偏紧标签；样本不足时不显示建议值。
- 前端 **98 文件 / 1170 项** 通过；`apiUsage` / `apiContract` / `endpoints` 11 项通过；`npm run build` 通过。
- 前端覆盖率：语句 69.03 / 分支 65.27 / 函数 59.38 / 行 73.67（阈值 68/65/59/73）；
  `static-audit.py` exit 0（Fact=1619）、metric floor 通过。

## 第八十轮：OpenAPI 快照刷新（2026-11-10）

`frontend/src/generated/openapi.json` 自 2026-10-29 起未刷新：模型资产 / 模板资产共 **15 个端点**、
**8 个 schema** 以及 `ParameterDescriptor.advanced` 字段缺失，`openapi-typescript` 生成链会漏掉这些契约。
本轮用 `scripts/export-openapi.ps1` 从当前后端重新导出（临时数据根，不污染本机现场库）：

- 路径 **242 → 257**（+15，全部为 `model-assets` / `template-assets`），**无删除**；
- schema **159 → 167**（+8）；既有 schema 仅 `ParameterDescriptor` 新增 `advanced`；
- `apiContract.test.ts` 增加"已存在端点不回落"棘轮，防止旧快照覆盖回流。

验证：`apiContract` 4 项通过；快照可解析（OpenAPI 3.x）；前端 **98 文件 / 1171 项** 通过。
已知缺口：`/api/validate`、`/api/compile` 仍返回匿名对象，未用 `Produces<T>()` 固化响应 schema。

## 第八十一轮：校验/编译响应进入 OpenAPI 契约（2026-11-10）

`/api/validate`、`/api/compile` 一直返回匿名对象：OpenAPI 里只有 `"200 OK"`，生成不出响应类型，
上一轮新增的 `warnings` 也就无法进入下游契约。本轮把它们固化为具名 DTO：

| DTO | 用途 |
| --- | --- |
| `WorkflowCompileResponse` | 编译成功（含 warnings / moduleDependencies / dslJson） |
| `WorkflowValidationSuccess` | 校验成功（含 registeredNodeTypes / warnings） |
| `WorkflowValidationFailure` | 校验失败（valid=false / error；仍以 400 作为交互反馈，不是 ProblemDetails） |

- 两个端点补 `.Produces<T>()`；JSON 字段仍由 Minimal API camelCase 输出，与既有前端解析逐字段一致。
- OpenAPI 快照重新导出：schema **167 → 173**，`/api/validate` 有 200/400 两个具名响应，`/api/compile` 有具名 200。
- `apiContract` 棘轮加入三个 DTO，防止端点退回匿名对象。

验证：Engine **2209**、Api **501**、前端 **98 文件 / 1171 项** 通过；`apiContract` + `useWorkflowInspection`
50 项通过；OpenAPI 快照可解析且两个端点的响应为 `$ref`；warning baseline 22 条无新增、static-audit exit 0。

真实宿主冒烟（临时数据根 + 安全关闭）：`POST /api/validate` 返回
`valid=true / warnings[0].code=flow.parallel.sequential / moduleDependencies=[] / registeredNodeTypes`，
`POST /api/compile` 返回同结构 + `dslJson`；空流程返回 `400 {"valid":false,"error":"Workflow has no nodes."}`，
失败契约与旧前端解析保持一致。

## 第八十二轮：资产域端点响应 schema（2026-11-10）

上一轮固化校验/编译响应后，资产域（模型/模板）端点仍是匿名对象：OpenAPI 只有 `"200 OK"`，
资产面板与训练工作区无法使用生成类型，字段漂移也无从断言。本轮为 **15 个 JSON 端点**补 `.Produces<T>()`：

| 域 | 端点 | 响应 |
| --- | --- | --- |
| 模型资产 | list / create / get / patch | `ShapeModelAssetDescriptor` |
| 模型资产 | 版本追加 / 版本读取 | `ShapeModelVersionInfo` |
| 模型资产 | import | `ShapeModelAssetDescriptor` |
| 模型资产 | gc preview / gc apply | `ShapeModelAssetGcPlan` |
| 模型资产 | references | `ShapeModelAssetReference[]` |
| 模型资产 | extract | `ExtractShapeModelAssetResponse` |
| 模板资产 | list / create / get | `TemplateAssetDescriptor` |
| 模板资产 | 版本追加 | `TemplateAssetVersionInfo` |

OpenAPI schema **173 → 181**；`apiContract` 棘轮加入资产域 DTO（descriptor / version / gc / extract / template），
防止这些端点退回匿名对象。二进制下载端点（blob / export）保持文件响应，不在此列。

验证：Engine **2209**、Api **501**、前端 **98 文件 / 1171 项** 与 `npm run build` 通过；`apiContract` 4 项通过；
warning baseline 22 条无新增；static-audit exit 0、metric floor 通过。

## 第八十三轮：OpenAPI 生成类型与编译期对账（2026-11-10）

设计审计 P1-1 的核心风险是 `types.ts` 这份约 1,784 行手写镜像：后端契约变了前端不会报错。
本轮引入生成类型与编译期对账，作为迁移手写类型前的第一道闸门：

- `src/generated/api-types.ts`：由 `openapi-typescript` 从 OpenAPI 快照生成。
  openapi-typescript 7 的 peer 是 TypeScript 5.x，而项目是 TS 7，因此通过隔离的 TS5 环境执行
  （`npm run generate:api-types`，固定 TS 5.9.3 + openapi-typescript 7.13.0），**不把工具装进项目依赖**。
- `src/generated/apiContractParity.ts`：编译期断言"后端生成类型 → 前端手写类型"可赋值，
  覆盖 `WorkflowDefinition` / `NodeCatalogItem` / `WorkflowRunResult` / `RunTraceRecord`。
  .NET 10 OpenAPI 把数值生成成 `number | string`，对账前收敛回 number；后端开放 string、
  前端收窄为联合的字段（`ParameterDescriptor.type`、`RunTraceRecord.disposition`）做显式归一。
- 生成文件参与 `tsc -b`，`npm run build` 即会因契约漂移失败；API 用量棘轮排除 `src/generated/`。

对账第一跑就抓到真实漂移：前端 `NodeOutputFrameSource` 缺少后端已使用的
`ImageOutputFrame`（image.warpAffine）与 `OutputTransformTarget`（region.warpAffine），已补齐。

验证：前端 **98 文件 / 1171 项** 通过；覆盖率 69.03 / 65.27 / 59.38 / 73.67（阈值 68/65/59/73；
首跑有 2 项 worker 偶发失败，复跑全过）；`npm run build` 通过；static-audit exit 0（Fact=1619）、metric floor 通过。

## 第八十四轮：帧校验去类型名单 + Vitest 并发封顶（2026-11-10）

### 帧校验不再维护类型名单

`ValidateMeasurementImageFrames` / `ValidateMeasurementGeometryFrames` 此前各自硬编码 6 个测量类型；
新增测量节点若忘改名单，会静默跳过"几何/图像必须与 transform 帧一致"的校验。
现改为目录语义判定 `SamplesGeometryAgainstTransformedImage`：带 `transform`(Transform2D) + `image`(Image) 输入、
且图像帧策略不是 `TransformTarget` 的节点即受检（`image.warpAffine` 的图像是 transform 结果，
由 `ValidateImageWarpFrames` 单独校验）。对现有 242 个节点行为等价，新增节点自动受保护。

新增 1 项用例：自定义 `test.measure` 目录项（非内置类型）连接不匹配的图像/transform 帧，编译被拒绝。

### Vitest 并发封顶

本机 28 核时 Vitest 默认拉起 ~27 个 worker，每个创建 jsdom，是历史"worker 启动超时"偶发失败源。
`vite.config.ts` 增加 `maxWorkers: 8`（CI 4 vCPU 自然低于该上限）。全量测试 56s、
覆盖率 81–89s（此前约 60s），两次覆盖率复跑均 1171 项全绿。

### 验证

Engine **2210**、Api **501**、前端 **98 文件 / 1171 项** 通过；后端覆盖率 行 **84.22%** / 分支 **62.16%**
（阈值 81/60）；前端覆盖率 69.03 / 65.27 / 59.38 / 73.67（阈值 68/65/59/73，连续两次复跑）；
`npm run build` 通过；warning baseline 22 条无新增；static-audit exit 0（Fact=1620）、metric floor（Engine 1338）通过。

## 第八十五轮：参数默认值解析收敛为单一实现（2026-11-10）

设计审计第 3 项：Engine/Api 有 5 份"节点值优先、目录默认值兜底"的实现，且多处用 CurrentCulture
格式化数值默认值。在逗号小数点区域（de-DE / fr-FR）默认值 `0.5` 会被读成 `"0,5"`，
动作声明、frame 条件、端口必需性、运行时依赖与参数可见性会对同一个节点给出不同结论。

新增 `Abstractions/NodeParameterValue.cs` 作为唯一实现：

- 节点显式标量按 JSON 原文（数字取 raw text，布尔为小写 `true/false`）；
- 目录默认值用 invariant 文本（`JsonElement` 字符串还原、数字取原文）；
- 无节点值且无参数描述符 → `null`。

替换 8 处调用：`PortDescriptor.IsRequired`、`NodeActionContract`、`OperatorParameterForms.Resolve/Validate`、
`NodeFrameSemantics`、`ParameterTierCatalog`、编译器参数可见性判断、`RuntimeDependencies`
（声明资源值 / 资产字符串 / 版本整数）。新增 3 项测试，其中 de-DE 文化下动作条件与端口必填
仍按 `"0.5"` 成立——旧实现会失败。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.27%** / 分支 **62.17%**（阈值 81/60）；
warning baseline 22 条无新增；static-audit exit 0（Fact=1623）、metric floor（Engine 1341）通过。

## 第八十六轮：手写枚举收敛到生成类型（2026-11-10）

`types.ts` 中 9 个与后端完全一致的枚举联合改为生成类型别名（`ContractSchema<'X'>`）：

`NodeImageFramePolicy`、`NodeTransformFramePolicy`、`NodeOutputFrameSource`、`NodeMaturity`、
`CameraTriggerMode`、`CameraOutputPixelFormat`、`DeviceConnectionState`、`DeviceTagQuality`、`DeviceTagDataType`。

- 类型行为不变（`tsc` 全量通过）；后端新增枚举值会直接流入前端，不再需要双份维护。
- `AssetKind` 保持手写：后端 schema 含 `null`，前端语义不允许 null，属于刻意收窄而非镜像。
- 生成类型自此成为运行时源码的编译依赖（`types.ts → generated/api-types.ts`），
  契约漂移会在 `npm run build` 阶段直接暴露。

验证：前端 **98 文件 / 1171 项** 通过；覆盖率 69.03 / 65.27 / 59.38 / 73.67（阈值 68/65/59/73）；
`npm run build` 通过；static-audit exit 0（Fact=1623）、metric floor 通过。

## 第八十七轮：JsonElement 进入生成类型（2026-11-10）

`NodeDefinition.parameters` / `ParameterDescriptor.defaultValue` 等 `JsonElement` 字段在 OpenAPI 里是
"object 无属性"，openapi-typescript 会生成 `Record<string, never>`——`WorkflowPayload` 等 DTO 因此无法迁移到生成类型。
新增 `JsonElementSchemaTransformer`（通过 `AddOpenApi(options => ...)` 注册）：

- 直接出现的 `JsonElement` → 空 schema（生成 TS `unknown`）；
- `Dictionary<string, JsonElement>` → 保留原 `null` 类型位并补 `additionalProperties: {}`（生成 `Record<string, unknown>`）。

结果：`NodeDefinition.parameters` 由 `null | Record<string, never>` 变为 `null | { [key: string]: unknown }`；
`ParameterDescriptor.defaultValue` 等直接字段变为 `unknown`。OpenAPI 快照与 `api-types.ts` 已重新生成；
`apiContract` 增加守卫（`parameters.additionalProperties === {}`、`defaultValue` 为空 schema），
static-audit 同步识别带 options 的 `AddOpenApi(` 并校验 transformer 已注册。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.24%** / 分支 **62.19%**（阈值 81/60）；
前端 **98 文件 / 1172 项** 与 `npm run build` 通过；前端覆盖率 69.03 / 65.27 / 59.38 / 73.67；
warning baseline 22 条无新增；static-audit exit 0（Fact=1623）、metric floor 通过。

## 第八十八轮：WorkflowPayload 迁移到生成 schema（2026-11-10）

`types.ts` 的 `WorkflowPayload` 不再手写整份结构，改为从 `ContractSchema<'WorkflowDefinition'>` 派生：
`Omit` 掉 `nodes / edges / schemaVersion` 后只覆盖 UI 需要的宽化——

- `node.position` 的生成类型是 `number | string`（.NET 数值 schema），UI 使用 `number`；
- `name / position / parameters` 允许省略；`edge` 去掉计算属性 `isControl`。

字段名与基础类型由后端 schema 锁定，`WorkflowPayload ↔ WorkflowDefinition` 的漂移同时受
`apiContractParity` 编译期对账保护。`NodeCatalogItem` 暂保持手写（参数/端口类型比 schema 更窄），
留待后续按模块迁移。

验证：`tsc` / `npm run build` 通过；前端 **98 文件 / 1172 项** 通过；覆盖率不变
（69.03 / 65.27 / 59.38 / 73.67）；static-audit exit 0、metric floor 通过；后端未改动。

## 第八十九轮：目录/报告 DTO 迁移与 object 载荷（2026-11-10）

- `NodeCatalogItem` 从 `ContractSchema<'NodeCatalogItem'>` 派生：参数、端口、能力位、帧声明保留前端更窄的类型覆盖，
  其余字段（含 `actions / resources / declares*`）自动继承；`VisionToolCapabilities.executionMode` 改用生成枚举。
- `NodeRunReport` 从 schema 派生：结构继承，仅把耗时/序号/时间偏移覆盖为 `number` 且允许省略（旧记录无时间线）；
  `VisionValueSnapshot` 直接别名生成类型，`apiContractParity` 增加 `NodeRunReport` 对账。
- `JsonElementSchemaTransformer` 扩展到 `object` 与 `IReadOnlyDictionary<string, object?>`：
  `NodeRunReport.summary`、`DeviceBatchWriteRequest.values` 由 `Record<string, never>` 变为 `Record<string, unknown>`。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.24%** / 分支 **62.21%**（阈值 81/60）；
前端 **98 文件 / 1172 项** 与 `npm run build` 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第九十轮：DTO 编译期对账扩到 50 个（2026-11-10）

自动比对 `types.ts` 与 OpenAPI schema 的 54 个同名类型，把结构已对齐的 **45 个**全部加入
`apiContractParity`（连同原有 5 个共 **50 个**），覆盖 alarm / job / device / camera / robot / plugin /
production / port / parameter 等域。排除 9 个并在文件里写明原因：

- OpenAPI 把带 C# 默认值或计算属性的必填字段标成 optional（`CameraSettings.triggerMode/outputPixelFormat`、
  `RobotDescriptor.currentPose.x/y`、`RunCounterPersistenceStatus.degraded`、
  `CalibrationWorkspaceRequest.verificationPoints`）——后端实际总会序列化，前端保持 required 是对的，
  属于 schema 表达不精确，不应放宽前端类型；
- `NodeCatalogItem.parameters.type`、`TracePage → RunTraceRecord.disposition` 是前端刻意收窄（已有单独的归一化对账）；
- `CameraCommissioningProfile` 继承 `CameraSettings`。

验证：`tsc` / `npm run build` 通过；前端 **98 文件 / 1172 项** 通过；覆盖率不变；static-audit exit 0；后端未改动。

## 第九十一轮：required 表达修正与 53 个 DTO 对账（2026-11-10）

.NET OpenAPI 会把"带默认值的构造参数"和"只读计算属性"标成 optional，生成类型因此比实际响应宽松
（如 `CameraSettings.triggerMode`、`RunCounterPersistenceStatus.degraded`）。新增
`RequiredPropertiesSchemaTransformer`：

- 按 CLR 可空性补 required：值类型（非 `Nullable<T>`）与 NRT 标记为非空的引用类型；
- 尊重条件序列化：带 `JsonIgnore(WhenWritingNull/WhenWritingDefault)` 的属性保持 optional
  （`NodeMaturity` 缺省 Stable 不写 JSON 的行为不变）；
- `readonly record struct` 的属性默认总会序列化，整体按 required 表达。

结果：`CameraSettings` / `CameraCommissioningProfile` / `RunCounterPersistenceStatus` /
`CalibrationWorkspaceRequest` 纳入编译期对账（**对账总数 53**）；`WorkflowDefinition.schemaVersion`
变为 required（响应必含）。`RobotDescriptor` 例外：其 `currentPose / activeTarget` 是 readonly record struct，
ASP.NET OpenAPI 未对该 struct schema 应用 transformer，继续人工核对。`apiContract` 增加 required 守卫。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.24%** / 分支 **62.21%**（阈值 81/60）；
前端 **98 文件 / 1173 项** 与 `npm run build` 通过；warning baseline 22 条无新增；
static-audit exit 0（Fact=1623）、metric floor 通过。

## 第九十二轮：struct schema required 与 54 个 DTO 全覆盖（2026-11-10）

一次性探针确认：ASP.NET OpenAPI **不会对 readonly record struct 调用 schema transformer**，
`VisionRobotTarget2D` 的字段因此始终 optional（尽管默认序列化总会输出，包括 null）。
新增文档级 `StructSchemaRequiredDocumentTransformer`：对已知 struct schema 的全部属性补 required；
`RobotDescriptor` 重新纳入对账，`TracePage` 用 `NormalizeTraceItems` 覆盖 items 内的 disposition 收窄。
至此 `types.ts` 与 OpenAPI schema 同名的 **54 个 DTO 全部有编译期对账**。

- static-audit 增加"struct 文档 transformer 必须注册"守卫（与 schema transformer 守卫并列）。
- 验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.23%** / 分支 **62.21%**（阈值 81/60）；
  前端 **98 文件 / 1173 项** 与 `npm run build` 通过；warning baseline 22 条无新增；
  static-audit exit 0、metric floor 通过。

## 第九十三轮：数据集/验证域响应 schema（2026-11-10）

数据集/批量验证是前端核心面板，但 `/api/validation/**` 的响应一直是匿名对象。本轮为 **9 个 JSON 端点**补
`.Produces<T>()`：datasets（list/create/get、items add/update）、runs（start/list/get）、run workflow。

- OpenAPI schema **181 → 186**；`ValidationDatasetSummary / ValidationDatasetDetail / ValidationRunRecord`
  进入 `apiContract` 的 DTO 与路径守卫。
- 新增 4 个 validation 类型对账：`sourceKind`、`expectedDisposition` 是后端开放 string、前端收窄的联合，
  用 `NormalizeValidationItem` 显式归一后纳入（`ValidationDatasetItem / Detail / RunRecord / ResultRecord`）。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.25%** / 分支 **62.22%**（阈值 81/60）；
前端 **98 文件 / 1173 项** 与 `npm run build` 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第九十四轮：交付闸门复核（2026-11-10）

本轮不新增功能，逐项复核 `scripts/quality-gate.ps1` 的交付闸门（工作区为 76–93 轮累计改动）：

| 闸门 | 结果 |
| --- | --- |
| `static-audit.py` | exit 0（Fact=1623） |
| `check-metric-floor.py` | 目录 242 / Engine 1341 / Api 460 / 前端 98 |
| publish data separation | PASSED |
| data migration | PASSED |
| bash publish regression | 23 passed |
| `CatalogExporter --check` | current（hash `5bba52d1…`） |
| 后端测试 + 覆盖率 | Engine **2213** / Api **501**；行 **84.25%** / 分支 **62.22%**（阈值 81/60） |
| warning baseline | 22 条，无新增 |
| 前端测试 + 覆盖率 + build | **98 文件 / 1173 项**；69.03/65.27/59.38/73.67；build 通过 |
| e2e | 7/7（本机 Chromium 缓存缺失，用系统 Edge 验证；配置已还原） |

结论：仓库质量闸门全绿，处于可交付候选状态。未完成/不阻塞闸门的遗留：

1. **改动尚未提交**（用户未要求提交；共 53 个工作区文件，其中 11 个新增）。
2. 三个推理节点保持 Experimental：`inference.obb / pose / instanceSegmentation` 缺模型包，无法端到端验收。
3. 编译器仍有少量节点特判：`measure.fitLine/fitCircle` 最小点数、`measure.regionIntensity` 与
   `image.flatFieldCorrect` 参数校验、`caliperCircle + roi.transform` 双变换防护。
4. OpenAPI 仍有大量匿名端点（auth、插件、标定、trace/replay 等），可按域继续补 `Produces<T>()` 与生成类型。

## 第九十五轮：追溯/回放/存储域响应 schema（2026-11-10）

为 trace / storage / replay 共 **18 个 JSON 端点**补 `.Produces<T>()`：traces（stats、单条、observability、
compare、failure-signature、disposition、workflow、dependencies、overlays）、storage（status/schema/capacity/
cleanup/backup list/create/restore）、replay（context/result）。清理接口改用具名 `StorageCleanupResponse`
替代匿名对象。OpenAPI schema **186 → 218**。

`apiContract` 增加 6 个 DTO 与 3 条路径守卫；新增 **20 个类型对账**，其中 5 个字段是后端开放 string、
前端收窄的联合，用显式归一处理：`HardwareProvenanceSnapshot.kind/completeness`、
`NodePerformanceBaseline.status`、`NodeTimelineObservation.phase`、`OfflineReplayContext.trace.disposition`、
`RunObservabilitySnapshot.timeline/baselines`。

验证：Engine **2213**、Api **501** 通过；后端覆盖率 行 **84.25%** / 分支 **62.23%**（阈值 81/60）；
前端 **98 文件 / 1173 项** 与 `npm run build` 通过、覆盖率 69.03/65.27/59.38/73.67；
warning baseline 22 条无新增；static-audit exit 0、metric floor 通过。

## 第九十六轮：清理 match.shape 的不可达 transform 声明（2026-11-10）

`match.shape` 目录声明了 `TransformFrames: TemplateToImage`，但节点输出只有 pose/scale/detections，
没有 `transform` 端口；编译器对该声明的解析永远不可达（pose 需经 `roi.transform` 才生成 transform）。
这是首轮"transform 帧语义声明化"遗留的死契约，会误导维护者以为 match.shape 直接输出 transform。
本轮移除声明，同步目录快照（hash `5bba52d1…` → `c85e33f9…`）与声明测试。

对执行行为、目录条目数（242）与其他节点零影响。验证：Engine **2212**（少一条已删除的声明用例）、
Api **501**；后端覆盖率 行 **84.29%** / 分支 **62.22%**（阈值 81/60）；前端 **98 文件 / 1173 项**
与 `npm run build` 通过；warning baseline 22 条无新增；static-audit exit 0、metric floor 通过。

## 第九十七轮：诊断/目录元信息响应 schema（2026-11-10）

诊断中心与目录元信息此前返回匿名对象。本轮为 **5 个 JSON 端点**补 `.Produces<T>()`：
`/api/diagnostics/summary|assets|events`、`/api/catalog/meta`、`/api/host-mode`；两个匿名对象改为具名
`CatalogMetaResponse` 与 `HostModeStatus`。OpenAPI schema **218 → 225**。

`apiContract` 增加 `DiagnosticsSummary / AssetHealthSnapshot / CatalogMetaResponse / HostModeStatus` 与两条路径守卫；
新增 6 个类型对账（`AssetEventEnvelope / AssetEventSeverity / AssetHealthLevel / AssetHealthSnapshot /
DiagnosticsSummary / RunTraceComparison`）。

验证：Engine **2212**、Api **501** 通过；后端覆盖率 行 **84.28%** / 分支 **62.21%**（阈值 81/60）；
前端 **98 文件 / 1173 项** 与 `npm run build` 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第九十八轮：validate/compile 真实宿主契约测试（2026-11-10）

此前 `/api/validate`、`/api/compile` 的响应形状只由编译器单测和前端 mock 覆盖，真实 Minimal API 的
camelCase JSON、400 失败体与 `dslJson` 没有自动化守卫。新增 `WorkflowContractApiTests`（3 项，
使用 `VisionStudioApiFactory` 真实宿主）：

- `/api/validate` 成功体：`valid / workflowCoreId / registeredNodeTypes / pipelineSegmentCount / warnings`；
- 并行降级警告：`code=flow.parallel.sequential`、`nodeId=p`、消息含 "runs both branches sequentially"；
- 空工作流：400 且 `valid=false`、`error` 含 "no nodes"；
- `/api/compile`：`version=1`、`dslJson` 可解析且含 `Steps`、`warnings` 与 validate 一致。

验证：Engine **2212**、Api **504** 通过；warning baseline 22 条无新增；static-audit exit 0（Fact=1626）、
metric floor（Api 463）通过；后端覆盖率 行 **84.28%** / 分支 **62.21%**（阈值 81/60）。

## 第九十九轮：插件注册表响应 schema（2026-11-10）

为插件注册表 3 个端点补 `.Produces<T>()`：`/api/plugins` → `PluginLoadInfo[]`、
`/api/plugins/sdk` → `PluginSdkInfo`、`/api/plugins/rescan` → `PluginRescanResult`。
OpenAPI schema **225 → 232**；`apiContract` 增加 3 个 DTO 与 2 条路径守卫，新增 3 个类型对账。

验证：Engine **2212**、Api **504** 通过；后端覆盖率 行 **84.29%** / 分支 **62.23%**（阈值 81/60）；
前端 **98 文件 / 1173 项** 与 `npm run build` 通过；warning baseline 22 条无新增、static-audit exit 0。

## 第一百轮：Soak 与 L3 故障注入演练（2026-11-10）

补两类非功能交付证据（不修改代码）：

- **Soak 门禁**：按 CI 参数（250 工作流 / 50 机器人 / 50000 数学迭代）运行 `scripts/soak-test.ps1`，
  Engine 的 3 项 Soak 测试全部通过，输出 `Soak Gate PASSED`。
- **L3 故障注入（实验室模式）**：`scripts/l3-fault-drills.ps1` 全部 PASS——
  A1/A2/A3/A4（动作失败→未决意图→全入口 409 阻断→管理员核对→恢复）、
  B1（未决意图期间调试会话同样被阻断）、C1/C2（重启后意图仍可见、核对后恢复）；
  实测故障→阻断 1530 ms、核对→恢复 141 ms。报告与 CSV 输出到 `artifacts/`（已忽略）。

物理故障（相机断线 / PLC 断电 / 停止不返回 / 磁盘满）仍需现场按脚本输出的 MANUAL 清单执行。

## 第一百零一轮：认证/用户/审计响应 schema（2026-11-10）

为安全域 **12 个端点**补 `.Produces<T>()` 并消灭匿名响应：auth status/login/auto-login/logout/me/
change-password、security users list/create/update/reset-password、audit list。

- 新增具名响应：`SessionUserResponse / LogoutResponse / CurrentUserResponse / ChangePasswordResult /
  ResetPasswordResult`（复用 `SecurityStatus / SecurityUserDto / AuditEventDto`）；JSON 字段名不变。
- OpenAPI schema **232 → 240**；`apiContract` 增加 `SecurityStatus / SessionUserResponse / SecurityUserDto /
  AuditEventDto` 与 3 条路径守卫。

验证：Engine **2212**、Api **504** 通过；前端 **98 文件 / 1173 项** 与 `npm run build` 通过；
后端覆盖率 行 **84.29%** / 分支 **62.22%**（阈值 81/60）；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零二轮：标定/相机/媒体域响应 schema（2026-11-11）

为三个设备相关域补 `.Produces<T>()`，消灭列表/描述符的匿名响应：

- 标定域 **19 个端点**：providers、capture-grid、solve，标定资产与相机内参资产的 list/create/get/
  versions/publish/rollback → `CalibrationProviderDescriptor / CalibrationWorkspacePoint /
  CalibrationWorkspaceResult / CalibrationAssetDescriptor / CalibrationVersionSnapshot /
  CameraIntrinsicAssetDescriptor / CameraIntrinsicVersionSnapshot`。
- 相机域 **11 个端点**：列表、发现、vendor/file 注册、open/close/start/stop/settings/trigger、
  transport-telemetry → `CameraDescriptor / CameraDiscoveredDevice / CameraTransportTelemetry`。
- 媒体域 **5 个端点**：status、collections、items、import → `MediaLibraryStatus /
  MediaCollectionDescriptor / MediaItemDescriptor`。
- OpenAPI schema **240 → 264**；`apiContractParity` 新增 11 个 DTO 对账（相机 state、媒体 label
  在 schema 中是宽 string，前端联合仅做展示收窄），`apiContract` 新增 11 个必需 DTO、10 条路径与
  三个列表域的数组成员守卫。
- 修正 `check-warning-baseline.ps1`：基线是无 BOM UTF-8，PS 5.1 默认 ANSI 读取会把中文注释与首条
  告警粘连，导致已有告警误报为新增；改为显式 `-Encoding UTF8`。

验证：Engine **2212**、Api **504** 通过；前端 **98 文件 / 1174 项** 与 `npm run build` 通过；
warning baseline 22 条无新增；static-audit exit 0（Fact=1626）、metric floor 通过。

## 第一百零三轮：设备/作业/机器人/生产域响应 schema（2026-11-11）

继续把核心运行时域的匿名响应替换为 `.Produces<T>()`（共 **38 个端点**，JSON 字段不变）：

- PLC 设备域 **11 个**：地址模板、modbus/s7 注册、profiles、read-all、批量/单点写标签、
  connect/disconnect/settings、单点读标签 → `DeviceAddressTemplate / DeviceProfile / DeviceTagSample /
  DeviceDescriptor`。
- 作业域 **9 个**：create/get/version/publish/rollback、dependencies、dependencies validate、run →
  `JobDescriptor / JobVersionSnapshot / RuntimeDependencyManifest / RuntimeDependencyValidation /
  WorkflowRunResult`。
- 机器人域 **8 个**：connect/disconnect/stop/reset/settings/ack、命令追溯列表与详情 →
  `RobotDescriptor / RobotCommandTraceRecord`（`/target` 因握手与下发返回两种不同形状保持匿名，
  避免为对齐 schema 引入 null 字段改变响应）。
- 生产运行域 **10 个**：status/config GET+PUT、ptp/synchronization guard、start/stop/recover、
  device-actions resolve、alarm ack → `ProductionRuntimeStatus / ProductionRuntimeConfig /
  ProductionPtpGuardSnapshot / ProductionSynchronizationGuardSnapshot / AlarmRecord`。

OpenAPI schema **264 → 280**；`apiContractParity` 新增 `JobVersionSnapshot /
RuntimeDependencyValidation / RobotCommandTraceRecord / ProductionRuntimeStatus（state 收窄）/
ProductionPtpGuardSnapshot / ProductionSynchronizationGuardSnapshot`。`apiContract` 增加 7 个
必需 DTO 与 14 条路径守卫。

验证：Api **504**、前端 **98 文件 / 1174 项**、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零四轮：相机同步 / 相机特性域响应 schema（2026-11-11）

- 相机同步域 **18 个端点**：组列表/详情/状态/保存、运行记录与统计、PTP 诊断、GigE 网络诊断与
  测试报告、调试测试（列表/启动/详情/取消/报告）、组触发与调度 → `CameraSynchronizationGroup /
  GroupStatus / RunRecord / Statistics / PtpDiagnostics / CommissioningTest / CommissioningReport /
  CaptureResult`、`GigENetworkDiagnostics / CommissioningReport`（启动类返回 202，按
  `StatusCodes.Status202Accepted` 声明）。
- 相机特性域 **9 个 JSON 端点**：commissioning 读取/应用、features 列表与更新、特性配置档案的
  列表/读取/采集/导入/应用 → `CameraCommissioningSnapshot / CameraCommissioningApplyResult /
  CameraFeatureDescriptor / CameraFeatureProfileRecord`；三个匿名响应改为具名 record
  （`CameraCommissioningSnapshot / CameraFeatureUpdateResponse / CameraFeatureProfileApplyResponse`），
  字段名与顺序保持不变。
- OpenAPI schema **280 → 318**；`apiContractParity` 新增 13 个 DTO 对账（全部通过，说明前端镜像与
  后端逐字段一致），`apiContract` 增加 9 个必需 DTO 与 14 条路径守卫。

验证：Api 504、前端 98 文件 / 1174 项、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零五轮：插件包 / 模型包 / 工作流模块域响应 schema（2026-11-11）

- 插件包域 **7 个端点**：包列表、信任发布者列表/导入、预检、安装、版本选择、回滚 →
  `PluginPackageVersionInfo / TrustedPluginPublisher / PluginPackagePreflightResult /
  PluginPackageInstallResult / PluginPackageSelectionResult`。
- 模型包域 **5 个端点**：列表、详情、预检、导入、冒烟 → `ModelPackageSummary / ModelPackageDetail /
  ExternalTrainManifest / ExternalTrainSmokeResult`（`ModelPackageEndpoints` 补
  `VisionStudio.Engine.ExternalTrain` using）。
- 工作流模块域 **5 个端点**：列表、提取、详情、版本读取/保存 → `WorkflowModuleDescriptor /
  WorkflowModuleVersion / ExtractWorkflowModuleResult`（expand/validate 仍为匿名对象：
  响应由展开器组合派生，命名化收益低）。
- OpenAPI schema **318 → 334**；`apiContractParity` 新增 6 个 DTO 对账，其中模块参数的 `type`
  在 schema 中是宽 string、前端复用节点参数联合，新增嵌套收窄 `NormalizeWorkflowModuleVersion /
  Descriptor / ExtractWorkflowModuleResult`。`apiContract` 增加 12 个必需 DTO 与 11 条路径守卫。

验证：Api 504、前端 98 文件 / 1174 项、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零六轮：调查 / 硬件来源 / 脚本 / 配方域响应 schema（2026-11-11）

- 调查域 **8 个端点**：列表、趋势、按追溯查询、详情、从追溯立项、更新、验证证据读取/评估 →
  `InvestigationCaseSummary / CaseDetail / VerificationEvidence / TrendDashboard`。
- 硬件来源域 **3 个端点**：列表、捕获、声明写入 → `HardwareProvenanceSnapshot`。
- 脚本编辑器 **3 个端点**：validate/complete/format → `ScriptValidationResult /
  ScriptCompletionItem / ScriptFormatResult`。
- 配方域 **5 个端点**：创建、克隆、版本 diff、验证候选、验证关联 → `JobDescriptor / JobVersionDiff /
  ValidationCandidateInfo / JobVersionValidationInfo`。
- OpenAPI schema **334 → 348**；`apiContractParity` 新增 6 个 DTO 对账（硬件来源沿用已有的
  kind 收窄定义），`apiContract` 增加 10 个必需 DTO 与 13 条路径守卫。

验证：Api 504、前端 98 文件 / 1174 项、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零七轮：插件基准 / 工作进程 / 工作流存储 / 配方参数域 schema（2026-11-11）

- 插件基准域 **4 个端点**：列表、启动、详情、CI 规格 → `PluginBenchmarkRun / PluginBenchmarkCiSpec`
  （取消返回空 202）。
- 插件工作进程域 **4 个端点**：状态列表、性能列表、重启、性能重置 →
  `PluginWorkerStatus / PluginWorkerPerformanceProfile`。
- 工作流存储域 **2 个端点**：列表 → `WorkflowDocumentSummary`；读取 → `WorkflowDefinition`
  （保存/强制覆盖返回 204 与 `X-Workflow-Revision` 头）。
- 配方参数域 **3 个端点**：读取/替换 → `ParameterValueSet`；解析 → `ResolvedRecipeParameterization`
  （版本参数化快照仍为派生匿名对象）。
- OpenAPI schema **348 → 357**；`apiContractParity` 新增 5 个 DTO 对账（`ParameterValueSet.scope`
  在 schema 中是宽 string，做联合收窄），`apiContract` 增加 6 个必需 DTO 与 9 条路径守卫。

验证：Api 504、前端 98 文件 / 1174 项、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

## 第一百零八轮：调试会话 / 匹配自动调参 / 参数调优 schema（2026-11-11）

- 调试会话 **4 个 JSON 端点**：启动/继续/单节点运行 → `DebugSessionRunResponse`（此前为匿名对象，
  含 sessionId/operation 与 WorkflowRunResult 的展示字段）；会话快照 →
  `DebugSessionSnapshotResponse`（含 reportCount，不含预览 JPEG）。
- 匹配自动调参 `/api/matching/auto-tune` → `MatchingAutoTuneHttpResponse`（既有具名 DTO，此前未声明）。
- 参数调优 `/api/tuning/preview` → `ParameterTuningPreviewResult`。
- OpenAPI schema **357 → 364**；`apiContractParity` 新增 1 个 DTO 对账（sourceKind 与
  expectedDisposition 在 schema 中是宽 string，做联合收窄），`apiContract` 增加 4 个必需 DTO 与
  4 条路径守卫。
- 至此 `Endpoints/` 下每个含路由的文件都至少声明了一个 `Produces<T>()`；剩余匿名响应仅为
  多形状端点（机器人 `/target`、模块 expand/validate、相机适配器、生产待核对列表等）与
  204 空响应。

验证：Api 504、前端 98 文件 / 1174 项、typecheck/build 通过；warning baseline 22 条无新增；
static-audit exit 0、metric floor 通过。

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
