namespace VisionStudio.Engine.Robot;

/// <summary>
/// ABB-like in-process simulator using the standard six-bit industrial handshake.
/// It does not implement RAPID, RWS, EGM, safety, collision checking or controller limits.
/// </summary>
public sealed class VirtualAbbRobotAdapter : SimulatedHandshakeRobotAdapter
{
    public VirtualAbbRobotAdapter() : base(
        id: "virtual-abb-1",
        name: "Virtual ABB 4-Axis",
        vendor: "ABB",
        model: "Virtual IRB 2D",
        driver: "virtual-abb",
        initialPose: new VisionCoordinatePose2D(520, 240, 28, "RobotBase", "mm"),
        scanDelayMs: 20,
        capabilities: new RobotCapabilities(MaxLinearSpeedMmPerSec: 1200, MaxAngularSpeedDegPerSec: 540))
    { }
}
