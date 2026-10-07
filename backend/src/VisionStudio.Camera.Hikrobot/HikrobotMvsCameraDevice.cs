using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Camera.Hikrobot;

public sealed partial class HikrobotMvsCameraDevice : ICameraDevice, ICameraTelemetryProvider, IAsyncHardwareProvenanceProvider, ICameraFeatureProvider, ICameraSynchronizationProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _assemblyPath;
    private readonly string? _serial;
    private readonly string? _userName;
    private readonly string? _deviceKey;
    private Assembly? _assembly;
    private Type? _myCameraType;
    private object? _camera;
    private object? _deviceInfo;
    private HikIdentity? _identity;
    private IntPtr _bgrBuffer;
    private uint _bgrBufferBytes;
    private CameraState _state = CameraState.Closed;
    private CameraSettings _settings;
    private string? _error;
    private long _sequence;
    private long _driverDrops;
    private long? _lastNativeFrame;
    private string? _nativePixelFormat;

    public HikrobotMvsCameraDevice(CameraAdapterRegistration registration, string? assemblyPath = null)
    {
        Id = registration.Id.Trim();
        Name = string.IsNullOrWhiteSpace(registration.Name) ? Id : registration.Name.Trim();
        _serial = registration.SerialNumber;
        _userName = registration.UserDefinedName;
        _deviceKey = registration.DeviceKey;
        _assemblyPath = assemblyPath;
        _settings = registration.Settings?.Normalize() ?? new CameraSettings(TargetFps: 30);
    }

    public string Id { get; }
    public string Name { get; }
    public string Driver => "hikrobot-mvs";
    public string? Source => _serial is not null ? "serial:" + _serial : _deviceKey;
    public CameraState State => _state;
    public long FramesCaptured => Interlocked.Read(ref _sequence);
    public string? LastError => _error;
    public CameraSettings Settings => _settings;
    public CameraCapabilities Capabilities { get; } = new(
        HostSimulatedExternalTrigger: false, MaxFps: 500,
        OutputPixelFormats: [CameraOutputPixelFormat.Auto, CameraOutputPixelFormat.Mono8, CameraOutputPixelFormat.Bgr8]);

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is CameraState.Open or CameraState.Streaming) return;
            try
            {
                _assembly = HikrobotSdkReflection.Load(_assemblyPath);
                _myCameraType = HikrobotSdkReflection.MyCamera(_assembly);
                var selected = HikrobotSdkReflection.Select(HikrobotSdkReflection.Enumerate(_assembly), _serial, _userName, _deviceKey);
                _deviceInfo = selected.DeviceInfo;
                _identity = selected.Identity;
                _camera = HikrobotSdkReflection.CreateCamera(_myCameraType, _deviceInfo);
                OpenDevice(_camera);
                ConfigureLowLatency(_camera);
                ApplySettingsCore(_settings);
                AllocateBgrBuffer(_camera);
                _state = CameraState.Open;
                _error = null;
            }
            catch (Exception ex)
            {
                _error = Unwrap(ex).Message;
                _state = CameraState.Faulted;
                ReleaseSdkObjects();
                throw new InvalidOperationException($"Hikrobot camera '{Id}' open failed: {_error}", Unwrap(ex));
            }
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StopCore();
            if (_camera is not null)
            {
                try { HikrobotSdkReflection.InvokeInt(_camera, "MV_CC_CloseDevice_NET"); } catch { }
                try { HikrobotSdkReflection.InvokeInt(_camera, "MV_CC_DestroyDevice_NET"); } catch { }
            }
            ReleaseSdkObjects();
            _state = CameraState.Closed;
            _error = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == CameraState.Streaming) return;
            ApplySettingsCore(_settings);
            HikrobotSdkReflection.Check(HikrobotSdkReflection.InvokeInt(_camera!, "MV_CC_StartGrabbing_NET"), "StartGrabbing");
            _state = CameraState.Streaming;
            _error = null;
            RefreshTransportTelemetryCore(force: true);
        }
        catch (Exception ex)
        {
            _error = Unwrap(ex).Message;
            _state = CameraState.Faulted;
            throw new InvalidOperationException($"Hikrobot camera '{Id}' start failed: {_error}", Unwrap(ex));
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    public async Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var normalized = settings.Normalize();
            var wasStreaming = _state == CameraState.Streaming;
            if (wasStreaming) StopCore();
            _settings = normalized;
            if (_camera is not null) ApplySettingsCore(normalized);
            if (wasStreaming)
            {
                HikrobotSdkReflection.Check(HikrobotSdkReflection.InvokeInt(_camera!, "MV_CC_StartGrabbing_NET"), "StartGrabbing");
                _state = CameraState.Streaming;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_camera is null || _state != CameraState.Streaming) throw new InvalidOperationException($"Hikrobot camera '{Id}' is not streaming.");
            HikrobotSdkReflection.Check(HikrobotSdkReflection.InvokeInt(_camera, "MV_CC_SetCommandValue_NET", "TriggerSoftware"), "TriggerSoftware");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_camera is null || _myCameraType is null || _state != CameraState.Streaming)
                throw new InvalidOperationException($"Hikrobot camera '{Id}' is not streaming.");
            EnsureBgrBuffer();
            var frameInfoType = _myCameraType.GetNestedType("MV_FRAME_OUT_INFO_EX", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Hikrobot MV_FRAME_OUT_INFO_EX type was not found.");
            var frameInfo = Activator.CreateInstance(frameInfoType)!;
            var method = _myCameraType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "MV_CC_GetImageForBGR_NET" && x.GetParameters().Length == 4)
                ?? throw new InvalidOperationException("Hikrobot MV_CC_GetImageForBGR_NET was not found.");
            object?[] args = [_bgrBuffer, _bgrBufferBytes, frameInfo, Math.Max(1, (int)timeout.TotalMilliseconds)];
            var ret = Convert.ToInt32(method.Invoke(_camera, args));
            frameInfo = args[2] ?? frameInfo;
            if (ret != 0)
            {
                RefreshTransportTelemetryCore(force: true);
                if (IsTimeout(ret)) throw new CameraFrameTimeoutException($"Hikrobot camera '{Id}' frame timeout after {timeout.TotalMilliseconds:0} ms.");
                throw new InvalidOperationException($"Hikrobot GetImageForBGR failed: 0x{ret:x8}.");
            }
            var width = Convert.ToInt32(HikrobotSdkReflection.Member(frameInfo, "nWidth") ?? 0);
            var height = Convert.ToInt32(HikrobotSdkReflection.Member(frameInfo, "nHeight") ?? 0);
            var nativeFrame = Convert.ToInt64(HikrobotSdkReflection.Member(frameInfo, "nFrameNum") ?? 0L);
            if (_lastNativeFrame is { } previous && nativeFrame > previous + 1) Interlocked.Add(ref _driverDrops, nativeFrame - previous - 1);
            _lastNativeFrame = nativeFrame;
            _nativePixelFormat = "BGR8(MVS-converted)";
            var bytes = checked(width * height * 3);
            if (width <= 0 || height <= 0 || bytes > _bgrBufferBytes) throw new InvalidOperationException("Hikrobot returned invalid frame dimensions.");
            var bgr = new Mat(height, width, MatType.CV_8UC3);
            unsafe { Buffer.MemoryCopy((void*)_bgrBuffer, (void*)bgr.Data, bytes, bytes); }
            Mat output = bgr;
            var pixelFormat = "Bgr8";
            if (_settings.OutputPixelFormat == CameraOutputPixelFormat.Mono8)
            {
                var mono = new Mat();
                Cv2.CvtColor(bgr, mono, ColorConversionCodes.BGR2GRAY);
                bgr.Dispose();
                output = mono;
                pixelFormat = "Mono8";
            }
            var sequence = Interlocked.Increment(ref _sequence);
            var tsHigh = Convert.ToUInt64(HikrobotSdkReflection.Member(frameInfo, "nDevTimeStampHigh") ?? 0u);
            var tsLow = Convert.ToUInt64(HikrobotSdkReflection.Member(frameInfo, "nDevTimeStampLow") ?? 0u);
            var rawTimestamp = tsHigh != 0 || tsLow != 0 ? (long)((tsHigh << 32) | tsLow) : 0L;
            var tickHz = ReadInteger("GevTimestampTickFrequency");
            long? deviceTimestampNs = rawTimestamp > 0 && tickHz is > 0
                ? (long?)Math.Round(rawTimestamp * (1_000_000_000d / tickHz.Value))
                : null;
            _error = null;
            RefreshTransportTelemetryCore();
            return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, output, pixelFormat, deviceTimestampNs, nativeFrame);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
        finally { _gate.Release(); }
    }

    public CameraDeviceTelemetry GetTelemetry() => new(Interlocked.Read(ref _driverDrops), _nativePixelFormat, _lastNativeFrame, _transportTelemetry);

    public async ValueTask<HardwareProvenanceData> GetHardwareProvenanceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var assembly = _assembly ?? HikrobotSdkReflection.Load(_assemblyPath);
            var selected = _identity is null
                ? HikrobotSdkReflection.Select(HikrobotSdkReflection.Enumerate(assembly), _serial, _userName, _deviceKey)
                : (_deviceInfo!, _identity);
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["adapter"] = Driver,
                ["transportLayer"] = selected.Item2.Transport,
                ["sdkAssemblyVersion"] = assembly.GetName().Version?.ToString() ?? "unknown"
            };
            if (!string.IsNullOrWhiteSpace(selected.Item2.IpAddress)) attrs["ipAddress"] = selected.Item2.IpAddress;
            var firmware = _camera is null ? null : TryReadStringNode(_camera, "DeviceFirmwareVersion");
            return new HardwareProvenanceData(
                Manufacturer: selected.Item2.Manufacturer,
                ProductName: selected.Item2.UserDefinedName,
                Model: selected.Item2.Model,
                SerialNumber: selected.Item2.SerialNumber,
                HardwareRevision: selected.Item2.DeviceVersion,
                FirmwareVersion: firmware,
                SoftwareVersion: assembly.GetName().Version?.ToString(),
                Attributes: attrs);
        }
        finally { _gate.Release(); }
    }

    private void OpenDevice(object camera)
    {
        var methods = camera.GetType().GetMethods().Where(x => x.Name == "MV_CC_OpenDevice_NET").OrderBy(x => x.GetParameters().Length);
        foreach (var method in methods)
        {
            try
            {
                var args = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : HikrobotSdkReflection.DefaultValue(p.ParameterType)).ToArray();
                HikrobotSdkReflection.Check(Convert.ToInt32(method.Invoke(camera, args)), "OpenDevice");
                return;
            }
            catch (TargetParameterCountException) { }
        }
        throw new MissingMethodException(camera.GetType().FullName, "MV_CC_OpenDevice_NET");
    }

    private void ConfigureLowLatency(object camera)
    {
        TryInvoke(camera, "MV_CC_SetImageNodeNum_NET", 4u);
        TryInvoke(camera, "MV_CC_SetGrabStrategy_NET", 1u); // LatestImagesOnly where supported.
    }

    private void ApplySettingsCore(CameraSettings settings)
    {
        if (_camera is null) return;
        TryInvoke(_camera, "MV_CC_SetFloatValue_NET", "ExposureTime", (float)settings.ExposureUs);
        TryInvoke(_camera, "MV_CC_SetFloatValue_NET", "Gain", (float)settings.GainDb);
        TryInvoke(_camera, "MV_CC_SetBoolValue_NET", "AcquisitionFrameRateEnable", true);
        TryInvoke(_camera, "MV_CC_SetFloatValue_NET", "AcquisitionFrameRate", (float)settings.TargetFps);
        TryInvoke(_camera, "MV_CC_SetEnumValueByString_NET", "AcquisitionMode", "Continuous");
        TryInvoke(_camera, "MV_CC_SetEnumValueByString_NET", "TriggerSelector", "FrameStart");
        if (settings.TriggerMode == CameraTriggerMode.Continuous)
        {
            if (!TryInvoke(_camera, "MV_CC_SetEnumValueByString_NET", "TriggerMode", "Off")
                && !TryInvoke(_camera, "MV_CC_SetEnumValue_NET", "TriggerMode", 0u))
                throw new InvalidOperationException("Hikrobot camera could not disable TriggerMode.");
        }
        else
        {
            if (!TryInvoke(_camera, "MV_CC_SetEnumValueByString_NET", "TriggerMode", "On")
                && !TryInvoke(_camera, "MV_CC_SetEnumValue_NET", "TriggerMode", 1u))
                throw new InvalidOperationException("Hikrobot camera could not enable TriggerMode.");
            var source = settings.TriggerMode == CameraTriggerMode.Software ? "Software" : settings.ExternalTriggerSource;
            if (!TryInvoke(_camera, "MV_CC_SetEnumValueByString_NET", "TriggerSource", source))
            {
                if (settings.TriggerMode == CameraTriggerMode.Software && TryInvoke(_camera, "MV_CC_SetEnumValue_NET", "TriggerSource", 7u)) { }
                else throw new InvalidOperationException($"Hikrobot camera does not support TriggerSource '{source}'.");
            }
        }
    }

    private void AllocateBgrBuffer(object camera)
    {
        var payload = TryGetPayloadSize(camera);
        var requested = checked((ulong)Math.Max(1024 * 1024, payload) * 3UL);
        if (requested > int.MaxValue) throw new InvalidOperationException("Hikrobot frame buffer exceeds supported host allocation.");
        _bgrBufferBytes = (uint)requested;
        _bgrBuffer = Marshal.AllocHGlobal((int)_bgrBufferBytes);
    }

    private ulong TryGetPayloadSize(object camera)
    {
        if (_myCameraType is null) return 16 * 1024 * 1024;
        foreach (var methodName in new[] { "MV_CC_GetIntValueEx_NET", "MV_CC_GetIntValue_NET" })
        {
            var method = _myCameraType.GetMethods().FirstOrDefault(x => x.Name == methodName && x.GetParameters().Length == 2);
            if (method is null) continue;
            var holderType = method.GetParameters()[1].ParameterType.GetElementType() ?? method.GetParameters()[1].ParameterType;
            var holder = Activator.CreateInstance(holderType)!;
            object?[] args = ["PayloadSize", holder];
            try
            {
                if (Convert.ToInt32(method.Invoke(camera, args)) != 0) continue;
                holder = args[1] ?? holder;
                var value = HikrobotSdkReflection.Member(holder, "nCurValue");
                if (value is not null) return Convert.ToUInt64(value);
            }
            catch { }
        }
        return 16 * 1024 * 1024;
    }

    private void EnsureBgrBuffer()
    {
        if (_bgrBuffer == IntPtr.Zero || _bgrBufferBytes == 0) throw new InvalidOperationException("Hikrobot host frame buffer is not allocated.");
    }

    private bool IsTimeout(int code)
    {
        if (_myCameraType is null) return false;
        foreach (var name in new[] { "MV_E_NODATA", "MV_E_TIMEOUT", "MV_E_BUFOVER" })
        {
            if (code == HikrobotSdkReflection.StaticInt(_myCameraType, name, int.MinValue)) return name != "MV_E_BUFOVER";
        }
        return false;
    }

    private static bool TryInvoke(object instance, string methodName, params object?[] args)
    {
        try { return HikrobotSdkReflection.InvokeInt(instance, methodName, args) == 0; } catch { return false; }
    }

    private string? TryReadStringNode(object camera, string key)
    {
        if (_myCameraType is null) return null;
        var method = _myCameraType.GetMethods().FirstOrDefault(x => x.Name == "MV_CC_GetStringValue_NET" && x.GetParameters().Length == 2);
        if (method is null) return null;
        var holderType = method.GetParameters()[1].ParameterType.GetElementType() ?? method.GetParameters()[1].ParameterType;
        var holder = Activator.CreateInstance(holderType)!;
        object?[] args = [key, holder];
        try
        {
            if (Convert.ToInt32(method.Invoke(camera, args)) != 0) return null;
            holder = args[1] ?? holder;
            return HikrobotSdkReflection.Text(HikrobotSdkReflection.Member(holder, "chCurValue") ?? HikrobotSdkReflection.Member(holder, "strCurValue"));
        }
        catch { return null; }
    }

    private void StopCore()
    {
        if (_camera is not null && _state == CameraState.Streaming)
        {
            try { HikrobotSdkReflection.InvokeInt(_camera, "MV_CC_StopGrabbing_NET"); } catch { }
        }
        if (_state != CameraState.Closed && _state != CameraState.Faulted) _state = CameraState.Open;
    }

    private void ReleaseSdkObjects()
    {
        if (_bgrBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_bgrBuffer); _bgrBuffer = IntPtr.Zero; _bgrBufferBytes = 0; }
        _camera = null; _deviceInfo = null; _identity = null; _myCameraType = null; _assembly = null;
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(); } catch { }
        _gate.Dispose();
    }
}
