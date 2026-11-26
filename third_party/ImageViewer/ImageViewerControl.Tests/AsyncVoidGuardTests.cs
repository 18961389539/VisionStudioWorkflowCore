using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 防止新增未兜底的 async void 事件处理器：异常从 async void 逃逸会直接击穿 Dispatcher。
    /// Chinese: 每个 async void 必须自带 try/catch，或经 RunUiOperationAsync/RunShutdownOperationAsync 统一兜底。
    /// English: Guards against unguarded async void handlers, whose exceptions would reach the Dispatcher unhandled.
    /// </summary>
    public partial class AsyncVoidGuardTests
    {
        private static readonly string[] UiProjects = ["ImageViewerControl", "ImageViewerDemo"];

        [GeneratedRegex(@"\basync\s+void\s+(?<name>[A-Za-z_]\w*)\s*\(")]
        private static partial Regex AsyncVoidPattern();

        [GeneratedRegex(@"\bcatch\b")]
        private static partial Regex CatchPattern();

        [Fact]
        public void AsyncVoidHandlers_AreGuarded()
        {
            string repositoryRoot = FindRepositoryRoot();
            var violations = new List<string>();
            int handlerCount = 0;

            foreach (string projectName in UiProjects)
            {
                foreach (string file in EnumerateSourceFiles(Path.Combine(repositoryRoot, projectName)))
                {
                    string source = File.ReadAllText(file);
                    foreach (Match match in AsyncVoidPattern().Matches(source))
                    {
                        handlerCount++;
                        string body = ExtractBody(source, match.Index + match.Length);
                        if (!IsGuarded(body))
                        {
                            violations.Add($"{Path.GetRelativePath(repositoryRoot, file)}: {match.Groups["name"].Value}");
                        }
                    }
                }
            }

            Assert.True(handlerCount >= 15, $"Expected to find the existing async void handlers, found {handlerCount}.");
            Assert.True(
                violations.Count == 0,
                "async void handlers must catch exceptions or call RunUiOperationAsync/RunShutdownOperationAsync:" +
                Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void CoreAssembly_DoesNotUseAsyncVoid()
        {
            string repositoryRoot = FindRepositoryRoot();
            var violations = new List<string>();

            foreach (string file in EnumerateSourceFiles(Path.Combine(repositoryRoot, "ImageViewer.Core")))
            {
                string source = File.ReadAllText(file);
                foreach (Match match in AsyncVoidPattern().Matches(source))
                {
                    violations.Add($"{Path.GetRelativePath(repositoryRoot, file)}: {match.Groups["name"].Value}");
                }
            }

            Assert.Empty(violations);
        }

        private static IEnumerable<string> EnumerateSourceFiles(string projectDirectory)
        {
            foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(projectDirectory, file);
                if (relative.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }

        private static string ExtractBody(string source, int searchStart)
        {
            int braceIndex = source.IndexOf('{', searchStart);
            int expressionIndex = source.IndexOf("=>", searchStart, StringComparison.Ordinal);

            if (expressionIndex >= 0 && (braceIndex < 0 || expressionIndex < braceIndex))
            {
                int end = source.IndexOf(';', expressionIndex);
                return end < 0 ? source[expressionIndex..] : source[expressionIndex..end];
            }

            if (braceIndex < 0)
            {
                return string.Empty;
            }

            int endIndex = FindMatchingBrace(source, braceIndex);
            return source[braceIndex..(endIndex + 1)];
        }

        private static bool IsGuarded(string body)
        {
            return body.Contains("RunUiOperationAsync", StringComparison.Ordinal) ||
                   body.Contains("RunShutdownOperationAsync", StringComparison.Ordinal) ||
                   CatchPattern().IsMatch(body);
        }

        private static int FindMatchingBrace(string text, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < text.Length; i++)
            {
                char current = text[i];
                if (current == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        i = SkipLineComment(text, i);
                        continue;
                    }

                    if (text[i + 1] == '*')
                    {
                        i = SkipBlockComment(text, i);
                        continue;
                    }
                }

                if (current == '\'')
                {
                    i = SkipCharacterLiteral(text, i);
                    continue;
                }

                if (current == '"')
                {
                    i = SkipStringLiteral(text, i);
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return text.Length - 1;
        }

        private static int SkipLineComment(string text, int start)
        {
            int end = text.IndexOf('\n', start);
            return end < 0 ? text.Length - 1 : end;
        }

        private static int SkipBlockComment(string text, int start)
        {
            int end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? text.Length - 1 : end + 1;
        }

        private static int SkipCharacterLiteral(string text, int start)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '\'')
                {
                    return i;
                }
            }

            return text.Length - 1;
        }

        private static int SkipStringLiteral(string text, int start)
        {
            if (start + 2 < text.Length && text[start + 1] == '"' && text[start + 2] == '"')
            {
                int terminator = text.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
                return terminator < 0 ? text.Length - 1 : terminator + 2;
            }

            bool verbatim = start > 0 && (text[start - 1] == '@' || text[start - 1] == '$');
            for (int i = start + 1; i < text.Length; i++)
            {
                if (verbatim)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i++;
                            continue;
                        }

                        return i;
                    }

                    continue;
                }

                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '"')
                {
                    return i;
                }
            }

            return text.Length - 1;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ImageViewer.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("ImageViewer.sln");
        }
    }
}