using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 模型层边界守卫。
    /// Chinese: 断言 ImageViewer.Models 命名空间下不出现任何 WPF UI 类型（控件、画刷、Dispatcher 等），
    /// 把"模型层不依赖 UI"从口头约定变成可执行的约束。
    /// v2026-09-30（RoiBase 一族下沉到 Core）后，Models 命名空间横跨两个程序集：
    /// ① <c>ImageViewer.Core</c> 持有框架中立的领域基础类型（RoiBase / RoiColor / PointD / MeasurementTolerance…），
    ///    它连 WPF 程序集都不引用，由 <see cref="CoreAssembly_DoesNotReferenceAnyWpfAssembly"/> 在程序集层面锁死；
    /// ② 控件程序集持有其余模型类型（ROI 子类、体数据），仍需逐个类型扫描。
    /// English: Architectural guard for the model layer. Since the ROI base types moved to Core, the namespace spans two
    /// assemblies: Core hosts the framework-neutral domain types and is forbidden from referencing WPF assemblies at all
    /// (locked down at assembly level), while the control assembly keeps the remaining model types and is scanned per type.
    /// </summary>
    public class ModelsNamespaceBoundaryTests
    {
        /// <summary>
        /// 被禁止的 WPF 类型（按完整类型名精确匹配）。
        /// Chinese: 精确匹配而非可赋值性匹配——因为 BitmapSource 派生自 ImageSource，但前者是像素数据载体、被允许。
        /// 几何值类型（Point / Vector / Rect）自 v2 起也在禁止之列：模型层统一使用 PointD / VectorD / RectD。
        /// English: Forbidden WPF types, matched by exact full name rather than assignability, because BitmapSource
        /// derives from ImageSource yet is an allowed pixel-data carrier. The geometry value types (Point / Vector /
        /// Rect) are forbidden as well since the model layer uses PointD / VectorD / RectD.
        /// </summary>
        private static readonly HashSet<string> ForbiddenWpfUiTypes = new(StringComparer.Ordinal)
        {
            "System.Windows.DependencyObject",
            "System.Windows.FrameworkElement",
            "System.Windows.FrameworkContentElement",
            "System.Windows.Window",
            "System.Windows.Thickness",
            "System.Windows.Visibility",
            "System.Windows.Point",
            "System.Windows.Vector",
            "System.Windows.Rect",
            "System.Windows.Controls.Control",
            "System.Windows.Controls.Panel",
            "System.Windows.Controls.Canvas",
            "System.Windows.Controls.TextBlock",
            "System.Windows.Controls.MenuItem",
            "System.Windows.Shapes.Shape",
            "System.Windows.Media.Brush",
            "System.Windows.Media.SolidColorBrush",
            "System.Windows.Media.ImageSource",
            "System.Windows.Media.DoubleCollection",
            "System.Windows.Threading.Dispatcher",
            "System.Windows.Threading.DispatcherObject"
        };

        private const string ModelsNamespace = "ImageViewer.Models";

        private static Type[] ControlModelTypes { get; } = GetModelTypes(typeof(ImageViewer.Controls.ImageViewer).Assembly);

        private static Type[] CoreModelTypes { get; } = GetModelTypes(typeof(RoiBase).Assembly);

        [Fact]
        public void ModelsNamespace_DoesNotReferenceWpfUiTypes()
        {
            Type[] modelTypes = [.. CoreModelTypes, .. ControlModelTypes];

            var violations = new List<string>();

            foreach (Type type in modelTypes)
            {
                foreach (Type referenced in EnumerateReferencedTypes(type))
                {
                    if (referenced.FullName is string fullName && ForbiddenWpfUiTypes.Contains(fullName))
                    {
                        violations.Add($"{type.FullName} 引用了 {fullName}");
                    }
                }
            }

            Assert.Empty(violations);
        }

        /// <summary>
        /// 守卫自身的自检。
        /// Chinese: 确认扫描确实覆盖了控件侧模型层，自有几何类型（PointD）确实被引用、WPF 几何类型（Point）确实不再出现——
        /// 否则守卫可能因为扫描失效而"永远通过"；同时确认 RoiBase 一族确实已经下沉到 Core。
        /// （Core 不引用 WPF 由 ImageViewer.Core.Tests 的 CoreArchitectureBoundaryTests 在程序集层面锁死，这里不重复。）
        /// English: Self-check for the guard: confirms the scan covers the control-side model layer and that the
        /// moved domain base types now live in Core instead of the control assembly.
        /// </summary>
        [Fact]
        public void Guard_ActuallyScansModelTypesAndSeesModelOwnedGeometryTypes()
        {
            HashSet<string> referencedNames = ControlModelTypes
                .SelectMany(EnumerateReferencedTypes)
                .Select(type => type.FullName ?? type.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.NotEmpty(ControlModelTypes);
            Assert.Contains("ImageViewer.Models.PointD", referencedNames);
            Assert.DoesNotContain("System.Windows.Point", referencedNames);
            Assert.Contains("System.Windows.Media.Imaging.BitmapSource", referencedNames);
            Assert.DoesNotContain("System.Windows.Media.Color", referencedNames);

            // RoiBase 一族已下沉：控件侧不再定义，Core 侧必须定义，且四者同属一个程序集。
            string[] controlModelTypeNames = ControlModelTypes.Select(type => type.FullName ?? type.Name).ToArray();
            string[] coreModelTypeNames = CoreModelTypes.Select(type => type.FullName ?? type.Name).ToArray();
            Assert.DoesNotContain(typeof(RoiBase).FullName, controlModelTypeNames);
            Assert.Contains(typeof(RoiBase).FullName, coreModelTypeNames);
            Assert.Contains(typeof(RoiColor).FullName, coreModelTypeNames);
            Assert.Contains(typeof(RoiColors).FullName, coreModelTypeNames);

            // BaseViewModel 在 ImageViewer.Common 命名空间（不属于 Models），只断言它与 ROI 基础类型同属一个程序集。
            Assert.Same(typeof(RoiBase).Assembly, typeof(RoiColor).Assembly);
            Assert.Same(typeof(RoiBase).Assembly, typeof(RoiColors).Assembly);
            Assert.Same(typeof(RoiBase).Assembly, typeof(ImageViewer.Common.BaseViewModel).Assembly);
            Assert.Contains(
                typeof(ImageViewer.Common.BaseViewModel).FullName,
                typeof(RoiBase).Assembly.GetTypes().Select(type => type.FullName).ToArray());
        }

        private static Type[] GetModelTypes(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            return assembly
                .GetTypes()
                .Where(type => string.Equals(type.Namespace, ModelsNamespace, StringComparison.Ordinal))
                .ToArray();
        }

        private static IEnumerable<Type> EnumerateReferencedTypes(Type type)
        {
            const BindingFlags DeclaredMembers =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (FieldInfo field in type.GetFields(DeclaredMembers))
            {
                foreach (Type referenced in Unwrap(field.FieldType))
                {
                    yield return referenced;
                }
            }

            foreach (PropertyInfo property in type.GetProperties(DeclaredMembers))
            {
                foreach (Type referenced in Unwrap(property.PropertyType))
                {
                    yield return referenced;
                }

                foreach (ParameterInfo indexParameter in property.GetIndexParameters())
                {
                    foreach (Type referenced in Unwrap(indexParameter.ParameterType))
                    {
                        yield return referenced;
                    }
                }
            }

            foreach (MethodInfo method in type.GetMethods(DeclaredMembers))
            {
                foreach (Type referenced in Unwrap(method.ReturnType))
                {
                    yield return referenced;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    foreach (Type referenced in Unwrap(parameter.ParameterType))
                    {
                        yield return referenced;
                    }
                }
            }
        }

        private static IEnumerable<Type> Unwrap(Type type)
        {
            yield return type;

            if (type.IsArray && type.GetElementType() is Type elementType)
            {
                foreach (Type referenced in Unwrap(elementType))
                {
                    yield return referenced;
                }
            }

            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    foreach (Type referenced in Unwrap(argument))
                    {
                        yield return referenced;
                    }
                }
            }
        }
    }
}
