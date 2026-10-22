using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Camera.Basler;

public sealed partial class BaslerPylonCameraDevice : ICameraDevice, ICameraTelemetryProvider, IAsyncHardwareProvenanceProvider, ICameraFeatureProvider, ICameraSynchronizationProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _assemblyPath;
    private readonly string? _serial;
    private readonly string? _userName;
    private readonly string? _deviceKey;
    private object? _camera;
    private Assembly? _assembly;
    private object? _cameraInfo;
    private object? _streamGrabber;
    private object? _converter;
    private CameraState _state = CameraState.Closed;
    private CameraSettings _settings;
    private string? _error;
    private long _sequence;
    private long _driverDrops;
    private long? _nativeFrameId;
    private string? _nativePixelFormat;

    public BaslerPylonCameraDevice(CameraAdapterRegistration registration, string? assemblyPath = null)
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
    public string Driver => "basler-pylon";
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
                _assembly = BaslerSdkReflection.Load(_assemblyPath);
                _cameraInfo = BaslerSdkReflection.Select(BaslerSdkReflection.Enumerate(_assembly), _serial, _userName, _deviceKey);
                _camera = BaslerSdkReflection.CreateCamera(_assembly, _cameraInfo);
                _camera.GetType().GetMethod("Open", Type.EmptyTypes)?.Invoke(_camera, null);
                _streamGrabber = _camera.GetType().GetProperty("StreamGrabber")?.GetValue(_camera)
                    ?? throw new InvalidOperationException("Basler StreamGrabber was not available.");
                _converter = Activator.CreateInstance(_assembly.GetType("Basler.Pylon.PixelDataConverter", true)!)
                    ?? throw new InvalidOperationException("Basler PixelDataConverter could not be created.");
                ApplySettingsCore(_settings);
                _error = null;
                _state = CameraState.Open;
            }
            catch (Exception ex)
            {
                _error = Unwrap(ex).Message;
                _state = CameraState.Faulted;
                ReleaseSdkObjects();
                throw new InvalidOperationException($"Basler camera '{Id}' open failed: {_error}", Unwrap(ex));
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
                try { _camera.GetType().GetMethod("Close", Type.EmptyTypes)?.Invoke(_camera, null); } catch { }
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
            StartGrabber();
            _state = CameraState.Streaming;
            _error = null;
            RefreshTransportTelemetryCore(force: true);
        }
        catch (Exception ex)
        {
            _error = Unwrap(ex).Message;
            _state = CameraState.Faulted;
            throw new InvalidOperationException($"Basler camera '{Id}' start failed: {_error}", Unwrap(ex));
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
                StartGrabber();
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
            if (_camera is null || _state != CameraState.Streaming) throw new InvalidOperationException($"Basler camera '{Id}' is not streaming.");
            var ready = _camera.GetType().GetMethods().FirstOrDefault(x => x.Name == "WaitForFrameTriggerReady" && x.GetParameters().Length == 2);
            if (ready is not null)
            {
                var timeoutHandling = Enum.Parse(_assembly!.GetType("Basler.Pylon.TimeoutHandling", true)!, "ThrowException", true);
                _ = ready.Invoke(_camera, [1000, timeoutHandling]);
            }
            _camera.GetType().GetMethod("ExecuteSoftwareTrigger", Type.EmptyTypes)?.Invoke(_camera, null);
        }
        catch (Exception ex) { throw new InvalidOperationException($"Basler software trigger failed: {Unwrap(ex).Message}", Unwrap(ex)); }
        finally { _gate.Release(); }
    }

    public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != CameraState.Streaming || _streamGrabber is null || _assembly is null || _converter is null)
                throw new InvalidOperationException($"Basler camera '{Id}' is not streaming.");
            object? result = null;
            try
            {
                var retrieve = _streamGrabber.GetType().GetMethods()
                    .FirstOrDefault(x => x.Name == "RetrieveResult" && x.GetParameters().Length == 2)
                    ?? throw new InvalidOperationException("Basler RetrieveResult(timeout, handling) was not found.");
                var timeoutHandling = Enum.Parse(_assembly.GetType("Basler.Pylon.TimeoutHandling", true)!, "ThrowException", true);
                result = retrieve.Invoke(_streamGrabber, [Math.Max(1, (int)timeout.TotalMilliseconds), timeoutHandling]);
                if (result is null) throw new CameraFrameTimeoutException($"Basler camera '{Id}' frame timeout.");
                if (!(bool)(result.GetType().GetProperty("GrabSucceeded")?.GetValue(result) ?? false))
                {
                    var description = Convert.ToString(result.GetType().GetProperty("ErrorDescription")?.GetValue(result));
                    throw new InvalidOperationException($"Basler grab failed: {description ?? "unknown error"}.");
                }
                var width = Convert.ToInt32(result.GetType().GetProperty("Width")?.GetValue(result));
                var height = Convert.ToInt32(result.GetType().GetProperty("Height")?.GetValue(result));
                var skipped = Convert.ToInt64(result.GetType().GetProperty("SkippedImageCount")?.GetValue(result) ?? 0L);
                Interlocked.Add(ref _driverDrops, skipped);
                _nativeFrameId = Convert.ToInt64(result.GetType().GetProperty("BlockID")?.GetValue(result) ?? 0L);
                _nativePixelFormat = Convert.ToString(result.GetType().GetProperty("PixelTypeValue")?.GetValue(result));
                var output = ResolveOutputFormat(_settings.OutputPixelFormat, _nativePixelFormat);
                var channels = output == CameraOutputPixelFormat.Mono8 ? 1 : 3;
                var outputName = channels == 1 ? "Mono8" : "BGR8packed";
                var pixelType = Enum.Parse(_assembly.GetType("Basler.Pylon.PixelType", true)!, outputName, true);
                _converter.GetType().GetProperty("OutputPixelFormat")?.SetValue(_converter, pixelType);
                var bytes = checked(width * height * channels);
                var mat = new Mat(height, width, channels == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3);
                try
                {
                    var pointerConvert = _converter.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => x.Name == "Convert" && !x.IsGenericMethod && x.GetParameters().Length == 3
                            && x.GetParameters()[0].ParameterType == typeof(IntPtr));
                    if (pointerConvert is not null)
                    {
                        pointerConvert.Invoke(_converter, [mat.Data, (long)bytes, result]);
                    }
                    else
                    {
                        var buffer = new byte[bytes];
                        var genericConvert = _converter.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(x => x.Name == "Convert" && x.IsGenericMethodDefinition)
                            .FirstOrDefault(x => x.GetParameters().Length == 2)
                            ?? throw new InvalidOperationException("Basler PixelDataConverter conversion API was not found.");
                        genericConvert.MakeGenericMethod(typeof(byte)).Invoke(_converter, [buffer, result]);
                        Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
                    }
                    var sequence = Interlocked.Increment(ref _sequence);
                    var rawTimestamp = Convert.ToInt64(result.GetType().GetProperty("Timestamp")?.GetValue(result) ?? 0L);
                    var tickHz = ReadLong("GevTimestampTickFrequency");
                    long? deviceTimestampNs = rawTimestamp > 0
                        ? tickHz is > 0 ? (long?)Math.Round(rawTimestamp * (1_000_000_000d / tickHz.Value))
                            : ReadBoolAlias("PtpEnable", "GevIEEE1588") == true ? rawTimestamp : null
                        : null;
                    _error = null;
                    RefreshTransportTelemetryCore();
                    return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, mat, channels == 1 ? "Mono8" : "Bgr8", deviceTimestampNs, _nativeFrameId);
                }
                catch
                {
                    mat.Dispose();
                    throw;
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException is TimeoutException)
            {
                RefreshTransportTelemetryCore(force: true);
                throw new CameraFrameTimeoutException($"Basler camera '{Id}' frame timeout after {timeout.TotalMilliseconds:0} ms.");
            }
            finally
            {
                if (result is IDisposable disposable) disposable.Dispose();
            }
        }
        finally { _gate.Release(); }
    }

    public CameraDeviceTelemetry GetTelemetry() => new(
        Interlocked.Read(ref _driverDrops), _nativePixelFormat, _nativeFrameId, _transportTelemetry);

    public async ValueTask<HardwareProvenanceData> GetHardwareProvenanceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var assembly = _assembly ?? BaslerSdkReflection.Load(_assemblyPath);
            var info = _cameraInfo ?? BaslerSdkReflection.Select(BaslerSdkReflection.Enumerate(assembly), _serial, _userName, _deviceKey);
            var firmware = _camera is null ? null : BaslerSdkReflection.TryGetParameterText(_camera, "DeviceFirmwareVersion");
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["adapter"] = Driver,
                ["transportLayer"] = BaslerSdkReflection.DictionaryValue(info, "TLType", "DeviceClass") ?? "unknown",
                ["sdkAssemblyVersion"] = assembly.GetName().Version?.ToString() ?? "unknown"
            };
            var ip = BaslerSdkReflection.DictionaryValue(info, "IpAddress", "DeviceIpAddress");
            if (!string.IsNullOrWhiteSpace(ip)) attrs["ipAddress"] = ip;
            return new HardwareProvenanceData(
                Manufacturer: BaslerSdkReflection.DictionaryValue(info, "VendorName", "ManufacturerInfo") ?? "Basler",
                ProductName: BaslerSdkReflection.DictionaryValue(info, "UserDefinedName", "FriendlyName"),
                Model: BaslerSdkReflection.DictionaryValue(info, "ModelName"),
                SerialNumber: BaslerSdkReflection.DictionaryValue(info, "SerialNumber"),
                HardwareRevision: BaslerSdkReflection.DictionaryValue(info, "DeviceVersion"),
                FirmwareVersion: firmware,
                SoftwareVersion: assembly.GetName().Version?.ToString(),
                Attributes: attrs);
        }
        finally { _gate.Release(); }
    }

    private void ApplySettingsCore(CameraSettings settings)
    {
        if (_camera is null) return;
        BaslerSdkReflection.TrySetParameter(_camera, "ExposureTime", settings.ExposureUs);
        BaslerSdkReflection.TrySetParameter(_camera, "Gain", settings.GainDb);
        BaslerSdkReflection.TrySetParameter(_camera, "AcquisitionFrameRateEnable", true);
        BaslerSdkReflection.TrySetParameter(_camera, "AcquisitionFrameRate", settings.TargetFps);
        BaslerSdkReflection.TrySetParameter(_camera, "AcquisitionMode", "Continuous");
        if (!BaslerSdkReflection.TrySetParameter(_camera, "TriggerSelector", "FrameStart"))
            throw new InvalidOperationException("Basler camera does not expose TriggerSelector=FrameStart.");
        if (settings.TriggerMode == CameraTriggerMode.Continuous)
        {
            if (!BaslerSdkReflection.TrySetParameter(_camera, "TriggerMode", "Off"))
                throw new InvalidOperationException("Basler camera could not disable TriggerMode.");
        }
        else
        {
            if (!BaslerSdkReflection.TrySetParameter(_camera, "TriggerMode", "On"))
                throw new InvalidOperationException("Basler camera could not enable TriggerMode.");
            var triggerSource = settings.TriggerMode == CameraTriggerMode.Software ? "Software" : settings.ExternalTriggerSource;
            if (!BaslerSdkReflection.TrySetParameter(_camera, "TriggerSource", triggerSource))
                throw new InvalidOperationException($"Basler camera does not support TriggerSource '{triggerSource}'.");
        }
    }

    private void StartGrabber()
    {
        if (_streamGrabber is null || _assembly is null) throw new InvalidOperationException("Basler StreamGrabber is not initialized.");
        var start = _streamGrabber.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "Start" && x.GetParameters().Length == 2);
        if (start is not null)
        {
            var grabStrategy = Enum.Parse(_assembly.GetType("Basler.Pylon.GrabStrategy", true)!, "LatestImages", true);
            var grabLoop = Enum.Parse(_assembly.GetType("Basler.Pylon.GrabLoop", true)!, "ProvidedByUser", true);
            start.Invoke(_streamGrabber, [grabStrategy, grabLoop]);
            return;
        }
        _streamGrabber.GetType().GetMethod("Start", Type.EmptyTypes)?.Invoke(_streamGrabber, null);
    }

    private void StopCore()
    {
        if (_streamGrabber is not null && _state == CameraState.Streaming)
        {
            try { _streamGrabber.GetType().GetMethod("Stop", Type.EmptyTypes)?.Invoke(_streamGrabber, null); } catch { }
        }
        if (_state != CameraState.Closed && _state != CameraState.Faulted) _state = CameraState.Open;
    }

    private void ReleaseSdkObjects()
    {
        if (_converter is IDisposable converter) { try { converter.Dispose(); } catch { } }
        if (_camera is IDisposable camera) { try { camera.Dispose(); } catch { } }
        _converter = null; _streamGrabber = null; _camera = null; _cameraInfo = null; _assembly = null;
    }

    private static CameraOutputPixelFormat ResolveOutputFormat(CameraOutputPixelFormat requested, string? native) => requested switch
    {
        CameraOutputPixelFormat.Mono8 => CameraOutputPixelFormat.Mono8,
        CameraOutputPixelFormat.Bgr8 => CameraOutputPixelFormat.Bgr8,
        _ => native?.Contains("Mono", StringComparison.OrdinalIgnoreCase) == true ? CameraOutputPixelFormat.Mono8 : CameraOutputPixelFormat.Bgr8
    };

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(); } catch { }
        _gate.Dispose();
    }
}
