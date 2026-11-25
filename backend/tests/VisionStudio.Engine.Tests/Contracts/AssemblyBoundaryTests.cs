using System.Xml.Linq;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 程序集边界护栏（2026-11-25）。
///
/// 背景：厂商相机适配器此前引用整个 <c>VisionStudio.Engine</c>（66k 行，连带 JLVisionLib /
/// WorkflowCore / Tesseract / ZXing / NModbus / S7netplus），只为拿到 12 个契约类型。
/// 契约迁到 <c>VisionStudio.Hardware.Abstractions</c> 后，适配器依赖集应只剩
/// Abstractions + OpenCvSharp + 厂商 SDK。
///
/// 这些测试读 csproj 而不是加载程序集：csproj 是依赖的唯一声明处，断言因此稳定、
/// 不依赖构建顺序，也不会被"某台机器上恰好没还原某个包"影响。
/// 一旦有人给契约程序集加回引擎引用（哪怕是通过"顺手加个辅助方法"），这里会先失败。
/// </summary>
public sealed class AssemblyBoundaryTests
{
    /// <summary>硬件契约程序集允许的项目依赖白名单——新增依赖必须显式更新这里并说明理由。</summary>
    private static readonly string[] AllowedHardwareContractProjectDependencies = ["VisionStudio.Abstractions"];

    /// <summary>
    /// 算法内核只许依赖契约层与图像层。它此前和引擎同程序集，任何"顺手引用一下运行时就绪的东西"
    /// 都不会被察觉；拆包后这条断言是第一道闸门。
    /// </summary>
    private static readonly string[] AllowedAlgorithmProjectDependencies =
        ["VisionStudio.Abstractions", "VisionStudio.Imaging"];

    /// <summary>图像基础层只依赖契约层。</summary>
    private static readonly string[] AllowedImagingProjectDependencies = ["VisionStudio.Abstractions"];

    [Theory]
    [InlineData("VisionStudio.Camera.Basler")]
    [InlineData("VisionStudio.Camera.Hikrobot")]
    public void VendorCameraAdapter_DoesNotReferenceEngine(string projectName)
    {
        var references = ReadProjectReferences(projectName);

        Assert.DoesNotContain(references, name => name.Equals("VisionStudio.Engine", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("VisionStudio.Hardware.Abstractions", references);
    }

    /// <summary>
    /// 契约程序集自己也不许回头引用引擎——否则上面的断言只是把耦合挪了一层。
    /// </summary>
    [Fact]
    public void HardwareContracts_OnlyDependOnAbstractions()
    {
        var references = ReadProjectReferences("VisionStudio.Hardware.Abstractions");

        Assert.Equal(
            AllowedHardwareContractProjectDependencies.OrderBy(x => x, StringComparer.Ordinal),
            references.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 契约程序集只允许 OpenCvSharp（VisionFrame 以 Mat 承载像素缓冲）。
    /// 出现第二个包就是要重新讨论分层，而不是让它悄悄进去。
    /// </summary>
    [Fact]
    public void HardwareContracts_OnlyReferenceOpenCvPackage()
    {
        var packages = ReadPackageReferences("VisionStudio.Hardware.Abstractions");

        Assert.Equal(["OpenCvSharp5.Windows"], packages.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 算法内核不得回头引用引擎。这条边一旦反转，整次拆分就退回原点——
    /// 而且因为两边 namespace 都叫 VisionStudio.Engine.*，代码看上去毫无异样。
    /// </summary>
    [Fact]
    public void AlgorithmCore_OnlyDependsOnContractsAndImaging()
    {
        var references = ReadProjectReferences("VisionStudio.Algorithms");

        Assert.Equal(
            AllowedAlgorithmProjectDependencies.OrderBy(x => x, StringComparer.Ordinal),
            references.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ImagingLayer_OnlyDependsOnContracts()
    {
        var references = ReadProjectReferences("VisionStudio.Imaging");

        Assert.Equal(
            AllowedImagingProjectDependencies.OrderBy(x => x, StringComparer.Ordinal),
            references.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 引擎必须引用下游三个程序集——否则说明有人把类型又搬回引擎，
    /// 或删掉了引用却留下编译告警被抑制的代码。
    /// </summary>
    [Fact]
    public void Engine_ReferencesExtractedLayers()
    {
        var references = ReadProjectReferences("VisionStudio.Engine");

        Assert.Contains("VisionStudio.Algorithms", references);
        Assert.Contains("VisionStudio.Imaging", references);
        Assert.Contains("VisionStudio.Hardware.Abstractions", references);
    }

    private static string[] ReadProjectReferences(string projectName)
        => ReadItemIncludes(projectName, "ProjectReference")
            // ProjectReference 的 Include 是相对路径，取项目名（去目录与 .csproj 后缀）。
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .ToArray();

    private static string[] ReadPackageReferences(string projectName)
        => ReadItemIncludes(projectName, "PackageReference")
            // 包名本身就是标识，不能去后缀（OpenCvSharp5.Windows 去掉 .Windows 会变成另一个包名）。
            .ToArray();

    private static string[] ReadItemIncludes(string projectName, string itemName)
    {
        var project = LoadProject(projectName);
        return project.Descendants(itemName)
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static XDocument LoadProject(string projectName)
    {
        var path = Path.Combine(RepositoryRoot(), "backend", "src", projectName, projectName + ".csproj");
        Assert.True(File.Exists(path), $"找不到项目文件：{path}");
        return XDocument.Load(path);
    }

    /// <summary>
    /// 从测试输出目录向上找到仓库根（含 backend/VisionStudio.slnx 的目录）。
    /// 测试在 bin/Debug/net10.0 下运行，层级随 TFM 变化，因此按内容而不是固定层数定位。
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "VisionStudio.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"从 '{AppContext.BaseDirectory}' 向上未找到含 backend/VisionStudio.slnx 的仓库根。");
    }
}
