# Test Layers

ImageViewer tests use xUnit traits for CI selection:

- `Category=Smoke`: host construction, dependency composition, and WPF lifecycle checks.
- `Category=Unit`: deterministic service, controller, and state behavior.
- `Category=Integration`: workflows crossing multiple ImageViewer services.
- `Category=Wpf`: tests requiring the STA WPF dispatcher.
- `Category=Compatibility`: legacy API and compatibility-shim contracts.
- `Category=Performance`: behavior sentinels and focused performance checks; these do not impose timing thresholds on normal pull requests.

`ImageViewer.Core.Tests` is a separate `net8.0`/`net10.0` test project. It can run without WPF and is the first coverage-gated layer. The desktop test project continues to cover adapters, rendering, dialogs, and WPF lifecycle behavior.

Smoke tests run separately in CI. The coverage run uses `Category!=Smoke` so the complete non-smoke test set remains visible without relying on class-name filters.

The GitHub Actions workflow also runs a focused export/batch gate before the full regression gate. Full regression failures block the workflow, while the raw test and coverage artifacts are still uploaded for diagnosis.

## Performance Baselines

`ImageViewerPerformanceBaselineTests` (also `Category=Performance`) establishes quantified baselines for the four hot paths: large-image zoom, thousands of ROIs, rapid MPR slicing, and pseudo-color tiling. The rule that keeps these tests useful:

**Assert only what is deterministic; measure and print everything else.** Wall-clock time and process memory vary by machine, so they are written through `ITestOutputHelper` for human comparison, never asserted. The assertions are deterministic counters — tile build/colorize counts, cache byte totals, cache entry counts, peak concurrency — plus one deliberately loose per-scenario guard rail ("5000 ROI mutations must finish under 30 s", "MPR cache must stay under 80 MB") that will not flake on a slow agent but will fail on an order-of-magnitude regression.

Reference numbers on the development machine (2026-10-10, Debug):

| Scenario | Deterministic assertion | Reported measurement |
| --- | --- | --- |
| 4000×4000 zoom, 2000 repeated frames | entry count == 1 (one tile, no growth) | ~5 ms total, ~330k hits/s, 0.2 MB |
| 3000×3000 pseudo-color, 200 frames | colorize invoked exactly once | 16 ms, 1.0 MB |
| 5000 ROI add + undo to exhaustion | undo bounded at `DefaultMaxDepth` (200) | add 17 ms, 200 undos 3 ms |
| 2000 ROI `ClearAll` | one pass, collection empty | <1 ms |
| 96-plane MPR sweep (requested twice) | build count == 96 (second pass is pure hits) | 66 ms, 2.2 MB |
| 256 coronal slices of 1024×400 | cache bytes ≤ 80 MB and entries < 256 | 63.7 MB, 163 entries (LRU trimming at the 64 MB budget) |

These baselines are regression tripwires, not SLOs: raising a threshold requires a note explaining why the new cost is acceptable.
