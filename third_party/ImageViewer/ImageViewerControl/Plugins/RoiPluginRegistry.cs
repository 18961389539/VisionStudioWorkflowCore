using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Plugins
{
    public sealed partial class RoiPluginRegistry
    {
        private readonly object _gate = new();
        private readonly List<IRoiPlugin> _plugins = new();
        private readonly Dictionary<Type, IRoiPlugin> _pluginsByType = new();
        private readonly Dictionary<string, IRoiPlugin> _pluginsByTypeKey = new(StringComparer.OrdinalIgnoreCase);
        private int _updateDepth;
        private bool _hasPendingChange;

        [Obsolete("Prefer passing an explicit registry instance.")]
        public static RoiPluginRegistry Default { get; } = CreateBuiltIn();

        public IReadOnlyList<IRoiPlugin> Plugins
        {
            get
            {
                lock (_gate)
                {
                    return [.. _plugins];
                }
            }
        }

        /// <summary>
        /// 可用类型集合发生变化时触发。
        /// Chinese: Register / Unregister 都是**就地**修改同一个实例，调用方靠引用比较发现不了变化；
        /// 宿主也需要据此刷新绘制菜单、并把新注册表不再认识的 ROI 从视图状态里清掉。
        /// 事件在锁外、于**调用方线程**触发：处理器可以安全地回调注册表，但**必须自行调度到 UI 线程**
        /// （事件链上的迁移会修改 ObservableCollection 并刷新 WPF 视觉）。控件侧通过注入的调度器完成这一步。
        /// English: Raised outside the lock on the caller's thread. Handlers may re-enter the registry but must marshal
        /// to the UI thread themselves; the control injects a scheduler for that.
        /// </summary>
        public event EventHandler? Changed;

        /// <summary>
        /// 把多次 Register / Unregister 合并成一次 <see cref="Changed"/>。
        /// Chinese: 模块通常一次注册多个插件；不合并的话每次注册都会触发一次完整的状态迁移
        /// （N 个插件 = N 次迁移与 N 次菜单刷新），而且中间态是"部分类型已注册"，容易误判可迁移性。
        /// English: Coalesces several Register/Unregister calls into a single <see cref="Changed"/>. A module typically
        /// registers several plugins at once, and per-call notifications would migrate state N times with a partial
        /// "some types registered" intermediate state.
        /// </summary>
        public IDisposable BeginUpdate()
        {
            lock (_gate)
            {
                _updateDepth++;
            }

            return new UpdateScope(this);
        }

        public IReadOnlyCollection<string> RegisteredTypeKeys
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pluginsByTypeKey.Keys];
                }
            }
        }

        public void Register(IRoiPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);

            plugin = FilterDrawingTools(plugin);

            lock (_gate)
            {
                if (_pluginsByType.ContainsKey(plugin.RoiType))
                {
                    throw new InvalidOperationException($"ROI plugin for type '{plugin.RoiType.FullName}' is already registered.");
                }

                if (_pluginsByTypeKey.ContainsKey(plugin.TypeKey))
                {
                    throw new InvalidOperationException($"ROI plugin with key '{plugin.TypeKey}' is already registered.");
                }

                _plugins.Add(plugin);
                _pluginsByType.Add(plugin.RoiType, plugin);
                _pluginsByTypeKey.Add(plugin.TypeKey, plugin);
            }

            NotifyChanged();
        }

        public IRoiPlugin? FindByRoi(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return FindByType(roi.GetType());
        }

        public IRoiPlugin? FindByType(Type roiType)
        {
            ArgumentNullException.ThrowIfNull(roiType);

            lock (_gate)
            {
                return _pluginsByType.TryGetValue(roiType, out var plugin) ? plugin : null;
            }
        }

        public IRoiPlugin? FindByTypeKey(string typeKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);

            lock (_gate)
            {
                return _pluginsByTypeKey.TryGetValue(typeKey, out var plugin) ? plugin : null;
            }
        }

        public bool Unregister(string typeKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);

            bool removed;
            lock (_gate)
            {
                if (!_pluginsByTypeKey.TryGetValue(typeKey, out var plugin))
                {
                    return false;
                }

                _plugins.Remove(plugin);
                _pluginsByType.Remove(plugin.RoiType);
                _pluginsByTypeKey.Remove(typeKey);
                removed = true;
            }

            if (removed)
            {
                NotifyChanged();
            }

            return removed;
        }

        /// <summary>
        /// 触发一次变更通知；批量更新期间只记标记，由最外层 <see cref="BeginUpdate"/> 结束时统一触发。
        /// </summary>
        private void NotifyChanged()
        {
            bool shouldRaise;
            lock (_gate)
            {
                _hasPendingChange = true;
                shouldRaise = _updateDepth == 0;
                if (shouldRaise)
                {
                    _hasPendingChange = false;
                }
            }

            if (shouldRaise)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void EndUpdate()
        {
            bool shouldRaise;
            lock (_gate)
            {
                if (_updateDepth > 0)
                {
                    _updateDepth--;
                }

                shouldRaise = _updateDepth == 0 && _hasPendingChange;
                if (shouldRaise)
                {
                    _hasPendingChange = false;
                }
            }

            if (shouldRaise)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private sealed class UpdateScope : IDisposable
        {
            private RoiPluginRegistry? _registry;

            public UpdateScope(RoiPluginRegistry registry)
            {
                _registry = registry;
            }

            public void Dispose()
            {
                RoiPluginRegistry? registry = _registry;
                _registry = null;
                registry?.EndUpdate();
            }
        }

        public IEnumerable<IRoiPlugin> GetPluginsInHitTestOrder()
        {
            IRoiPlugin[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _plugins];
            }

            return snapshot.OrderByDescending(plugin => plugin.HitTestOrder);
        }

        public IEnumerable<RoiToolDescriptor> GetDrawingTools()
        {
            IRoiPlugin[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _plugins];
            }

            return RoiToolCatalog.OrderVisibleTools(snapshot);
        }

        private static IRoiPlugin FilterDrawingTools(IRoiPlugin plugin)
        {
            if (!RoiToolCatalog.ContainsFilteredTools(plugin))
            {
                return plugin;
            }

            IReadOnlyList<RoiToolDescriptor> filteredTools = RoiToolCatalog.GetVisibleTools(plugin);

            return filteredTools.Count == 0
                ? new FilteredRoiPlugin(plugin, [])
                : new FilteredRoiPlugin(plugin, filteredTools);
        }

        private sealed class FilteredRoiPlugin : IRoiPlugin
        {
            private readonly IRoiPlugin _inner;

            public FilteredRoiPlugin(IRoiPlugin inner, IReadOnlyList<RoiToolDescriptor> drawingTools)
            {
                _inner = inner;
                DrawingTools = drawingTools;
            }

            public string TypeKey => _inner.TypeKey;

            public Type RoiType => _inner.RoiType;

            public int HitTestOrder => _inner.HitTestOrder;

            public IReadOnlyList<RoiToolDescriptor> DrawingTools { get; }

            public IRoiBehavior Behavior => _inner.Behavior;

            public IRoiRenderer Renderer => _inner.Renderer;

            public IEnumerable<RoiBase> GetRois(ImageViewerViewModel viewModel) => _inner.GetRois(viewModel);

            public void ClearCollection(ImageViewerViewModel viewModel) => _inner.ClearCollection(viewModel);

            public bool AddToCollection(ImageViewerViewModel viewModel, RoiBase roi) => _inner.AddToCollection(viewModel, roi);

            public bool RemoveFromCollection(ImageViewerViewModel viewModel, RoiBase roi) => _inner.RemoveFromCollection(viewModel, roi);

            public RoiBase CreateRoi(RoiPersistenceData data) => _inner.CreateRoi(data);

            public void PopulatePersistenceData(RoiBase roi, RoiPersistenceData data) => _inner.PopulatePersistenceData(roi, data);

            public IReadOnlyList<string> BuildInfoLines(RoiBase roi, BitmapSource? bitmap, double pixelSize, string? physicalUnit)
                => _inner.BuildInfoLines(roi, bitmap, pixelSize, physicalUnit);

            public FrameworkElement? CreatePropertyEditor(RoiBase roi) => _inner.CreatePropertyEditor(roi);
        }

        private readonly struct BuiltInPluginFactory
        {
            private readonly IReadOnlyDictionary<Type, IRoiBehavior> _behaviors;
            private readonly IReadOnlyDictionary<Type, IRoiRenderer> _renderers;

            public BuiltInPluginFactory(IReadOnlyDictionary<Type, IRoiBehavior> behaviors, IReadOnlyDictionary<Type, IRoiRenderer> renderers)
            {
                _behaviors = behaviors ?? throw new ArgumentNullException(nameof(behaviors));
                _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
            }

            public RoiPlugin<T> Create<T>(
                string typeKey,
                int hitTestOrder,
                IEnumerable<RoiToolDescriptor>? drawingTools,
                Func<RoiPersistenceData, T> createRoi,
                Action<T, RoiPersistenceData> populatePersistenceData,
                Func<T, BitmapSource?, double, string?, IEnumerable<string>>? buildInfoLines = null,
                Func<T, FrameworkElement?>? createPropertyEditor = null)
                where T : RoiBase
            {
                FrameworkElement? DefaultCreatePropertyEditor(T roi) => BuiltInRoiPropertyEditorFactory.CreateEditor(roi);

                Func<T, FrameworkElement?> resolvedPropertyEditor = createPropertyEditor ?? DefaultCreatePropertyEditor;

                return new RoiPlugin<T>(
                    typeKey,
                    hitTestOrder,
                    static vm => vm.GetRoiCollection<T>(),
                    drawingTools,
                    _behaviors[typeof(T)],
                    _renderers[typeof(T)],
                    createRoi,
                    populatePersistenceData,
                    buildInfoLines,
                    resolvedPropertyEditor);
            }
        }

        private interface IBuiltInPluginRegistration
        {
            void Register(RoiPluginRegistry registry, BuiltInPluginFactory pluginFactory);
        }

        private readonly struct RoiPersistenceDescriptor<T>
            where T : RoiBase
        {
            private readonly Func<RoiPersistenceData, T> _createRoi;
            private readonly Action<T, RoiPersistenceData> _populatePersistenceData;

            public RoiPersistenceDescriptor(Func<RoiPersistenceData, T> createRoi, Action<T, RoiPersistenceData> populatePersistenceData)
            {
                _createRoi = createRoi ?? throw new ArgumentNullException(nameof(createRoi));
                _populatePersistenceData = populatePersistenceData ?? throw new ArgumentNullException(nameof(populatePersistenceData));
            }

            public T CreateRoi(RoiPersistenceData data) => _createRoi(data);

            public void PopulatePersistenceData(T roi, RoiPersistenceData data) => _populatePersistenceData(roi, data);
        }

        private sealed class BuiltInPluginRegistration<T> : IBuiltInPluginRegistration
            where T : RoiBase
        {
            private readonly string _typeKey;
            private readonly int _hitTestOrder;
            private readonly IReadOnlyList<RoiToolDescriptor>? _drawingTools;
            private readonly RoiPersistenceDescriptor<T> _persistence;
            private readonly Func<T, BitmapSource?, double, string?, IEnumerable<string>>? _buildInfoLines;
            private readonly Func<T, FrameworkElement?>? _createPropertyEditor;

            public BuiltInPluginRegistration(
                string typeKey,
                int hitTestOrder,
                IReadOnlyList<RoiToolDescriptor>? drawingTools,
                RoiPersistenceDescriptor<T> persistence,
                Func<T, BitmapSource?, double, string?, IEnumerable<string>>? buildInfoLines = null,
                Func<T, FrameworkElement?>? createPropertyEditor = null)
            {
                _typeKey = typeKey;
                _hitTestOrder = hitTestOrder;
                _drawingTools = drawingTools;
                _persistence = persistence;
                _buildInfoLines = buildInfoLines;
                _createPropertyEditor = createPropertyEditor;
            }

            public void Register(RoiPluginRegistry registry, BuiltInPluginFactory pluginFactory)
            {
                var persistence = _persistence;

                registry.Register(pluginFactory.Create(
                    _typeKey,
                    _hitTestOrder,
                    _drawingTools,
                    persistence.CreateRoi,
                    persistence.PopulatePersistenceData,
                    _buildInfoLines,
                    _createPropertyEditor));
            }
        }

        private static RoiPersistenceDescriptor<T> CreatePersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Action<T, RoiPersistenceData> populatePersistenceData)
            where T : RoiBase
        {
            return new RoiPersistenceDescriptor<T>(createRoi, populatePersistenceData);
        }

        private static RoiPersistenceDescriptor<T> CreateCenterWidthHeightAnglePersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getCenter,
            Func<T, double> getWidth,
            Func<T, double> getHeight,
            Func<T, double> getAngle,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Center = RoiPersistencePointExtensions.FromPoint(getCenter(roi));
                data.Geometry.Width = getWidth(roi);
                data.Geometry.Height = getHeight(roi);
                data.Geometry.Angle = getAngle(roi);
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreateCenterEllipsePersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getCenter,
            Func<T, double> getRadiusX,
            Func<T, double> getRadiusY,
            Func<T, double> getAngle,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Center = RoiPersistencePointExtensions.FromPoint(getCenter(roi));
                data.Geometry.RadiusX = getRadiusX(roi);
                data.Geometry.RadiusY = getRadiusY(roi);
                data.Geometry.Angle = getAngle(roi);
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreateCenterRadiusPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getCenter,
            Func<T, double> getRadius,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Center = RoiPersistencePointExtensions.FromPoint(getCenter(roi));
                data.Geometry.Radius = getRadius(roi);
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreateCenterRadiusPairPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getCenter,
            Func<T, double> getRadius,
            Func<T, double> getRadius2,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Center = RoiPersistencePointExtensions.FromPoint(getCenter(roi));
                data.Geometry.Radius = getRadius(roi);
                data.Geometry.Radius2 = getRadius2(roi);
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreatePositionPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getPosition,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Position = RoiPersistencePointExtensions.FromPoint(getPosition(roi));
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreatePointPairPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getP1,
            Func<T, PointD> getP2,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.P1 = RoiPersistencePointExtensions.FromPoint(getP1(roi));
                data.Geometry.P2 = RoiPersistencePointExtensions.FromPoint(getP2(roi));
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreatePointTriplePersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getP1,
            Func<T, PointD> getP2,
            Func<T, PointD> getVertex,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.P1 = RoiPersistencePointExtensions.FromPoint(getP1(roi));
                data.Geometry.P2 = RoiPersistencePointExtensions.FromPoint(getP2(roi));
                data.Geometry.Vertex = RoiPersistencePointExtensions.FromPoint(getVertex(roi));
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreatePointQuadPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getP1,
            Func<T, PointD> getP2,
            Func<T, PointD> getVertex,
            Func<T, PointD> getP3,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.P1 = RoiPersistencePointExtensions.FromPoint(getP1(roi));
                data.Geometry.P2 = RoiPersistencePointExtensions.FromPoint(getP2(roi));
                data.Geometry.Vertex = RoiPersistencePointExtensions.FromPoint(getVertex(roi));
                data.Geometry.P3 = RoiPersistencePointExtensions.FromPoint(getP3(roi));
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreateLinePairPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getLine1P1,
            Func<T, PointD> getLine1P2,
            Func<T, PointD> getLine2P1,
            Func<T, PointD> getLine2P2,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePointQuadPersistence(
                createRoi,
                getLine1P1,
                getLine1P2,
                getLine2P1,
                getLine2P2,
                populateExtra);
        }

        private static RoiPersistenceDescriptor<T> CreatePointPairRadiusPairPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, PointD> getP1,
            Func<T, PointD> getP2,
            Func<T, double> getRadius,
            Func<T, double> getRadius2,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePointPairPersistence(createRoi, getP1, getP2, (roi, data) =>
            {
                data.Radius = getRadius(roi);
                data.Radius2 = getRadius2(roi);
                populateExtra?.Invoke(roi, data);
            });
        }

        private static RoiPersistenceDescriptor<T> CreatePointsPersistence<T>(
            Func<RoiPersistenceData, T> createRoi,
            Func<T, IEnumerable<PointD>> getPoints,
            Action<T, RoiPersistenceData>? populateExtra = null)
            where T : RoiBase
        {
            return CreatePersistence(createRoi, (roi, data) =>
            {
                data.Geometry.Points = getPoints(roi)
                    .Select(RoiPersistencePointExtensions.FromPoint)
                    .ToList();
                populateExtra?.Invoke(roi, data);
            });
        }

        private static void PopulateCaliperPersistenceData(
            RoiPersistenceData data,
            int caliperCount,
            int caliperSearchRange,
            int caliperSamplingHalfWidth,
            double caliperEdgeSigma,
            int minimumValidCalipers,
            double caliperMinimumGradient,
            double caliperOutlierThreshold,
            CaliperEdgePolarity caliperEdgePolarity,
            LineFitMode caliperLineFitMode,
            int caliperFitClippingEndPoints)
        {
            data.Measurement.CaliperCount = caliperCount;
            data.Measurement.CaliperSearchRange = caliperSearchRange;
            data.Measurement.CaliperSamplingHalfWidth = caliperSamplingHalfWidth;
            data.Measurement.CaliperEdgeSigma = caliperEdgeSigma;
            data.Measurement.MinimumValidCalipers = minimumValidCalipers;
            data.Measurement.CaliperMinimumGradient = caliperMinimumGradient;
            data.Measurement.CaliperOutlierThreshold = caliperOutlierThreshold;
            data.Measurement.CaliperEdgePolarity = caliperEdgePolarity.ToString();
            data.Measurement.CaliperLineFitMode = caliperLineFitMode.ToString();
            data.Measurement.CaliperFitClippingEndPoints = Math.Max(0, caliperFitClippingEndPoints);
        }

        private static CaliperEdgePolarity ParseCaliperEdgePolarity(string? value)
        {
            return Enum.TryParse<CaliperEdgePolarity>(value, true, out var polarity)
                ? polarity
                : CaliperEdgePolarity.Any;
        }

        private static LineFitMode ParseCaliperLineFitMode(string? value)
        {
            return Enum.TryParse<LineFitMode>(value, true, out var mode)
                ? mode
                : LineFitMode.Tukey;
        }

        private static List<IBuiltInPluginRegistration> GetBuiltInRegistrations()
        {
            var registrations = new List<IBuiltInPluginRegistration>();

            registrations.AddRange(RegionCatalog.GetRegistrations());
            registrations.AddRange(AnnotationCatalog.GetRegistrations());
            registrations.AddRange(MeasurementCatalog.GetRegistrations());

            return registrations;
        }

        private static BuiltInPluginRegistration<T> CreateRegistration<T>(
            string typeKey,
            int hitTestOrder,
            IReadOnlyList<RoiToolDescriptor>? drawingTools,
            RoiPersistenceDescriptor<T> persistence,
            Func<T, BitmapSource?, double, string?, IEnumerable<string>>? buildInfoLines = null,
            Func<T, FrameworkElement?>? createPropertyEditor = null)
            where T : RoiBase
        {
            return new BuiltInPluginRegistration<T>(
                typeKey,
                hitTestOrder,
                drawingTools,
                persistence,
                buildInfoLines,
                createPropertyEditor);
        }

        public static RoiPluginRegistry CreateBuiltIn()
        {
            var registry = new RoiPluginRegistry();
            var behaviors = RoiInteractionService.CreateBuiltInBehaviorMap();
            var renderers = RoiRenderService.CreateBuiltInRendererMap();
            var pluginFactory = new BuiltInPluginFactory(behaviors, renderers);

            foreach (var registration in GetBuiltInRegistrations())
            {
                registration.Register(registry, pluginFactory);
            }

            return registry;
        }

        private static Rectangle CreateRectangleIcon() => new() { Width = 12, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Canvas CreateBlobIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Rectangle { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(1) },
                new Ellipse { Width = 3, Height = 3, Fill = Brushes.Black, Margin = new Thickness(3) },
                new Ellipse { Width = 2, Height = 2, Fill = Brushes.Black, Margin = new Thickness(7, 6, 0, 0) }
            }
        };

        private static Ellipse CreateEllipseIcon() => new() { Width = 12, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Canvas CreateFittedEllipseIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 10, Height = 8, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(1, 2, 0, 0) },
                new Polyline { Points = new PointCollection { new(2, 9), new(4, 3), new(8, 2), new(10, 8) }, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Polyline CreatePolygonIcon() => new() { Points = new PointCollection { new(0, 10), new(5, 0), new(10, 10), new(0, 10) }, Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Ellipse CreateCircleIcon() => new() { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Canvas CreateRingIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(1) },
                new Ellipse { Width = 4, Height = 4, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(4) }
            }
        };

        private static Polyline CreatePolylineIcon() => new() { Points = new PointCollection { new(0, 10), new(4, 3), new(8, 8), new(12, 0) }, Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Path CreateFreehandIcon() => new() { Data = Geometry.Parse("M 0,8 C 4,0 8,12 12,4"), Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Ellipse CreatePointAnnotationIcon() => new() { Width = 4, Height = 4, Fill = Brushes.Black };

        private static Canvas CreatePointCoordinateIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black, Margin = new Thickness(4) },
                new Line { X1 = 0, Y1 = 6, X2 = 12, Y2 = 6, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 6, Y1 = 0, X2 = 6, Y2 = 12, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateAutomaticEdgePointIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 1, Y1 = 10, X2 = 11, Y2 = 2, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black, Margin = new Thickness(4) }
            }
        };

        private static TextBlock CreateTextIcon() => new() { Text = "T", FontWeight = FontWeights.Bold };

        private static Canvas CreateArrowAnnotationIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 1, Y1 = 10, X2 = 10, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 10, Y1 = 3, X2 = 7, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 10, Y1 = 3, X2 = 9, Y2 = 6, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateLineMeasureIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 1, Y1 = 10, X2 = 11, Y2 = 2, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 1, Y1 = 8, X2 = 1, Y2 = 11, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 11, Y1 = 1, X2 = 11, Y2 = 4, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateArcCaliperMeasureIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Path { Data = Geometry.Parse("M 1,9 A 5,5 0 0 1 11,9"), Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 3, Y1 = 8, X2 = 3, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 6, Y1 = 7, X2 = 6, Y2 = 2, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 9, Y1 = 8, X2 = 9, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateCircularCaliperMeasureIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 6, Y1 = 0, X2 = 6, Y2 = 12, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 0, Y1 = 6, X2 = 12, Y2 = 6, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateCaliperMeasureIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 2, Y1 = 10, X2 = 10, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 3, Y1 = 2, X2 = 3, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 6, Y1 = 4, X2 = 6, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 9, Y1 = 1, X2 = 9, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreateLineCaliperMeasureIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 1, Y1 = 9, X2 = 11, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 3, Y1 = 10, X2 = 1.5, Y2 = 7.5, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 4.5, Y1 = 9, X2 = 3, Y2 = 6.5, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 7.5, Y1 = 7, X2 = 6, Y2 = 4.5, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 10.5, Y1 = 5, X2 = 9, Y2 = 2.5, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Path CreateAngleMeasureIcon() => new() { Data = Geometry.Parse("M 0,10 L 10,10 L 10,0"), Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Path CreateArcMeasureIcon() => new() { Data = Geometry.Parse("M 0,8 A 8,8 0 0 1 12,4"), Stroke = Brushes.Black, StrokeThickness = 1 };

        private static Canvas CreateThreePointCircleIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 9, Height = 9, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(1.5) },
                new Ellipse { Width = 2, Height = 2, Fill = Brushes.Black, Margin = new Thickness(1, 1, 0, 0) },
                new Ellipse { Width = 2, Height = 2, Fill = Brushes.Black, Margin = new Thickness(9, 2, 0, 0) },
                new Ellipse { Width = 2, Height = 2, Fill = Brushes.Black, Margin = new Thickness(4, 9, 0, 0) }
            }
        };

        private static Canvas CreateCenterDistanceIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 2, Y1 = 6, X2 = 10, Y2 = 6, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Ellipse { Width = 3, Height = 3, Fill = Brushes.Black, Margin = new Thickness(0.5, 4.5, 0, 0) },
                new Ellipse { Width = 3, Height = 3, Fill = Brushes.Black, Margin = new Thickness(8.5, 4.5, 0, 0) }
            }
        };

        private static Canvas CreatePointToLineIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 0, Y1 = 10, X2 = 12, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black, Margin = new Thickness(4, 2, 0, 0) },
                new Line { X1 = 6, Y1 = 4, X2 = 6, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 1 } }
            }
        };

        private static Canvas CreatePointToCircleIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(2) },
                new Ellipse { Width = 3, Height = 3, Fill = Brushes.Black, Margin = new Thickness(1) },
                new Line { X1 = 2.5, Y1 = 2.5, X2 = 6, Y2 = 6, Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 1 } }
            }
        };

        private static Canvas CreateParallelismIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 0, Y1 = 3, X2 = 12, Y2 = 3, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 0, Y1 = 9, X2 = 12, Y2 = 9, Stroke = Brushes.Black, StrokeThickness = 1 }
            }
        };

        private static Canvas CreatePerpendicularityIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Line { X1 = 0, Y1 = 10, X2 = 12, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Line { X1 = 6, Y1 = 0, X2 = 6, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1 },
                new Rectangle { Width = 4, Height = 4, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(6, 6, 0, 0) }
            }
        };

        private static Canvas CreateConcentricityIcon() => new()
        {
            Width = 12,
            Height = 12,
            Children =
            {
                new Ellipse { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(1) },
                new Ellipse { Width = 6, Height = 6, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(3) },
                new Ellipse { Width = 2, Height = 2, Fill = Brushes.Black, Margin = new Thickness(5) }
            }
        };
    }

    internal static class RoiPersistencePointExtensions
    {
        public static RoiPersistencePoint FromPoint(PointD point) => new() { X = point.X, Y = point.Y };

        public static PointD ToPoint(this RoiPersistencePoint? point)
        {
            return point == null ? default : new PointD(point.X, point.Y);
        }

        public static void ApplyCommonState(this RoiBase roi, RoiPersistenceData data)
        {
            roi.MeasurementId = data.MeasurementId;
            roi.AdvancedMeasurements = data.AdvancedMeasurements;
            roi.Label = data.Common.Label ?? string.Empty;
            roi.StrokeThickness = data.Common.StrokeThickness;
            roi.IsVisible = data.Common.IsVisible;
            roi.IsLocked = data.Common.IsLocked;
            roi.Tolerance = data.Common.Tolerance is { } tolerance
                ? new MeasurementTolerance
                {
                    Nominal = tolerance.Nominal,
                    TolerancePlus = tolerance.TolerancePlus,
                    ToleranceMinus = tolerance.ToleranceMinus
                }
                : null;

            if (!string.IsNullOrWhiteSpace(data.Common.StrokeColor))
            {
                if (RoiColor.Parse(data.Common.StrokeColor) is RoiColor parsedColor)
                {
                    roi.StrokeColor = parsedColor;
                }
            }
        }

        public static void PopulateCommonState(this RoiPersistenceData data, RoiBase roi, string typeKey)
        {
            data.Type = typeKey;
            data.MeasurementId = roi.MeasurementId;
            data.AdvancedMeasurements = roi.AdvancedMeasurements.Select(item => item.DeepClone()).ToArray();
            data.Common.Label = roi.Label;
            data.Common.StrokeColor = roi.StrokeColor.ToString();
            data.Common.StrokeThickness = roi.StrokeThickness;
            data.Common.IsVisible = roi.IsVisible;
            data.Common.IsLocked = roi.IsLocked;
            data.Common.Tolerance = roi.Tolerance is { } tolerance
                ? new MeasurementTolerance
                {
                    Nominal = tolerance.Nominal,
                    TolerancePlus = tolerance.TolerancePlus,
                    ToleranceMinus = tolerance.ToleranceMinus
                }
                : null;
        }
    }
}
