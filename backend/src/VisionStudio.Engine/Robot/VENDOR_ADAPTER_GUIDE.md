# Vendor Robot Adapter Guide — V0.19

Implement `IRobot2DAdapter` for real hardware. Keep vendor SDK/protocol objects inside the adapter project.

## Required mapping

Expose a `RobotDescriptor` with:

- connection state
- current `CoordinatePose2D`
- active target / command id
- standard `RobotHandshakeSignals`
- error text/code

Map the standard handshake:

```text
TargetReady / Execute / Busy / Complete / Error / Ack
```

Do **not** implement generic timeout/retry loops inside the adapter. `RobotManager.ExecuteHandshakeAsync` owns command policy so all adapters behave consistently.

## Recommended projects

```text
VisionStudio.Robot.Abstractions   (contracts only)
VisionStudio.Robot.AbbTcp         (future)
VisionStudio.Robot.AbbRws         (future)
VisionStudio.Robot.PlcBridge      (future)
```

For PLC bridges, map signals to tags/registers and validate a sequence/CommandId to avoid accepting stale `Complete` bits.

For a real robot, motion safety, interlocks, safe zones, limits and controller authorization remain outside VisionStudio's generic handshake layer.
