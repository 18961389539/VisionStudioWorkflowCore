using System.Collections.Concurrent;
using System.Text.Json;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Guided camera-intrinsics session: accumulate board views with quality gates,
/// accept/reject, then solve with the existing JLVision calibrator. This is the
/// backend half of a HALCON-style Calibration Assistant slice — not a full product
/// (no live camera preview, no fisheye, no auto-pose planner).
/// </summary>
public static class CalibrationIntrinsicsWizard
{
    public sealed record BoardConfig(
        string Pattern,
        int Columns,
        int Rows,
        double SpacingMm,
        double MinBlurVariance = 0,
        double MinCoverage = 0,
        double MaxGridError = 2,
        double SaddleThreshold = 10);

    public sealed record ViewEntry(
        int Index,
        bool Enabled,
        string Label,
        CalibrationViewQuality.Report Quality,
        CameraCalibrationView View);

    public sealed record SessionState(
        string SessionId,
        BoardConfig Board,
        int? ImageWidth,
        int? ImageHeight,
        IReadOnlyList<ViewEntry> Views,
        int EnabledCount,
        CameraCalibrationModel? Model,
        object? LastSolveSummary);

    public sealed class Session
    {
        private readonly object _gate = new();
        private readonly List<ViewEntry> _views = [];
        private int _nextIndex;
        private int? _imageWidth;
        private int? _imageHeight;
        private CameraCalibrationModel? _model;
        private object? _lastSolveSummary;

        public Session(string sessionId, BoardConfig board)
        {
            SessionId = sessionId;
            Board = ValidateBoard(board);
        }

        public string SessionId { get; }
        public BoardConfig Board { get; private set; }

        public SessionState Snapshot()
        {
            lock (_gate)
            {
                return new SessionState(
                    SessionId,
                    Board,
                    _imageWidth,
                    _imageHeight,
                    _views.Select(CloneEntry).ToArray(),
                    _views.Count(v => v.Enabled),
                    _model,
                    _lastSolveSummary);
            }
        }

        public SessionState UpdateBoard(BoardConfig board)
        {
            lock (_gate)
            {
                if (_views.Count > 0) throw new InvalidOperationException("Board configuration cannot change after views are captured; create a new session.");
                Board = ValidateBoard(board);
                _model = null;
                _lastSolveSummary = null;
                return SnapshotUnlocked();
            }
        }

        public SessionState AddView(CameraCalibrationView view, CalibrationViewQuality.Report quality, string? label = null, bool? enable = null)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(quality);
            if (view.ObjectPoints is null || view.ImagePoints is null || view.ObjectPoints.Count != view.ImagePoints.Count)
                throw new InvalidOperationException("View object/image point counts must match.");
            if (view.ObjectPoints.Count == 0) throw new InvalidOperationException("Cannot add an empty view.");

            lock (_gate)
            {
                if (_views.Count >= 100) throw new InvalidOperationException("Calibration view budget exceeded (100).");
                // Infer image size from extrema when the session has none yet (JSON-imported views).
                var maxU = view.ImagePoints.Max(p => p[0]);
                var maxV = view.ImagePoints.Max(p => p[1]);
                if (_imageWidth is null)
                {
                    // Prefer explicit size from quality path; otherwise pad to a safe even size.
                    _imageWidth = Math.Max(16, (int)Math.Ceiling(maxU) + 1);
                    _imageHeight = Math.Max(16, (int)Math.Ceiling(maxV) + 1);
                }
                else if (maxU >= _imageWidth || maxV >= _imageHeight)
                    throw new InvalidOperationException($"View points fall outside session image size {_imageWidth}x{_imageHeight}.");

                var shouldEnable = enable ?? quality.Verdict != CalibrationViewQuality.Verdict.Fail;
                var index = _nextIndex++;
                var entry = new ViewEntry(index, shouldEnable, string.IsNullOrWhiteSpace(label) ? $"view-{index}" : label.Trim(), quality, CloneView(view));
                _views.Add(entry);
                _model = null;
                _lastSolveSummary = null;
                return SnapshotUnlocked();
            }
        }

        public SessionState SetImageSize(int width, int height)
        {
            if (width is < 16 or > 32768 || height is < 16 or > 32768)
                throw new InvalidOperationException("ImageWidth and ImageHeight must each be between 16 and 32768 pixels.");
            lock (_gate)
            {
                if (_views.Count > 0 && (_imageWidth != width || _imageHeight != height))
                    throw new InvalidOperationException("Image size is locked after the first view; create a new session for a different camera.");
                _imageWidth = width;
                _imageHeight = height;
                return SnapshotUnlocked();
            }
        }

