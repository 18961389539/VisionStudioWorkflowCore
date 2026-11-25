import pathlib
import tempfile
import unittest

from scripts.audit.context import AuditContext


class AuditDocumentationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.ctx = AuditContext(self.root)

    def test_missing_document_fails_even_for_content_checks(self) -> None:
        self.assertFalse(self.ctx.doc_present("missing.md"))
        self.assertFalse(self.ctx.doc_has("missing.md", "required"))
        self.assertEqual(self.ctx.errors, ["Required documentation missing: missing.md"])

    def test_directory_is_not_a_document(self) -> None:
        (self.root / "guide.md").mkdir()
        self.assertFalse(self.ctx.doc_present("guide.md"))
        self.assertEqual(self.ctx.errors, ["Required documentation missing: guide.md"])

    def test_missing_token_fails_and_present_token_passes(self) -> None:
        (self.root / "guide.md").write_text("Required Content", encoding="utf-8")
        self.assertTrue(self.ctx.doc_has("guide.md", "required content"))
        self.assertFalse(self.ctx.doc_has("guide.md", "absent"))
        self.assertEqual(
            self.ctx.errors,
            ["Required documentation token missing in guide.md: absent"],
        )


if __name__ == "__main__":
    unittest.main()
