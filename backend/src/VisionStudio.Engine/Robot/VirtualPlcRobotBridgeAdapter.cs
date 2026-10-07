namespace VisionStudio.Engine.Robot;

/// <summary>
/// PLC-style bridge simulator with a slower scan cycle. It exercises TargetReady/Execute/Busy/Complete/Error/Ack
/// exactly like a register/bit handshake without depending on a specific PLC protocol.
/// </summary>
public sealed class VirtualPlcRobotBridgeAdapter : SimulatedHandshakeRobotAdapter
{
    public VirtualPlcRobotBridgeAdapter() : base(
        id: "virtual-plc-1",
        name: "Virtual PLC Robot Bridge",
        vendor: "PLC Bridge",
        model: "6-bit Handshake",
        driver: "virtual-plc-bridge",
        initialPose: new VisionCoordinatePose2D(500, 220, 20, "RobotBase", "mm"),
        scanDelayMs: 50,
        capabilities: new RobotCapabilities(MaxLinearSpeedMmPerSec: 800, MaxAngularSpeedDegPerSec: 360))
    { }
}
