# Vendor Camera Adapter Guide

Real camera SDKs should live in separate projects such as:

```text
VisionStudio.Camera.Hikrobot
VisionStudio.Camera.Basler
```

Each adapter implements `ICameraDevice` and converts a vendor frame to `VisionFrame`.

## Required mapping

| VisionStudio | Vendor responsibility |
|---|---|
| OpenAsync | create/open SDK camera handle |
| CloseAsync | close/destroy SDK handle |
| StartAsync | start acquisition / stream |
| StopAsync | stop acquisition / stream |
| ApplySettingsAsync | exposure, gain, FPS and trigger mapping |
| ExecuteSoftwareTriggerAsync | execute the SDK software-trigger command |
| GrabAsync | wait for the next SDK frame and return VisionFrame |
| CameraCapabilities | declare supported settings/ranges and trigger behavior |

## Trigger behavior

### Continuous

Configure vendor free-run / continuous acquisition. `GrabAsync` should return each arriving frame.

### Software

Configure the camera for software trigger. `CameraAcquisitionWorker` waits for a host trigger request, calls `ExecuteSoftwareTriggerAsync`, then calls `GrabAsync`.

### External hardware

Set `CameraCapabilities.HostSimulatedExternalTrigger = false`. Configure the SDK line trigger and let `GrabAsync` block/wait for a hardware-triggered frame. If its wait timeout expires without a frame, throw `CameraFrameTimeoutException`; the worker treats this as an idle trigger wait rather than a disconnect/reconnect condition.

Virtual/File drivers set `HostSimulatedExternalTrigger = true`, allowing the UI Trigger button to simulate an external line during hardware-free testing.

## Buffer ownership

Do not expose a vendor SDK pointer after its buffer has been returned to the SDK. Either:

1. copy the SDK buffer into an owned OpenCV Mat before returning `VisionFrame`; or
2. add a vendor-specific lease object that keeps the SDK buffer locked until the frame is released.

Option 2 is the future zero-copy path, but it must obey the `VisionFrame -> CameraFrameHub -> CameraFrameLease -> VisionImage` lifetime boundary.
