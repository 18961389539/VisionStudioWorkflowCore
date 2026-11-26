using System.Collections.Generic;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Plugins
{
    public sealed partial class RoiPluginRegistry
    {
        private static class AnnotationCatalog
        {
            public static IReadOnlyList<IBuiltInPluginRegistration> GetRegistrations()
            {
                return
                [
                    CreateRegistration<ArrowAnnotationRoi>(
                    typeKey: "arrow-annotation",
                    hitTestOrder: 29,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolArrowAnnotation"), BuiltInDrawControllers.ArrowAnnotation, 101, CreateArrowAnnotationIcon)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new ArrowAnnotationRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint(),
                            ArrowHeadLength = data.Geometry.Width > 0 ? data.Geometry.Width : 12
                        },
                        static roi => roi.P1,
                        static roi => roi.P2,
                        static (roi, data) => data.Geometry.Width = roi.ArrowHeadLength)),

                    CreateRegistration<PointAnnotationRoi>(
                    typeKey: "point-annotation",
                    hitTestOrder: 40,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolPointAnnotation"), BuiltInDrawControllers.PointAnnotation, 100, CreatePointAnnotationIcon)
                    ],
                    persistence: CreatePositionPersistence(
                        data => new PointAnnotationRoi
                        {
                            Position = data.Geometry.Position.ToPoint()
                        },
                        static roi => roi.Position)),

                    CreateRegistration<TextAnnotationRoi>(
                    typeKey: "text-annotation",
                    hitTestOrder: 30,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolTextAnnotation"), BuiltInDrawControllers.TextAnnotation, 110, CreateTextIcon)
                    ],
                    persistence: CreatePositionPersistence(
                        data => new TextAnnotationRoi
                        {
                            Position = data.Geometry.Position.ToPoint()
                        },
                        static roi => roi.Position))
                ];
            }
        }
    }
}