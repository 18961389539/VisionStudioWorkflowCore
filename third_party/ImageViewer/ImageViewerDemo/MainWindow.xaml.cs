using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Services;
using ImageViewerDemo.Localization;
using ImageViewer.Models;

namespace ImageViewerDemo;

public partial class MainWindow : Window, IDisposable
{
    private CancellationTokenSource? _openCancellation;
    private bool _isOpeningData;
    private bool _allowClose;
    private bool _closePromptActive;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnMainWindowLoaded;
        Viewer.ImageDocumentOpened += (_, _) => ClearOpenSummary();
    }

    /// <summary>
    /// 启动时自动加载随程序分发的单通道灰度测试图。
    /// Chinese: 便于直接演示测量与标注功能；文件缺失或加载失败时静默跳过，不阻塞主窗口。
    /// English: Loads the bundled single-channel gray test image on startup for immediate measurement demos.
    /// </summary>
    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        // --sample 让 Demo 直接载入随程序分发的测量示例（两张线 + 标定 + 公差），
        // 用于演示、录屏和文档截图；不带参数时仍按原来的测试图流程启动。
        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--sample", StringComparison.OrdinalIgnoreCase)))
        {
            OnMeasurementSampleClick(this, new RoutedEventArgs());
            return;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "TestImage.png");
        if (!File.Exists(path) || !TryBeginOpen(DemoText.Get("ImageLoadingStatus"), isIndeterminate: false))
        {
            return;
        }

        try
        {
            CancellationToken token = _openCancellation!.Token;
            BitmapSource bitmap = await LoadImageWithProgressAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (Viewer.ImageSource != null || Viewer.ImageViewer.IsImageLoading)
            {
                return;
            }
            Viewer.ImageSource = bitmap;
            Viewer.ImageViewer.FitImageToView();
            Viewer.MarkDocumentClean();
        }
        catch (OperationCanceledException)
        {
            SetOpenStatus(DemoText.Get("OpenCancelledStatus"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, DemoText.Get("OpenImageErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOpen();
        }
    }


    private async void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        if (_isOpeningData)
        {
            return;
        }

        try
        {
            await Viewer.ShowOpenImageDialogAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, DemoText.Get("OpenImageErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnMeasurementSampleClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOpen(DemoText.Get("MeasurementSampleLoading"), isIndeterminate: true))
        {
            return;
        }

        try
        {
            CancellationToken token = _openCancellation!.Token;
            token.ThrowIfCancellationRequested();
            using CancellationTokenRegistration registration = token.Register(Viewer.ImageViewer.CancelImageLoad);
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "MeasurementSample.ivsession");
            if (await Viewer.OpenProjectFromFileAsync(path))
            {
                SetOpenSummary(DemoText.Get("MeasurementSampleGuide"));
                // The bundled session can be opened in a window of any size.
                Viewer.UpdateLayout();
                Viewer.ImageViewer.FitImageToView();
            }
        }
        catch (OperationCanceledException)
        {
            SetOpenStatus(DemoText.Get("OpenCancelledStatus"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, DemoText.Get("OpenMeasurementSampleErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOpen();
        }
    }

    private async void OnOpenVolumeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = DemoText.Get("OpenVolumeDialogTitle"),
            Filter = DemoText.Get("OpenVolumeDialogFilter"),
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (dialog.FileNames.Length < 2)
        {
            MessageBox.Show(this, DemoText.Get("OpenVolumeNeedMultiple"), DemoText.Get("OpenVolumeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBeginOpen(DemoText.Get("VolumeInspectingStatus"), isIndeterminate: true))
        {
            return;
        }

        try
        {
            CancellationToken cancellationToken = _openCancellation!.Token;
            string[] selectedPaths = dialog.FileNames;
            VolumeSliceOrderingResult ordering = await VolumeSliceOrderingService.OrderPathsWithMetadataAsync(selectedPaths, cancellationToken);
            IReadOnlyList<string> orderedPaths = ordering.OrderedPaths;
            List<string> importWarnings = ordering.Warnings.ToList();
            (double Value, string Source) spacingX = ResolveSpacing(ordering.Slices.Select(slice => slice.SpacingX).ToArray(), 1.0, "X", importWarnings);
            (double Value, string Source) spacingY = ResolveSpacing(ordering.Slices.Select(slice => slice.SpacingY).ToArray(), 1.0, "Y", importWarnings);
            (double Value, string Source) spacingZ = ResolveVolumeDepthSpacing(ordering, importWarnings);
            string orderSource = GetOrderSource(ordering.Source);
            bool orderNeedsConfirmation = !ordering.IsAuthoritative ||
                importWarnings.Count > 0 ||
                VolumeSliceOrderingService.HasOrderChanged(selectedPaths, orderedPaths);
            if (orderNeedsConfirmation)
            {
                bool orderConfirmed = ImageViewerDialogService.ConfirmReadOnlyTextDialog(
                    this,
                    DemoText.Get("OpenVolumeOrderChangedTitle"),
                    DemoText.Format(
                        "OpenVolumeOrderChangedMessage",
                        orderedPaths.Count,
                        orderSource,
                        Path.GetFileName(orderedPaths[0]),
                        Path.GetFileName(orderedPaths[Math.Min(1, orderedPaths.Count - 1)]),
                        Path.GetFileName(orderedPaths[Math.Max(0, orderedPaths.Count - 2)]),
                        Path.GetFileName(orderedPaths[^1]),
                        importWarnings.Count == 0 ? string.Empty : $"\n{string.Join(Environment.NewLine, importWarnings)}",
                        spacingX.Value, spacingY.Value, spacingZ.Value),
                    DemoText.Get("OpenVolumeConfirmImport"));

                if (!orderConfirmed)
                {
                    return;
                }
            }

            SetOpenStatus(DemoText.Get("VolumeVerifyingStatus"));
            openProgressBar.IsIndeterminate = false;
            openProgressBar.Value = 0;
            var inspectionProgress = new Progress<double>(value =>
            {
                if (_openCancellation?.Token == cancellationToken && !cancellationToken.IsCancellationRequested)
                {
                    openProgressBar.Value = value;
                }
            });
            VolumeImportPlan importPlan = await VolumeImportPreflight.PrepareAsync(orderedPaths, inspectionProgress, cancellationToken);
            VolumeImportEstimate estimate = importPlan.Estimate;
            if (estimate.ExceedsBudget(768L * 1024 * 1024))
            {
                MessageBoxResult memoryConfirmation = MessageBox.Show(
                    this,
                    DemoText.Format("OpenVolumeMemoryWarning", estimate.EstimatedBytes / (1024d * 1024d)),
                    DemoText.Get("OpenVolumeErrorTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (memoryConfirmation != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            openProgressBar.IsIndeterminate = false;
            openProgressBar.Value = 0;
            SetOpenStatus(DemoText.Format("VolumeLoadingProgress", 0, importPlan.SliceCount));
            var decodeProgress = new Progress<int>(completed =>
            {
                if (_openCancellation?.Token == cancellationToken && !cancellationToken.IsCancellationRequested)
                {
                    SetOpenStatus(DemoText.Format("VolumeLoadingProgress", completed, importPlan.SliceCount));
                    openProgressBar.Value = completed * 100d / importPlan.SliceCount;
                }
            });
            BitmapSource[] slices = await importPlan.LoadAllSlicesAsync(decodeProgress, cancellationToken);

            var orderMetadata = new VolumeSliceOrderMetadata(
                ordering.Source.ToString(),
                ordering.IsAuthoritative,
                ordering.Slices.Select(slice => new VolumeSliceProvenance(
                    slice.Path,
                    slice.Position,
                    slice.InstanceNumber,
                    slice.AcquisitionTime)));
            cancellationToken.ThrowIfCancellationRequested();
            SetOpenStatus(DemoText.Get("VolumeAssemblingStatus"));
            VolumeData volume = await Task.Run(
                () => new VolumeData(slices, spacingX.Value, spacingY.Value, spacingZ.Value, orderMetadata),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await Viewer.ReplaceVolumeDocumentAsync(volume, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            string summary = DemoText.Format(
                "OpenVolumeLoaded",
                slices.Length,
                estimate.Width,
                estimate.Height,
                spacingX.Value,
                spacingY.Value,
                spacingZ.Value);
            summary += Environment.NewLine + DemoText.Format(
                "OpenVolumeSourceSummary",
                orderSource,
                spacingX.Source,
                spacingY.Source,
                spacingZ.Source);
            if (importWarnings.Count > 0)
            {
                summary += Environment.NewLine + DemoText.Format(
                    "OpenVolumeWarningSummary",
                    string.Join(Environment.NewLine, importWarnings));
            }

            SetOpenSummary(summary);
        }
        catch (OperationCanceledException)
        {
            SetOpenStatus(DemoText.Get("OpenCancelledStatus"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, DemoText.Get("OpenVolumeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOpen();
        }
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closePromptActive)
        {
            return;
        }

        _closePromptActive = true;
        try
        {
            MessageBoxResult result = !Viewer.IsDirty ? MessageBoxResult.No : MessageBox.Show(
                this,
                DemoText.Get(Viewer.VolumeViewer.SliceViewer.IsDirty
                    ? Viewer.ImageViewer.IsDirty ? "UnsavedCloseBothViews" : "UnsavedCloseAxialView"
                    : "UnsavedCloseMessage"),
                DemoText.Get("UnsavedCloseTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Cancel)
            {
                return;
            }

            if (result == MessageBoxResult.Yes && !await Viewer.SaveUnsavedSessionsAsync())
            {
                MessageBox.Show(this, DemoText.Get("UnsavedCloseSaveFailed"), DemoText.Get("UnsavedCloseTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _openCancellation?.Cancel();
            try
            {
                await Viewer.DisposeAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("ImageViewer shutdown: {0}", exception);
            }
            _allowClose = true;
            // Closing is a synchronous, cancelable WPF event. Save may yield
            // back to the dispatcher, so calling Close() here re-enters the
            // window while its original Closing event is still active.
            // Queue the final close until that event has returned.
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, DemoText.Get("UnsavedCloseTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closePromptActive = false;
        }
    }

    private void OnCancelOpenClick(object sender, RoutedEventArgs e)
    {
        _openCancellation?.Cancel();
        cancelOpenButton.IsEnabled = false;
    }

    private bool TryBeginOpen(string status, bool isIndeterminate)
    {
        if (_isOpeningData)
        {
            return false;
        }

        _isOpeningData = true;
        _openCancellation = new CancellationTokenSource();
        openImageButton.IsEnabled = false;
        openVolumeButton.IsEnabled = false;
        measurementSampleButton.IsEnabled = false;
        cancelOpenButton.IsEnabled = true;
        cancelOpenButton.Visibility = Visibility.Visible;
        openProgressPanel.Visibility = Visibility.Visible;
        openProgressBar.IsIndeterminate = isIndeterminate;
        openProgressBar.Value = 0;
        SetOpenStatus(status);
        return true;
    }

    private void SetOpenStatus(string status)
    {
        openStatusText.Text = status;
    }

    private void SetOpenSummary(string summary)
    {
        openSummaryText.Text = summary;
        openSummaryScroll.ScrollToTop();
        openSummaryScroll.Visibility = Visibility.Visible;
    }

    private void ClearOpenSummary()
    {
        openSummaryText.Text = string.Empty;
        openSummaryScroll.Visibility = Visibility.Collapsed;
    }

    private void EndOpen()
    {
        _openCancellation?.Dispose();
        _openCancellation = null;
        _isOpeningData = false;
        openImageButton.IsEnabled = true;
        openVolumeButton.IsEnabled = true;
        measurementSampleButton.IsEnabled = true;
        cancelOpenButton.IsEnabled = true;
        cancelOpenButton.Visibility = Visibility.Collapsed;
        openProgressPanel.Visibility = Visibility.Collapsed;
    }

    private Task<BitmapSource> LoadImageWithProgressAsync(string filePath, CancellationToken cancellationToken)
    {
        var progress = new Progress<double>(value =>
        {
            if (_isOpeningData && !cancellationToken.IsCancellationRequested && _openCancellation?.Token == cancellationToken)
            {
                openProgressBar.Value = value;
            }
        });
        return ImageViewerImageDecoder.LoadFromFileAsync(filePath, progress, cancellationToken: cancellationToken);
    }

    private static (double Value, string Source) ResolveVolumeDepthSpacing(
        VolumeSliceOrderingResult ordering, ICollection<string> warnings)
    {
        VolumeSlicePositionSpacing positions = VolumeSliceOrderingService.AnalyzePositionSpacing(ordering);
        if (positions.HasIrregularIntervals)
        {
            warnings.Add(DemoText.Get("OpenVolumeIrregularPositions"));
        }
        else if (positions.ConflictsWithDeclaredSpacing)
        {
            warnings.Add(DemoText.Get("OpenVolumeDepthSpacingConflict"));
        }

        if (positions.Interval is { } interval)
        {
            string source = positions.HasIrregularIntervals
                ? DemoText.Get("OpenVolumeDepthSpacingApproximation")
                : DemoText.Get("OpenVolumeDepthSpacingFromPosition");
            return (interval, source);
        }

        return ResolveSpacing(ordering.Slices.Select(slice => slice.SpacingZ).ToArray(), 1.0, "Z", warnings);
    }

    private static (double Value, string Source) ResolveSpacing(
        IReadOnlyList<double?> values,
        double fallback,
        string axis,
        ICollection<string> warnings)
    {
        double[] valid = values
            .Where(value => value is { } number && double.IsFinite(number) && number > 0)
            .Select(value => value!.Value)
            .ToArray();
        if (valid.Length == 0)
        {
            warnings.Add(DemoText.Format("OpenVolumeSpacingMissing", axis, fallback));
            return (fallback, DemoText.Get("OpenVolumeSpacingDefaultSource"));
        }

        double selected = valid[0];
        bool isConsistent = valid.Length == values.Count && valid.All(value => Math.Abs(value - selected) <= 1e-9);
        if (!isConsistent)
        {
            warnings.Add(DemoText.Format("OpenVolumeSpacingWarning", axis));
        }

        return (selected, isConsistent ? "侧车元数据" : "侧车元数据首个有效值");
    }

    private static string GetOrderSource(VolumeSliceOrderSource source)
    {
        return source switch
        {
            VolumeSliceOrderSource.ExplicitPosition => "空间位置元数据",
            VolumeSliceOrderSource.InstanceNumber => "实例号元数据",
            VolumeSliceOrderSource.AcquisitionTime => "采集时间元数据",
            _ => "文件名自然排序（缺少完整元数据）"
        };
    }

    private static long EstimateVolumeBytes(IReadOnlyList<BitmapSource> slices, int bytesPerPixel)
    {
        if (slices.Count == 0)
        {
            return 0;
        }

        return checked((long)slices[0].PixelWidth * slices[0].PixelHeight * slices.Count * bytesPerPixel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = null;
        Viewer.Dispose();
        GC.SuppressFinalize(this);
    }
}
