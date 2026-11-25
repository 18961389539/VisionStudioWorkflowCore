// 算法层与引擎共用契约类型（VisionValue / VisionRoi / NodeExecutionContext / VisionImage ...）。
// 这些类型分布在 VisionStudio.Abstractions 与 VisionStudio.Imaging 两个程序集里，
// 统一在这里引入，避免 51 个算法文件各写一遍。
global using VisionStudio.Abstractions;
global using VisionStudio.Engine.Camera;
