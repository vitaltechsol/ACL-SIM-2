using System.ComponentModel;
using ACL_SIM_2.Models;
using ACL_SIM_2.Services;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2.Tests;

/// <summary>
/// Unit tests for <see cref="AxisManager"/>.
/// All tests use null Modbus / ProSim dependencies so no hardware is required.
/// The <see cref="AxisTorqueControl"/> and movement commands are silently skipped
/// when the ModbusClient is null, so state-oriented behavior can still be verified.
/// </summary>
public class AxisManagerTests : IDisposable
{
    private readonly AxisViewModel _vm;
    private readonly AxisManager _manager;

    public AxisManagerTests()
    {
        var settings = new AxisSettings
        {
            FullLeftPosition = -2000,
            FullRightPosition = 2000,
            SelfCenteringSpeed = 50,
            Dampening = 10,
            RS485Ip = "" // empty so no torque control is created
        };
        var axis = new Axis("Pitch", settings);
        _vm = new AxisViewModel(axis);
        _manager = new AxisManager("Pitch", _vm, modbusClient: null, modbusLock: null,
                                   proSimManager: null!, logger: null!);
    }

    public void Dispose()
    {
        try { _manager.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------
    // Constructor argument validation
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NullName_ThrowsArgumentNullException()
    {
        var axis = new Axis("Roll", new AxisSettings());
        var vm = new AxisViewModel(axis);
        Assert.Throws<ArgumentNullException>(() =>
            new AxisManager(null!, vm, null, null, null!, null!));
    }

    [Fact]
    public void Constructor_NullAxisViewModel_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AxisManager("Pitch", null!, null, null, null!, null!));
    }

    // ------------------------------------------------------------------
    // UpdateEncoderPosition
    // ------------------------------------------------------------------

    [Fact]
    public void UpdateEncoderPosition_SetsEncoderPositionOnViewModel()
    {
        _manager.UpdateEncoderPosition(1500.0);
        Assert.Equal(1500.0, _vm.EncoderPosition);
    }

    [Fact]
    public void UpdateEncoderPosition_NegativeValue_Accepted()
    {
        _manager.UpdateEncoderPosition(-800.0);
        Assert.Equal(-800.0, _vm.EncoderPosition);
    }

    [Fact]
    public void UpdateEncoderPosition_Zero_Accepted()
    {
        _manager.UpdateEncoderPosition(42.0);
        _manager.UpdateEncoderPosition(0.0);
        Assert.Equal(0.0, _vm.EncoderPosition);
    }

    // ------------------------------------------------------------------
    // SetSimPaused
    // ------------------------------------------------------------------

    [Fact]
    public void SetSimPaused_TrueAndFalse_DoesNotThrow()
    {
        // With null ModbusClient the Stop() inside SetSimPaused is a no-op.
        _manager.SetSimPaused(true);
        _manager.SetSimPaused(false);
    }

    // ------------------------------------------------------------------
    // PrepareCentering
    // ------------------------------------------------------------------

    [Fact]
    public void PrepareCentering_DoesNotThrow()
    {
        // SendCenteringSpeed is a no-op when torqueControl is null; just verify no crash.
        _manager.PrepareCentering();
    }

    // ------------------------------------------------------------------
    // SendCenteringSpeed / SendDampening (no-ops without torque control)
    // ------------------------------------------------------------------

    [Fact]
    public void SendCenteringSpeed_WithoutTorqueControl_DoesNotThrow()
    {
        _manager.SendCenteringSpeed(100);
    }

    [Fact]
    public void SendDampening_WithoutTorqueControl_DoesNotThrow()
    {
        _manager.SendDampening(500);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_CanBeCalledTwice_DoesNotThrow()
    {
        var axis = new Axis("Rudder", new AxisSettings());
        var vm = new AxisViewModel(axis);
        var mgr = new AxisManager("Rudder", vm, null, null, null!, null!);
        mgr.Dispose();
        mgr.Dispose(); // second call must be safe
    }

    // ------------------------------------------------------------------
    // CenterToProSimPositionAsync — cancellation path
    // ------------------------------------------------------------------

    [Fact]
    public async Task CenterToProSimPositionAsync_ImmediateCancellation_CompletesWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var messages = new List<string>();
        // With null ModbusClient the method should exit very quickly (no movement possible).
        await _manager.CenterToProSimPositionAsync(
            getProSimValue: () => 512.0,
            log: m => messages.Add(m),
            cancellationToken: cts.Token
        );
    }
}
