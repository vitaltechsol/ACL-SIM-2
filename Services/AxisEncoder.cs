using System;
using System.Threading;
using System.Threading.Tasks;
using EasyModbus;
using System.Windows;
using System.Diagnostics;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Reads encoder value from a ModbusClient in a background loop.
    /// The client instance is provided by the caller (seperate connection per axis).
    /// Loop runs until disposed or application exits.
    /// </summary>
    public class AxisEncoder : IDisposable
    {
        private readonly ModbusClient _mbc;
        private readonly int _encoderAddress = 387;
        private readonly int _pollIntervalMs;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _currentValue;
        private readonly object _sync = new object();
        private Task? _loopTask;
        private bool _wasConnected;
        private readonly string _name;

        // Encoder rollover tracking
        private const int ENCODER_MAX = 10000; // Maximum encoder value before rollover
        private int _loopCount = 0; // Number of times encoder has rolled over
        private int _previousRawValue = 0; // Previous raw encoder reading

        /// <summary>
        /// Raised when a new encoder value is read (may be raised on background thread).
        /// </summary>
        public event Action<int>? ValueUpdated;

        /// <summary>
        /// Raised when connection state changes. Argument is true when connected.
        /// </summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>
        /// Raised when an error occurs (connection failure, read error). Argument is error message.
        /// </summary>
        public event Action<string>? ErrorOccurred;

        public AxisEncoder(ModbusClient modbusClient, string name, int encoderAddress = 387, int pollIntervalMs = 200)
        {
            _mbc = modbusClient ?? throw new ArgumentNullException(nameof(modbusClient));
            _name = name ?? "Unknown";
            _encoderAddress = encoderAddress;
            _pollIntervalMs = Math.Max(10, pollIntervalMs);

            // start the read loop
            _loopTask = Task.Run(() => ReadLoopAsync(_cts.Token));

            // ensure loop is cancelled on application exit
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Exit += OnAppExit;
                }
            }
            catch
            {
                // ignore when Application not available (unit tests etc.)
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_mbc != null)
                    {
                        if (!_mbc.Connected)
                        {
                            try 
                            { 
                                _mbc.Connect(); 
                            } 
                            catch (Exception ex)
                            {
                                try { ErrorOccurred?.Invoke($"[{_name}] Connection failed: {ex.Message}"); } catch { }
                            }
                        }

                        // connection state changed?
                        var nowConnected = _mbc.Connected;
                        if (nowConnected != _wasConnected)
                        {
                            _wasConnected = nowConnected;
                            try { ConnectionChanged?.Invoke(nowConnected); } catch { }
                            if (!nowConnected)
                            {
                                try { ErrorOccurred?.Invoke($"[{_name}] Connection lost"); } catch { }
                            }
                        }

                        if (nowConnected)
                        {
                            try
                            {
                                var regs = _mbc.ReadHoldingRegisters(_encoderAddress, 1);
                                if (regs != null && regs.Length > 0)
                                {
                                    var rawValue = regs[0];

                                    // Detect rollover by looking at the difference between readings
                                    // If difference is very large (more than half the range), we crossed the boundary
                                    var delta = rawValue - _previousRawValue;
                                    const int HALF_RANGE = ENCODER_MAX / 2;

                                    if (delta > HALF_RANGE)
                                    {
                                        // Large positive jump: we wrapped backward (0 → 9900)
                                        // Example: prev=100, curr=9900, delta=9800 (>5000) → going backward, decrement
                                        _loopCount--;
                                        Debug.WriteLine($"[{_name}] Backward wrap detected (prev:{_previousRawValue} -> curr:{rawValue}, delta:{delta}). Loop count: {_loopCount}");
                                        try { ErrorOccurred?.Invoke($"[{_name}] Backward wrap. Loop count: {_loopCount}"); } catch { }
                                    }
                                    else if (delta < -HALF_RANGE)
                                    {
                                        // Large negative jump: we wrapped forward (9900 → 0)
                                        // Example: prev=9900, curr=100, delta=-9800 (<-5000) → going forward, increment
                                        _loopCount++;
                                        Debug.WriteLine($"[{_name}] Forward wrap detected (prev:{_previousRawValue} -> curr:{rawValue}, delta:{delta}). Loop count: {_loopCount}");
                                        try { ErrorOccurred?.Invoke($"[{_name}] Forward wrap. Loop count: {_loopCount}"); } catch { }
                                    }

                                    _previousRawValue = rawValue;

                                    // Calculate absolute position including loops (supports negative loop counts)
                                    var absolutePosition = (_loopCount * ENCODER_MAX) + rawValue;

                                    lock (_sync)
                                    {
                                        _currentValue = absolutePosition;
                                    }

                                  //  Debug.WriteLine($"[{_name}] Raw: {rawValue}, Loops: {_loopCount}, Absolute: {absolutePosition}");
                                    try { ValueUpdated?.Invoke(absolutePosition); } catch { }
                                }
                            }
                            catch (Exception ex)
                            {
                                try { ErrorOccurred?.Invoke($"[{_name}] Read error: {ex.Message}"); } catch { }
                            }
                        }
                    }
                }
                catch
                {
                    // swallow top-level loop exceptions
                }

                try
                {
                    await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void OnAppExit(object? sender, ExitEventArgs e)
        {
            Dispose();
        }

        /// <summary>
        /// Gets the most recently read encoder value.
        /// </summary>
        public int CurrentValue
        {
            get
            {
                lock (_sync) { return _currentValue; }
            }
        }

        /// <summary>
        /// Async-friendly getter that returns the latest value.
        /// </summary>
        public Task<int> GetValueAsync() => Task.FromResult(CurrentValue);

        /// <summary>
        /// Returns whether the underlying Modbus client is currently connected.
        /// </summary>
        public bool IsConnected => _mbc != null && _mbc.Connected;

        /// <summary>
        /// Gets the current loop/rollover count.
        /// </summary>
        public int LoopCount
        {
            get { lock (_sync) { return _loopCount; } }
        }

        /// <summary>
        /// Gets the last raw encoder value (before loop calculation).
        /// </summary>
        public int RawValue
        {
            get { lock (_sync) { return _previousRawValue; } }
        }

        /// <summary>
        /// Resets the loop count to zero. Use this to re-calibrate when setting center position.
        /// </summary>
        public void ResetLoopCount()
        {
            lock (_sync)
            {
                _loopCount = 0;
                _currentValue = _previousRawValue; // Update current value to raw value only
            }
            try { ErrorOccurred?.Invoke($"[{_name}] Loop count reset to 0"); } catch { }
        }

        public void Dispose()
        {
            try
            {
                if (Application.Current != null)
                    Application.Current.Exit -= OnAppExit;
            }
            catch { }

            try
            {
                _cts.Cancel();
            }
            catch { }

            try
            {
                _loopTask?.Wait(500);
            }
            catch { }

            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
