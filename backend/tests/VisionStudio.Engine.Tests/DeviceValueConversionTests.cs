using VisionStudio.Engine.Device;

namespace VisionStudio.Engine.Tests;

public sealed class DeviceValueConversionTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(0, false)]
    [InlineData(5, true)]
    public void ConvertBoolean_IsDeterministic(object input, bool expected)
    {
        var actual = DeviceValueConversion.ConvertTo(DeviceTagDataType.Boolean, input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ModbusAddress_ParsesCanonicalAreas()
    {
        Assert.Equal(new ModbusAddress(ModbusArea.Coil, 7), ModbusAddress.Parse("C:7"));
        Assert.Equal(new ModbusAddress(ModbusArea.DiscreteInput, 8), ModbusAddress.Parse("DI:8"));
        Assert.Equal(new ModbusAddress(ModbusArea.HoldingRegister, 9), ModbusAddress.Parse("HR:9"));
        Assert.Equal(new ModbusAddress(ModbusArea.InputRegister, 10), ModbusAddress.Parse("IR:10"));
    }

    [Fact]
    public void S7Driver_RejectsUnknownCpuBeforeNetworkAccess()
    {
        Assert.Throws<DeviceAddressException>(() => new S7NetPlusDeviceDriver(new S7NetPlusDeviceOptions(
            "bad", "bad", "127.0.0.1", CpuType: "DefinitelyNotAPlc")));
    }
}
