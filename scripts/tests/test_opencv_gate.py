import pathlib
import subprocess
import sys
import tempfile
import unittest

from scripts.audit.catalog_contracts import audit_opencv_node_calls
from scripts.audit.context import AuditContext


class OpenCvNodeGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.nodes = self.root / "backend" / "src" / "VisionStudio.Engine" / "Nodes"
        self.nodes.mkdir(parents=True)
        (self.nodes.parent / "VisionStudio.Engine.csproj").write_text(
            '<PackageReference Include="JLVisionLib"/><PackageReference Include="Microsoft.ML.OnnxRuntime"/>',
            encoding="utf-8",
        )
        self.ctx = AuditContext(self.root)

    def test_direct_algorithm_call_fails_even_with_old_baseline(self) -> None:
        nested = self.nodes / "Matching"
        nested.mkdir()
        (nested / "ExampleNode.cs").write_text(
            "class ExampleNode : IVisionNodeExecutor { void Run() { Cv2.GaussianBlur(); } }",
            encoding="utf-8",
        )
        baseline = self.root / "scripts" / "opencv-call-baseline.json"
        baseline.parent.mkdir()
        baseline.write_text('[["ExampleNode.cs", "ExampleNode", "GaussianBlur"]]', encoding="utf-8")
        audit_opencv_node_calls(self.ctx)
        self.assertEqual(
            self.ctx.errors,
            ["OpenCV algorithm call remains in node: ExampleNode.cs: Cv2.GaussianBlur"],
        )

    def test_existing_io_drawing_and_specific_3d_exceptions_remain_allowed(self) -> None:
        (self.nodes / "ExampleNode.cs").write_text(
            "class ExampleNode : IVisionNodeExecutor { void Run() { Cv2.ImRead(); Cv2.Circle(); } }",
            encoding="utf-8",
        )
        (self.nodes / "HandEye3DCalibrationNode.cs").write_text(
            "class HandEye3DCalibrationNode : IVisionNodeExecutor { void Run() { Cv2.CalibrateHandEye(); } }",
            encoding="utf-8",
        )
        audit_opencv_node_calls(self.ctx)
        self.assertEqual(self.ctx.errors, [])

    def test_removed_baseline_writer_is_rejected(self) -> None:
        script = pathlib.Path(__file__).resolve().parents[1] / "static-audit.py"
        result = subprocess.run(
            [sys.executable, str(script), "--write-opencv-baseline"],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn("Unknown option", result.stderr)


if __name__ == "__main__":
    unittest.main()
