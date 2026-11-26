namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private readonly IImageViewerForcedRefreshScheduler _analysisRefreshScheduler;

        private void RequestAnalysisRefresh(bool force = false, bool immediate = false)
            => _analysisRefreshScheduler.Request(force, immediate);

        private void BeginAnalysisRefreshBatch()
            => _analysisRefreshScheduler.BeginBatch();

        private void EndAnalysisRefreshBatch(bool immediate = false)
            => _analysisRefreshScheduler.EndBatch(immediate);

        private void RefreshAnalysisPanels(bool force = false)
        {
            UpdateProfile(force);
            UpdateInfoPanel(force);
        }
    }
}
