using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Native operators missing from the 1.0 managed facade, through JLVision's public HLI API.
/// Validate the native name before using each pinned operator ID; never call a mismatched ABI.
/// </summary>
internal static class JLVisionExtendedOperators
{
    private static readonly ConcurrentDictionary<int, string> Names = new();
    [DllImport("JLVisionCore", CallingConvention = CallingConvention.Cdecl, EntryPoint = "HLILogicalName")]
    private static extern IntPtr LogicalName(int id);

    private static IntPtr Begin(int id, string expected)
    {
        var name = Names.GetOrAdd(id, key =>
        {
            using var initialize = new JlImage("byte", 1, 1);
            return Marshal.PtrToStringAnsi(LogicalName(key)) ?? string.Empty;
        });
        if (!string.Equals(name, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"JLVisionCore operator ABI mismatch: {id} is '{name}', expected '{expected}'.");
        return JlNativeApi.PreCall(id);
    }

    public static JlImage Image(int id, string name, JlImage[] images, params JlTuple[] controls)
    {
        var proc = Begin(id, name); var error = 2; JlImage? result = null;
        try
        {
            for (var i = 0; i < images.Length; i++) JlNativeApi.Store(proc, i + 1, images[i]);
            for (var i = 0; i < controls.Length; i++) JlNativeApi.Store(proc, i, controls[i]);
            JlNativeApi.InitOCT(proc, 1);
            error = JlNativeApi.CallProcedure(proc);
            error = JlImage.LoadNew(proc, 1, error, out result);
            var finished = proc; proc = IntPtr.Zero;
            JlNativeApi.PostCall(finished, error);
            return result;
        }
        catch { result?.Dispose(); throw; }
        finally
        {
            foreach (var control in controls) { JlNativeApi.UnpinTuple(control); control.Dispose(); }
            if (proc != IntPtr.Zero) JlNativeApi.DestroyProcedure(proc, error);
            foreach (var image in images) GC.KeepAlive(image);
        }
    }

    public static JlTuple[] Tuples(int id, string name, int count, params JlTuple[] controls)
    {
        var proc = Begin(id, name); var error = 2; var result = new JlTuple[count];
        try
        {
            for (var i = 0; i < controls.Length; i++) JlNativeApi.Store(proc, i, controls[i]);
            for (var i = 0; i < count; i++) JlNativeApi.InitOCT(proc, i);
            error = JlNativeApi.CallProcedure(proc);
            for (var i = 0; i < count; i++) error = JlTuple.LoadNew(proc, i, error, out result[i]);
            var finished = proc; proc = IntPtr.Zero;
            JlNativeApi.PostCall(finished, error);
            return result;
        }
        catch { foreach (var tuple in result) tuple?.Dispose(); throw; }
        finally
        {
            foreach (var control in controls) { JlNativeApi.UnpinTuple(control); control.Dispose(); }
            if (proc != IntPtr.Zero) JlNativeApi.DestroyProcedure(proc, error);
        }
    }

    public static JlImage Rft(JlImage image, string direction, string norm, int width)
        => Image(1462, "rft_generic", [image], new(direction), new(norm), new(direction == "to_freq" ? "complex" : "real"), new(width));
    public static JlImage Convolve(JlImage image, JlImage filter)
        => Image(1455, "convol_fft", [image, filter]);
}
