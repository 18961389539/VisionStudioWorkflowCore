# 图像预处理补全（7 个节点，225 → 232）

来源：`node-gap-analysis.md` 第九节。这一批和测量批不同：预处理域本来就有 `jlvision.imageOp`
的 107 个算子兜底，所以只补了**四个真正绕不过去**的能力 + 三个"能绕但每次都要拼 4 个节点"的便利节点。

| 节点 | 说明 | 为什么非它不可（或为什么值得合并） |
|---|---|---|
| `image.clahe` | 分块自适应直方图均衡 | 全局 `equHisto` / `contrastNormalize` 在光照梯度下只会"亮的更亮、暗的更暗" |
| `image.polarUnwrap` | 环带 → 直线条带 | 极坐标不是仿射也不是单应，`warpAffine`/`perspective` 都表达不了 |
| `image.bilateral` | 保边平滑 | `isotropicDiffusion` 语义不同，NLM 是去噪不是保边，拉丝面会糊掉划痕 |
| `image.whiteBalance` | 白平衡估计 + 施加 | 施加侧可绕，**估计侧**没有入口 |
| `image.qualityMetrics` | 亮度/对比度/清晰度/噪声/饱和占比/熵 | 自动曝光与 HDR 选档每轮都要，5 个节点拼一次太贵 |
| `image.depthScale` | 显式位深映射 | 现有转换是隐式 1/257，12 位相机要可控 |
| `image.channelMix` | 通道线性加权（含负权） | `image.grayscale` 只有三档预设；红减蓝这种色向增强没有入口 |

## 一、实现要点

**全部走 OpenCV，不走 JLVision。** CLAHE、两极坐标重映射、双边滤波在 JLVisionLib 1.0.0 里没有
对应算子；用 `jlvision.imageOp` 的算子表组合不出来，这是它们在 9.2 被列为"真缺口"的原因。

**CLAHE 的三种通道语义。** 单通道直接均衡；彩色默认在 Lab 的 L 通道上做（颜色不漂移，彩色缺陷件要这个），
`colorMode=gray` 则塌成单通道（下游阈值/卡尺多数只吃灰度）。通道数跟着 `colorMode` 走，
不为了"保持类型"偷偷补成 3 通道——那会让下游误判这是彩色图。

**极坐标展开用自建 map + `Cv2.Remap`，不用 `LinearPolar`。** 只有自己算 (θ, r)→(x, y) 才能精确控制
内/外半径、起始角、扫描方向与采样数；`LinearPolar` 把半径和角度绑死在图像尺寸上，跳出内孔就做不到。
自动采样：角向按中间半径周长推（保持近似方形像素），径向一个半径单位一个像素。

**噪声 σ 的估计口径写死在注释里。** `noiseSigma = mean|Laplacian| / ( ‖k‖₂ · sqrt(2/π) )`，
3×3 Laplacian 的 ‖k‖₂ = √20，所以分母是 3.5676。这是**同一场景跨帧可比**的相对量，不是标定过的物理单位。

**白平衡按通道分位求参考。** 白块模式用直方图求 P99 作为白参考（忽略最亮 1% 的高光），
灰度世界用通道均值。默认 `preserveBrightness=true`：把三个增益整体缩放回原亮度，
否则一次白平衡就可能把整幅图推向过曝。

## 二、踩到的坑（都和 OpenCvSharp 5 / Mat 所有权有关）

1. **`Cv2.CLAHE` 在 OpenCvSharp 5 里不存在**，要用 `CLAHE.Create(clipLimit, tileGrid)`。
2. **`Mat.Split()` 无参重载被移除**，只能用 `Cv2.Split(mat, out var channels)`。
3. **`new Mat(rows, cols, type, float[])` 是 protected**，构建 map/kernel 必须先 `new Mat(...)` 再
   `Marshal.Copy(array, 0, mat.Data, len)`。
4. **`Cv2.Mean(src, mask)` 的 mask 不能传 null**（InputArray 隐式转换会抛 `ArgumentNullException`），
   无 ROI 时必须走单参重载——封装成 `MeanOf(src, mask)`。
5. **ROI 合成的所有权约定**：`Composite(processed, source, mask)` 会**消费** processed（无掩膜时原样返回、
   有掩膜时克隆后释放入参）。第一版让调用点写 `using var enhanced = ...`，结果输出 Mat 被提前 Dispose，
   测试里表现为 `ObjectDisposedException: OpenCvSharp.Mat`。

## 三、验证

- 引擎 2031（本批 9 条：CLAHE 提升暗侧局部对比度、环带落在预期行、双边压噪但保住阶跃、白平衡通道均值拉平、位深映射精确命中、权重和为零必须报错）。
- API 445（本批 2 条：7 节点全量注册 + pipeline 资格 + 无副作用 + 输出端口齐全）。
- 前端 1131（本批 10 个枚举选项已补中文）。
- `catalog --check`：232 节点，hash `a97f69e9…`。

## 四、仍未做

- `image.denoise` 的 16 位路径：OpenCV NLM 只吃 8 位，硬做要自写 NLM 或接受量化损失，暂用 `image.depthScale` 显式降位替代。
- `image.fuse` 的 `hdr` 加权模式：给多曝光融合加"良好曝光度"权重，属于既有节点扩展而非新节点。
- `ImageList` 端口类型：`image.stack` 与多轮廓迭代同源，需要引擎侧一起改。
