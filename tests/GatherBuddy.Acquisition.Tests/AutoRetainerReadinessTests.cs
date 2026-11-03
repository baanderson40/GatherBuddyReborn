using GatherBuddy.Automation;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AutoRetainerReadinessTests
{
    [Theory]
    [InlineData(AutoRetainerProcessingMode.Ventures, true, false)]
    [InlineData(AutoRetainerProcessingMode.Deployables, false, true)]
    [InlineData(AutoRetainerProcessingMode.Both, true, true)]
    public void ModesSelectCorrectArReadiness(AutoRetainerProcessingMode mode, bool ventures, bool deployables)
    {
        Assert.Equal(ventures, AutoRetainerReadiness.VenturesReady(mode, true, false, 300, 300));
        Assert.Equal(deployables, AutoRetainerReadiness.DeployablesReady(mode, true, false, true));
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, true)]
    [InlineData(299L, true)]
    [InlineData(300L, true)]
    [InlineData(301L, false)]
    [InlineData(null, false)]
    public void VentureThresholdAndUnknown(long? seconds, bool expected)
        => Assert.Equal(expected, AutoRetainerReadiness.VenturesReady(AutoRetainerProcessingMode.Both, true, false, seconds, 300));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, null)]
    public void DisabledExcludedUnknownAreNotReady(bool enabled, bool excluded, bool? ready)
    {
        Assert.False(AutoRetainerReadiness.DeployablesReady(AutoRetainerProcessingMode.Both, enabled, excluded, ready));
        if (!enabled || excluded) Assert.False(AutoRetainerReadiness.VenturesReady(AutoRetainerProcessingMode.Both, enabled, excluded, 0, 300));
    }

    [Fact]
    public void EnumContractPreservesArWireValuesAndMigrationDefault()
    {
        Assert.Equal(0, (int)default(AutoRetainerProcessingMode));
        Assert.Equal(1, (int)AutoRetainerProcessingMode.Deployables);
        Assert.Equal(2, (int)AutoRetainerProcessingMode.Both);
    }
}