        public SessionState SetEnabled(int index, bool enabled)
        {
            lock (_gate)
            {
                var i = _views.FindIndex(v => v.Index == index);
                if (i < 0) throw new InvalidOperationException($"View {index} was not found in this session.");
                _views[i] = _views[i] with { Enabled = enabled };
                _model = null;
                _lastSolveSummary = null;
                return SnapshotUnlocked();
            }
        }

        public SessionState RemoveView(int index)
        {
            lock (_gate)
            {
                var removed = _views.RemoveAll(v => v.Index == index);
                if (removed == 0) throw new InvalidOperationException($"View {index} was not found in this session.");
                _model = null;
                _lastSolveSummary = null;
                return SnapshotUnlocked();
            }
        }

        public CameraCalibrationRequest BuildRequest()
        {
            lock (_gate)
            {
                if (_imageWidth is null || _imageHeight is null)
                    throw new InvalidOperationException("Set the camera image size before solving (or add a detected view first).");
                var enabled = _views.Where(v => v.Enabled).Select(v => CloneView(v.View)).ToList();
                if (enabled.Count < 3)
                    throw new InvalidOperationException($"Intrinsic calibration requires at least three enabled views; currently {enabled.Count}.");
                return new CameraCalibrationRequest(_imageWidth.Value, _imageHeight.Value, enabled);
            }
        }

        public SessionState StoreSolveResult(CameraCalibrationModel model, object summary)
        {
            lock (_gate)
            {
                _model = model;
                _lastSolveSummary = summary;
                return SnapshotUnlocked();
            }
        }

        private SessionState SnapshotUnlocked() => new(
            SessionId,
            Board,
            _imageWidth,
            _imageHeight,
            _views.Select(CloneEntry).ToArray(),
            _views.Count(v => v.Enabled),
            _model,
            _lastSolveSummary);

        private static ViewEntry CloneEntry(ViewEntry e) => e with { View = CloneView(e.View) };

        private static CameraCalibrationView CloneView(CameraCalibrationView view) => new(
            view.ObjectPoints.Select(p => p.ToList()).ToList(),
            view.ImagePoints.Select(p => p.ToList()).ToList());

        private static BoardConfig ValidateBoard(BoardConfig board)
        {
            var pattern = (board.Pattern ?? "").Trim().ToLowerInvariant();
            if (pattern is not ("chessboard" or "circles")) throw new InvalidOperationException("Board pattern must be chessboard or circles.");
            if (board.Columns is < 2 or > 30 || board.Rows is < 2 or > 30 || board.Rows * board.Columns < 6)
                throw new InvalidOperationException("Board needs 2..30 rows/columns and at least six points.");
            if (!double.IsFinite(board.SpacingMm) || board.SpacingMm <= 0 || board.SpacingMm > 10000)
                throw new InvalidOperationException("Board spacing must be positive and <= 10000 mm.");
            if (!double.IsFinite(board.MinBlurVariance) || board.MinBlurVariance < 0)
                throw new InvalidOperationException("minBlurVariance must be finite and non-negative.");
            if (!double.IsFinite(board.MinCoverage) || board.MinCoverage is < 0 or > 1)
                throw new InvalidOperationException("minCoverage must be in 0..1.");
            if (!double.IsFinite(board.MaxGridError) || board.MaxGridError is < 0.1 or > 20)
                throw new InvalidOperationException("maxGridError must be 0.1..20 px.");
            if (!double.IsFinite(board.SaddleThreshold) || board.SaddleThreshold is < 0.1 or > 255)
                throw new InvalidOperationException("saddleThreshold must be 0.1..255.");
            return board with { Pattern = pattern };
        }
    }

    /// <summary>In-memory wizard sessions for the API. Not durable across process restarts.</summary>
    public sealed class SessionStore
    {
        private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

        public Session Create(BoardConfig board)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new Session(id, board);
            if (!_sessions.TryAdd(id, session)) throw new InvalidOperationException("Failed to allocate a wizard session id.");
            return session;
        }

        public Session Get(string sessionId) =>
            _sessions.TryGetValue(sessionId, out var session)
                ? session
                : throw new KeyNotFoundException($"Calibration wizard session '{sessionId}' was not found or has expired.");

        public bool TryGet(string sessionId, out Session? session) => _sessions.TryGetValue(sessionId, out session);

        public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

        public string ExportCalibrationDataJson(Session session)
        {
            var request = session.BuildRequest();
            return JsonSerializer.Serialize(request);
        }
    }
}
