using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Effects;

namespace ImageViewer.Rendering
{
    internal static class DisplayLutShaderCompiler
    {
        private const uint D3DCompileOptimizationLevel3 = 1u << 15;

        /// <summary>着色器源码在资源容器里的键后缀（小写化的相对路径）。</summary>
        private const string ShaderResourceKeySuffix = "shaders/displayluteffect.ps.hlsl";
        private const string ShaderSourceName = "DisplayLutEffect.ps.hlsl";
        private static readonly Lazy<PixelShader> Shader = new(CreatePixelShader, isThreadSafe: true);

        public static PixelShader GetPixelShader() => Shader.Value;

        private static PixelShader CreatePixelShader()
        {
            byte[] sourceBytes = LoadShaderSource();
            IntPtr codePointer = IntPtr.Zero;
            IntPtr errorPointer = IntPtr.Zero;

            try
            {
                int hr = D3DCompile(
                    sourceBytes,
                    (nuint)sourceBytes.Length,
                    ShaderSourceName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    "main",
                    "ps_2_0",
                    D3DCompileOptimizationLevel3,
                    0,
                    out codePointer,
                    out errorPointer);

                if (hr < 0)
                {
                    string error = errorPointer != IntPtr.Zero ? ReadBlob(errorPointer) : $"HRESULT 0x{hr:X8}";
                    throw new InvalidOperationException($"Failed to compile display LUT shader. {error}");
                }

                byte[] bytecode = ReadBlobBytes(codePointer);
                var pixelShader = new PixelShader();
                using var stream = new MemoryStream(bytecode, writable: false);
                pixelShader.SetStreamSource(stream);
                if (pixelShader.CanFreeze)
                {
                    pixelShader.Freeze();
                }

                return pixelShader;
            }
            finally
            {
                ReleaseBlob(errorPointer);
                ReleaseBlob(codePointer);
            }
        }

        /// <summary>
        /// 读取着色器源码。
        /// Chinese: WPF 工程的 &lt;Resource&gt; 项会被并进 <c>&lt;程序集&gt;.g.resources</c> 容器，键是小写化的相对路径
        /// （`shaders/displayluteffect.ps.hlsl`），既不是顶层清单资源、也不会以小写形式出现在程序集里。
        /// 这里两种形式都支持：先找容器条目，再退回顶层清单资源。
        /// English: WPF merges &lt;Resource&gt; items into the <c>&lt;assembly&gt;.g.resources</c> container under a lowercased
        /// relative path, so look there first and fall back to a plain manifest resource.
        /// </summary>
        private static byte[] LoadShaderSource()
        {
            Assembly assembly = typeof(DisplayLutShaderCompiler).Assembly;
            string? containerName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));

            if (containerName != null)
            {
                using Stream containerStream = assembly.GetManifestResourceStream(containerName)!;
                using var reader = new ResourceReader(containerStream);
                foreach (DictionaryEntry entry in reader)
                {
                    if (entry.Key is string key &&
                        key.EndsWith(ShaderResourceKeySuffix, StringComparison.OrdinalIgnoreCase) &&
                        entry.Value is Stream shaderStream)
                    {
                        using (shaderStream)
                        using (var buffer = new MemoryStream())
                        {
                            shaderStream.CopyTo(buffer);
                            return buffer.ToArray();
                        }
                    }
                }
            }

            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ShaderResourceKeySuffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                throw new InvalidOperationException(
                    $"Display LUT shader source '{ShaderResourceKeySuffix}' was not found in '{assembly.GetName().Name}'. "
                    + "It must be declared as <Resource> (or <EmbeddedResource>) in the project file.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Unable to open display LUT shader source resource stream.");
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        private static string ReadBlob(IntPtr blobPointer)
        {
            return Encoding.UTF8.GetString(ReadBlobBytes(blobPointer));
        }

        private static byte[] ReadBlobBytes(IntPtr blobPointer)
        {
            var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPointer);
            try
            {
                int size = checked((int)blob.GetBufferSize());
                byte[] data = new byte[size];
                Marshal.Copy(blob.GetBufferPointer(), data, 0, size);
                return data;
            }
            finally
            {
                Marshal.ReleaseComObject(blob);
            }
        }

        private static void ReleaseBlob(IntPtr blobPointer)
        {
            if (blobPointer != IntPtr.Zero)
            {
                Marshal.Release(blobPointer);
            }
        }

#pragma warning disable CA2101
        [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
        private static extern int D3DCompile(
            byte[] srcData,
            nuint srcDataSize,
            [MarshalAs(UnmanagedType.LPStr)]
            string sourceName,
            IntPtr defines,
            IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)]
            string entryPoint,
            [MarshalAs(UnmanagedType.LPStr)]
            string target,
            uint flags1,
            uint flags2,
            out IntPtr code,
            out IntPtr errorMsgs);
#pragma warning restore CA2101

        [ComImport]
        [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig]
            IntPtr GetBufferPointer();

            [PreserveSig]
            nuint GetBufferSize();
        }
    }
}
