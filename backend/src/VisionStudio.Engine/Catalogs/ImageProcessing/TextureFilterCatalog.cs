using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Catalog entries for the custom convolution and FFT frequency-filter nodes (2026-10-27).</summary>
internal static class TextureFilterCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);

    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("image.convolve", "Custom Convolution", "Preprocess", [Exec, Image],
            [
                new("image", VisionDataType.Image), new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer),
                new("kernelWidth", VisionDataType.Integer), new("kernelHeight", VisionDataType.Integer),
                new("kernelSum", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("kernelJson", "Kernel JSON", "textarea", "[[0,-1,0],[-1,5,-1],[0,-1,0]]", Group: "Kernel",
                    Description: "Kernel as a JSON array of rows, for example [[-1,-1,-1],[-1,8,-1],[-1,-1,-1]] for an edge kernel. Up to 31x31, values within +/-10000, every row must have the same length."),
                new("normalize", "Normalize Kernel", "boolean", true, Group: "Kernel",
                    Description: "Divide the kernel by the sum of its values so a uniform area keeps its brightness. Edge/difference kernels usually sum to 0 and must turn this off, otherwise the node fails instead of producing a black image."),
                new("delta", "Delta", "number", 0, -100000, 100000, 0.5, Group: "Kernel",
                    Description: "Constant added to every filtered pixel after convolution. Use a positive value to lift an edge image out of negative clipping."),
                new("anchorX", "Anchor X", "number", -1, -1, 30, 1, Group: "Anchor",
                    Description: "Kernel column aligned with the output pixel. -1 means the geometric center; set an explicit anchor for asymmetric kernels (for example a directional ridge detector)."),
                new("anchorY", "Anchor Y", "number", -1, -1, 30, 1, Group: "Anchor",
                    Description: "Kernel row aligned with the output pixel. -1 means the geometric center."),
                new("borderType", "Border Type", "select", "reflect101", Group: "Anchor", Options:
                [
                    new("Reflect 101 (default)", "reflect101"),
                    new("Reflect", "reflect"),
                    new("Replicate edge", "replicate"),
                    new("Constant zero", "constant")
                ], Description: "How pixels outside the image are extrapolated. Reflect 101 is the least biased for measurement edges; constant zero darkens the border and should only be used for explicitly padded work."),
            ],
            "Convolve the image with a user-supplied kernel. Wire: image -> filtered image. Computation runs in float32 and is converted back to the source depth with saturation, so negative side lobes clip to 0 instead of wrapping to 255. Covers kernels that the fixed blur/sharpen/morphology nodes cannot express (asymmetric, directional, ridge, custom sharpening). Failures: non-rectangular or oversized kernel, non-finite values, normalize with a zero-sum kernel, anchor out of range, unsupported depth. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: []),

        new("image.fftFilter", "Frequency Filter (FFT)", "Preprocess", [Exec, Image],
            [
                new("image", VisionDataType.Image), new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer),
                new("maskPassRatio", VisionDataType.Double), new("paddedWidth", VisionDataType.Integer),
                new("paddedHeight", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("filter", "Filter Type", "select", "bandstop", Group: "Band", Options:
                [
                    new("Band stop (remove one period)", "bandstop"),
                    new("Band pass (keep only one period)", "bandpass"),
                    new("Low pass (keep coarse structure)", "lowpass"),
                    new("High pass (keep fine detail)", "highpass")
                ], Description: "Band stop is the texture/moiré workhorse: it removes a periodic pattern whose spacing falls inside the wavelength band while leaving everything else untouched."),
                new("wavelengthMinPx", "Minimum Wavelength", "number", 3, 1, 100000, 0.5, Unit: "px", Group: "Band",
                    VisibleWhen: new("filter", ["lowpass", "bandpass", "bandstop"]),
                    Description: "Shortest period (in pixels) in the band — the feature period that should be removed or kept. 3 px means 'one cycle every 3 pixels', that is 1/3 cycles per pixel."),
                new("wavelengthMaxPx", "Maximum Wavelength", "number", 30, 1, 100000, 0.5, Unit: "px", Group: "Band",
                    VisibleWhen: new("filter", ["highpass", "bandpass", "bandstop"]),
                    Description: "Longest period (in pixels) in the band. Must be >= Minimum Wavelength for band pass/stop; larger values reach closer to the DC (coarse) end of the spectrum."),
                new("directionDeg", "Band Direction", "number", 0, -360, 360, 1, Unit: "deg", Group: "Direction",
                    Description: "Center direction of the band in image coordinates (0 = horizontal stripes, 90 = vertical). Frequencies are symmetric, so 0 and 180 describe the same band."),
                new("directionWidthDeg", "Direction Width", "number", 0, 0, 360, 1, Unit: "deg", Group: "Direction",
                    Description: "Angular width of the band. 0 removes the direction window and treats the band as rotationally symmetric; 15-30 deg targets brushed, turned or woven textures that only run one way."),
                new("maskBlurPx", "Mask Softness", "number", 0, 0, 8, 0.1, Unit: "px", Group: "Mask",
                    Description: "Gaussian softening of the frequency mask in spectrum pixels. 0 gives a hard edge (sharpest result, most ringing); 0.5-2 trades a little selectivity for far fewer ringing artifacts around strong edges."),
                new("keepDc", "Keep DC (preserve brightness)", "boolean", true, Group: "Mask",
                    Description: "Force the zero-frequency gain back to 1 so the mean brightness survives high pass and band stop. Turn off only when the absolute brightness level is irrelevant and a zero-mean result is wanted."),
                new("outputMode", "Output", "select", "filtered", Group: "Output", Options:
                [
                    new("Filtered image", "filtered"),
                    new("Removed component (input - filtered)", "removed")
                ], Description: "Filtered is the cleaned result used for inspection. Removed isolates the pattern that was filtered out, which is the fastest way to tune the wavelength band. The removed component is zero-mean, so for 8/16-bit output the node adds each channel's mean back (float32 input keeps the pure difference); otherwise half of the residual would clip to black."),
                new("maxPixels", "Maximum Padded Pixels", "number", 16000000, 1000, 64000000, 1000000, Group: "Limits",
                    Description: "Budget on the padded FFT size (width and height are rounded up to a fast DFT size). FFT memory grows with the padded area and runs per channel; exceeding the budget fails with a size message instead of exhausting memory."),
            ],
            "Frequency-domain filtering by feature period. The image is padded to a fast DFT size, transformed, multiplied by a gain mask, and transformed back; DC sits at (0,0) and wrap-around is handled by distance to the nearest corner, so no shift is needed. Wavelengths are given in pixels (period), not normalized frequency. Optional direction window turns a rotationally symmetric band into a directional notch for brushed/woven/turned textures. maskPassRatio exposes how much of the spectrum survived, which makes an over-wide or over-narrow band obvious in the trace. Failures: unsupported depth, wavelengthMin > wavelengthMax, empty mask (band rejects every frequency), or a padded size above maxPixels. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [])
    ];
}
