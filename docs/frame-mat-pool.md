# 相机帧 Mat 复用池

采集路径此前每帧都 `new Mat(width, height, CV_8UC3)`，再从 SDK 缓冲 `MemoryCopy` 进去。
5 MP × 3 B ≈ 15 MB/帧，10 fps 就是 150 MB/s 的托管大对象分配——这些 `Mat` 全部落在 LOH。
gen2 回收跟不上时，现象是"每隔几秒卡一下"，而 l3 长稳只看工作集趋势，抓不到分配速率，
所以这个问题在压测里长期不可见，只在现场被描述成"软件有时候会顿"。

- 池：`backend/src/VisionStudio.Engine/Camera/FrameMatPool.cs`
- 归还点：`CameraFrameHub.cs` 的 `SharedCameraFrame.Release()`
- 租用点：`VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs`、`VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs`
- 观测：`/api/health` 的 `framePool` 字段（认证后读取；匿名探针 `/healthz` 只返回 status/ready/productionReady）、`FrameMatPoolTests`

## 为什么复用点在引用计数归零处

帧在 `CameraFrameHub` 里按引用计数分发：预览、推理、留证可能同时持有同一帧。只要还有人持有，
这块 `Mat` 就还在被读——此时回收复用会让某个消费者看到"图被下一帧覆盖了"，是最难查的一类 bug。

所以 `Return` 只在 `Release()` 里引用计数归零时调用，归还的必然是"已无租约"的缓冲。

## 分桶与上限

| 参数 | 值 | 理由 |
|---|---|---|
| 分桶键 | `(Width, Height, MatType)` | 不同相机/不同像素格式的缓冲不能互换，`Mat` 的步长与通道数都绑在尺寸上 |
| `MaxPerShape` | 4 | 与 `CameraFrameHub` 的环容量对齐；再多就是替 GC 囤内存 |
| `MaxPooledBytes` | 192 MB | 8 台 5 MP 相机同时跑时不会把整机内存吃光 |

超过单桶容量或总字节上限时直接 `Dispose` 并计入 `dropped`——**宁可多分配一次，也不让池无限长**。
池的第一优先级是削掉稳态分配，不是"一帧都不许 new"。

## 安全前提：不清零

复用的 `Mat` 不清零，因为相机驱动每一次都会写满整幅图（`MemoryCopy` / `PixelDataConverter.Convert`
写的是完整帧）。如果将来出现"只写 ROI"或"部分行有效"的驱动路径，这个前提就破了，必须改成
`Rent` 后显式清零，否则会读到上一帧的残影。

同理：**非相机路径（几何运算的中间图）不要走这个池**。那里的 `Mat` 可能只被部分写入，
复用会带出脏数据。池的语义是"相机帧缓冲"，不是通用分配器。

## 观测

```text
GET /api/health   （需要登录；匿名探针用 /healthz）
  framePool: "rented=120 reused=3840 returned=3840 dropped=12 pooled=4 bytes=62914560"
```

| 计数 | 含义 | 该看什么 |
|---|---|---|
| `rented` | 真正 `new` 出来的次数 | 稳态下应该停在很小的数；持续增长说明桶容量不够或尺寸在变 |
| `reused` | 从池里取到的次数 | 与 `rented` 的比例就是命中率 |
| `returned` | 归还进池的次数 | 长期明显小于 `reused + rented` 说明有帧没被 `Release`（引用计数泄漏） |
| `dropped` | 因超限被销毁的次数 | 持续增长说明 `MaxPooledBytes` 或 `MaxPerShape` 偏小，或者尺寸种类太多 |
| `pooled` / `bytes` | 池内当前占用 | 就是这部分的常驻内存，应当有上界 |

`returned` 与 `rented + reused` 的缺口是最有价值的一列：它把"某条路径拿了帧没还"从
"内存慢慢涨"变成了一个可以直接读的数。

## 测试

`FrameMatPoolTests` 只锁三件事：同尺寸第二次 `Rent` 返回同一块内存（`Data` 指针相等）、
不同尺寸不复用、`Return(null)` 无副作用。不断言池的容量行为——那属于实现细节，
容量参数调整不应该让测试变红。
