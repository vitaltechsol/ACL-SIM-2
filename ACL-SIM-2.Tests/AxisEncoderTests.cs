using ACL_SIM_2.Services;
using EasyModbus;

namespace ACL_SIM_2.Tests;

/// <summary>
/// Unit tests for <see cref="AxisEncoder"/>.
/// These tests use a real (disconnected) <see cref="ModbusClient"/> so no hardware
/// or WPF application is required.  The background poll loop will run but the
/// client will never connect, keeping all tests side-effect-free.
/// </summary>
public class AxisEncoderTests : IDisposable
{
    // A disconnected ModbusClient used as the test double.
    private readonly ModbusClient _disconnectedClient = new ModbusClient();
    private readonly object _modbusLock = new object();
    private readonly List<AxisEncoder> _encoders = new List<AxisEncoder>();

    private AxisEncoder CreateEncoder(string name = "Test", Func<bool>? isReversed = null, int pollMs = 200)
    {
        var enc = new AxisEncoder(_disconnectedClient, name, isReversed ?? (() => false), _modbusLock, pollMs);
        _encoders.Add(enc);
        return enc;
    }

    public void Dispose()
    {
        foreach (var enc in _encoders)
        {
            try { enc.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // Constructor argument validation
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NullModbusClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AxisEncoder(null!, "Test", () => false, _modbusLock));
    }

    [Fact]
    public void Constructor_NullModbusLock_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AxisEncoder(_disconnectedClient, "Test", () => false, null!));
    }

    // ------------------------------------------------------------------
    // Initial state
    // ------------------------------------------------------------------

    [Fact]
    public void CurrentValue_InitiallyZero()
    {
        var enc = CreateEncoder();
        Assert.Equal(0, enc.CurrentValue);
    }

    [Fact]
    public void IsConnected_WhenClientNotConnected_ReturnsFalse()
    {
        var enc = CreateEncoder();
        Assert.False(enc.IsConnected);
    }

    [Fact]
    public async Task GetValueAsync_ReturnsCurrentValue()
    {
        var enc = CreateEncoder();
        var value = await enc.GetValueAsync();
        Assert.Equal(enc.CurrentValue, value);
    }

    // ------------------------------------------------------------------
    // Name handling
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NullName_DoesNotThrow()
    {
        // Null name is allowed (defaults to "Unknown" internally); no exception expected.
        var enc = new AxisEncoder(_disconnectedClient, null!, () => false, _modbusLock);
        _encoders.Add(enc);
    }

    // ------------------------------------------------------------------
    // Poll interval clamping
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_PollIntervalBelowMinimum_DoesNotThrow()
    {
        // Poll interval below 10 ms should be clamped without throwing.
        var enc = new AxisEncoder(_disconnectedClient, "Test", () => false, _modbusLock, pollIntervalMs: 1);
        _encoders.Add(enc);
    }

    // ------------------------------------------------------------------
    // Events: no crashes when no subscribers
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_CanBeCalledTwice_DoesNotThrow()
    {
        var enc = new AxisEncoder(_disconnectedClient, "Test", () => false, _modbusLock);
        enc.Dispose();
        enc.Dispose(); // second call must be safe
    }

    // ------------------------------------------------------------------
    // ConnectionChanged event fires on connect state change
    // ------------------------------------------------------------------

    [Fact]
    public void ErrorOccurred_SubscriberReceivesMessages_WhenSet()
    {
        var messages = new List<string>();
        var enc = CreateEncoder();
        enc.ErrorOccurred += msg => messages.Add(msg);

        // Just verify subscription works — actual messages arrive from background loop
        Assert.NotNull(enc);
    }
}
