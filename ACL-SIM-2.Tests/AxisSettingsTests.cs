using ACL_SIM_2.Models;

namespace ACL_SIM_2.Tests;

public class AxisSettingsTests
{
    // ------------------------------------------------------------------
    // ConvertCenteringSpeedToActual
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertCenteringSpeedToActual_Zero_ReturnsZero()
    {
        Assert.Equal(0, AxisSettings.ConvertCenteringSpeedToActual(0));
    }

    [Fact]
    public void ConvertCenteringSpeedToActual_100_ReturnsMax()
    {
        Assert.Equal((int)AxisSettings.CenteringSpeedActualMax,
            AxisSettings.ConvertCenteringSpeedToActual(100));
    }

    [Fact]
    public void ConvertCenteringSpeedToActual_50_ReturnsMidpoint()
    {
        var expected = (int)Math.Round(0.5 * AxisSettings.CenteringSpeedActualMax);
        Assert.Equal(expected, AxisSettings.ConvertCenteringSpeedToActual(50));
    }

    [Fact]
    public void ConvertCenteringSpeedToActual_NegativeInput_ClampsToZero()
    {
        Assert.Equal(0, AxisSettings.ConvertCenteringSpeedToActual(-10));
    }

    [Fact]
    public void ConvertCenteringSpeedToActual_Over100_ClampsToMax()
    {
        Assert.Equal((int)AxisSettings.CenteringSpeedActualMax,
            AxisSettings.ConvertCenteringSpeedToActual(200));
    }

    // ------------------------------------------------------------------
    // ConvertDampeningToActual
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertDampeningToActual_Zero_ReturnsMin()
    {
        Assert.Equal((int)Math.Round(AxisSettings.DampeningActualMin),
            AxisSettings.ConvertDampeningToActual(0));
    }

    [Fact]
    public void ConvertDampeningToActual_100_ReturnsMax()
    {
        Assert.Equal((int)AxisSettings.DampeningActualMax,
            AxisSettings.ConvertDampeningToActual(100));
    }

    [Fact]
    public void ConvertDampeningToActual_50_ReturnsMidpoint()
    {
        var expected = (int)Math.Round(AxisSettings.DampeningActualMin +
                       0.5 * (AxisSettings.DampeningActualMax - AxisSettings.DampeningActualMin));
        Assert.Equal(expected, AxisSettings.ConvertDampeningToActual(50));
    }

    [Fact]
    public void ConvertDampeningToActual_NegativeInput_ClampsToMin()
    {
        Assert.Equal((int)Math.Round(AxisSettings.DampeningActualMin),
            AxisSettings.ConvertDampeningToActual(-50));
    }

    [Fact]
    public void ConvertDampeningToActual_Over100_ClampsToMax()
    {
        Assert.Equal((int)AxisSettings.DampeningActualMax,
            AxisSettings.ConvertDampeningToActual(999));
    }

    // ------------------------------------------------------------------
    // ConvertTorqueDisplayToActual (instance method)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertTorqueDisplayToActual_Zero_ReturnsZero()
    {
        var s = new AxisSettings();
        Assert.Equal(0.0, s.ConvertTorqueDisplayToActual(0));
    }

    [Fact]
    public void ConvertTorqueDisplayToActual_100_ReturnsMax()
    {
        var s = new AxisSettings();
        Assert.Equal(AxisSettings.TorqueActualMax, s.ConvertTorqueDisplayToActual(100));
    }

    [Fact]
    public void ConvertTorqueDisplayToActual_50_ReturnsMidpoint()
    {
        var s = new AxisSettings();
        Assert.Equal(AxisSettings.TorqueActualMax * 0.5, s.ConvertTorqueDisplayToActual(50));
    }

    [Fact]
    public void ConvertTorqueDisplayToActual_NegativeInput_ClampsToZero()
    {
        var s = new AxisSettings();
        Assert.Equal(0.0, s.ConvertTorqueDisplayToActual(-20));
    }

    [Fact]
    public void ConvertTorqueDisplayToActual_Over100_ClampsToMax()
    {
        var s = new AxisSettings();
        Assert.Equal(AxisSettings.TorqueActualMax, s.ConvertTorqueDisplayToActual(150));
    }

    // ------------------------------------------------------------------
    // Clone / CopyFrom
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_ProducesEqualSettings()
    {
        var original = new AxisSettings
        {
            FullLeftPosition = -3000,
            FullRightPosition = 3000,
            MinTorquePercent = 10,
            MaxTorquePercent = 60,
            SelfCenteringSpeed = 75,
            Dampening = 25,
            MotorSpeedRpm = 5,
            RS485Ip = "192.168.1.1",
            DriverId = 2
        };

        var clone = original.Clone();

        Assert.Equal(original.FullLeftPosition, clone.FullLeftPosition);
        Assert.Equal(original.FullRightPosition, clone.FullRightPosition);
        Assert.Equal(original.MinTorquePercent, clone.MinTorquePercent);
        Assert.Equal(original.MaxTorquePercent, clone.MaxTorquePercent);
        Assert.Equal(original.SelfCenteringSpeed, clone.SelfCenteringSpeed);
        Assert.Equal(original.Dampening, clone.Dampening);
        Assert.Equal(original.MotorSpeedRpm, clone.MotorSpeedRpm);
        Assert.Equal(original.RS485Ip, clone.RS485Ip);
        Assert.Equal(original.DriverId, clone.DriverId);
    }

    [Fact]
    public void Clone_IsIndependent_ChangingCloneDoesNotAffectOriginal()
    {
        var original = new AxisSettings { MinTorquePercent = 5 };
        var clone = original.Clone();

        clone.MinTorquePercent = 99;

        Assert.Equal(5, original.MinTorquePercent);
    }

    [Fact]
    public void CopyFrom_NullSource_ThrowsArgumentNullException()
    {
        var s = new AxisSettings();
        Assert.Throws<ArgumentNullException>(() => s.CopyFrom(null!));
    }

    // ------------------------------------------------------------------
    // Default values
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultSettings_FullLeftPositionIsNegative()
    {
        var s = new AxisSettings();
        Assert.True(s.FullLeftPosition < 0);
    }

    [Fact]
    public void DefaultSettings_FullRightPositionIsPositive()
    {
        var s = new AxisSettings();
        Assert.True(s.FullRightPosition > 0);
    }

    [Fact]
    public void DefaultSettings_CenterPositionIsZero()
    {
        var s = new AxisSettings();
        Assert.Equal(0.0, s.CenterPosition);
    }
}
