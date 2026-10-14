# 区域处理节点

2026-10-15 新增六个节点，内置目录 94 → 100。算法使用 JLVision；不调用 OpenCV 区域算法。

| 节点 | 用途 |
|---|---|
| `region.fromThreshold` | 按包含端点的灰度范围提取像素区域，彩色自动转灰度 |
| `region.connectedComponents` | 对输入并集拆分连通域 |
| `region.filter` | 按面积、圆度、宽、高、矩形度、等效椭圆轴比筛选已有区域对象 |
| `region.boolean` | 对 A、B 各自并集进行并、交、A−B、对称差运算 |
| `region.fillHoles` | 填充每个对象的封闭孔洞 |
| `image.reduceDomain` | 保留图像像素并附加处理域，多次限定取交集 |

## 区域契约

`Region` 是独立的图数据类型，记录图像宽高和区域对象列表。每个对象由按行排序的像素游程组成，列范围包含两端。数据不可变，不含原生句柄，保留孔洞；支持 JSON 导出、运行报告快照和插件 Worker JSON 编解码。

一个对象可以包含多个不连通目标。阈值和布尔运算不会自动拆分；要按目标筛选，应先连接 `region.connectedComponents`。对象序号不是跨帧跟踪 ID。面积是像素计数，不是轮廓多边形面积；汇总面积对所有对象取并集，不重复计数重叠像素。筛选上下限包含端点，多条件筛选可串联节点。

五个区域节点都输出 `region`、`image`、`count`、`area`、`found`。`image` 是独立的 0／255 掩膜图，可连接既有轮廓、Blob 和保存图像节点；显示保留精确形状和孔洞。叠加矩形用于标出对象包围框，最多 200 个。空结果正常返回零面积、零对象和 `found=false`。

输入最多 10000 个对象、累计 1000000 条游程；超过预算明确报错，避免悄悄截断缺陷区域。像素游程必须在所属图像内。布尔运算要求图像尺寸一致，使用者还需确保坐标系一致；不同裁剪或变换来源的区域须先对齐。

## 限定处理域

`image.reduceDomain` 输入 `region`，以及 `image` 或 `domainImage` 二选一。输出 `domainImage` 为专用的 `DomainImage` 类型，拥有独立的原始像素与不可变区域；不会把域外像素涂黑。再次限定时与原域取交集。

`DomainImage` 可连接 `region.fromThreshold.domainImage` 和下一次 `image.reduceDomain.domainImage`。缺陷检测批次还支持 `image.dynamicThreshold`、`defect.referenceCompare`、`measure.regionIntensity` 的同名输入。阈值节点恢复原生 JLVision 处理域后执行阈值，所以检测灰度 0 时不会把域外像素当作前景。普通 `Image` 端口拒绝这个类型，避免旧节点或插件静默丢失处理域；不能把它直接连接任意旧图像节点。需要接现有图像链时，使用区域节点的普通 `image` 掩膜输出。

限定域支持结构化域快照和单节点图像预览。插件 Worker 暂不支持 `DomainImage` 传输，会明确报错；`Region` 本身支持 Worker JSON。重放仍从普通采集／源图像重新执行工作流构建处理域。

## 示例

前端“从模板新建” → “测量与标定” → **区域处理与限定域**。对应 `samples/region-processing-workflow.json`。

流程：合成图 → 阈值区域 → 连通域 → 面积筛选 → 填孔 → 限定原图处理域 → 域内阈值 → 区域交集 → 数量范围 → OK／NG。无需相机或设备，不写外部文件。

验证入口：`RegionNodeTests`、`RegionProcessingApiTests`。覆盖包含端点的阈值、孔洞、各筛选属性、集合运算、空区域、域内黑色检测、域交集、所有权、取消、JSON 编解码和完整 API 工作流。

最终验证：Release 后端非 Soak 测试 **654/654**（Engine 314、API 340），其中本批新增 22 项；前端 **39/39**，生产构建通过。100 节点目录一致性、静态审查、差异空白检查与警告基线通过，无新增编译警告。
