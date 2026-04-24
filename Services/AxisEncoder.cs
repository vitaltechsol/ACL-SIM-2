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
        private readonly int _pollIntervalMs;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _currentValue;
        private readonly object _sync = new object();
        private readonly object _modbusLock; // Shared lock for thread-safe Modbus access
        private Task? _loopTask;
        private bool _wasConnected;
        private readonly string _name;
        private readonly Func<bool> _isReversedFunc; // Function to check if motor is reversed

        /// <summary>
        /// Raised when a new encoder value is read (may be raised on background thread).
        /// </summary>
        public event Action<int>? ValueUpdated;


        /// <summary>
        /// Raised when a new encoder value is read (may be raised on background thread).
        /// </summary>
        public event Action<int>? CommandValueUpdated;


        /// <summary>
        /// Raised when connection state changes. Argument is true when connected.
        /// </summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>
        /// Raised when an error occurs (connection failure, read error). Argument is error message.
        /// </summary>
        public event Action<string>? ErrorOccurred;

        /// <summary>
        /// Raised once when the first Modbus register read succeeds, confirming the motor driver
        /// is responding on the RS485 bus (distinct from TCP connection to the adapter).
        /// </summary>
        public event Action? FirstReadSucceeded;

        public AxisEncoder(ModbusClient modbusClient, string name, Func<bool> isReversedFunc, object modbusLock, int pollIntervalMs = 200)
        {
            _mbc = modbusClient ?? throw new ArgumentNullException(nameof(modbusClient));
            _name = name ?? "Unknown";
            _isReversedFunc = isReversedFunc ?? (() => false);
            _modbusLock = modbusLock ?? throw new ArgumentNullException(nameof(modbusLock));
            _pollIntervalMs = Math.Max(10, pollIntervalMs);

            // start the read loop and a one-shot initial probe
            _loopTask = Task.Run(() => ReadLoopAsync(_cts.Token));
            Task.Run(() => InitialProbeAsync(_cts.Token));

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
                        lock (_modbusLock)
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
                                    var absolutePosition = ReadFeedbackPosition();

                                    if (_isReversedFunc())
                                        absolutePosition = -absolutePosition;

                                    lock (_sync)
                                    {
                                        _currentValue = absolutePosition;
                                    }

                                    try { ValueUpdated?.Invoke(absolutePosition); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    try { ErrorOccurred?.Invoke($"[{_name}] Read error: {ex.Message.Split('.')[0]}"); } catch { }
                                }
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

        /// <summary>
        /// One-shot task: polls until the first successful Modbus read, fires
        /// <see cref="FirstReadSucceeded"/> once, then exits.
        /// </summary>
        private async Task InitialProbeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool success = false;
                lock (_modbusLock)
                {
                    if (_mbc.Connected)
                    {
                        try { ReadFeedbackPosition(); success = true; } catch { }
                    }
                }

                if (success)
                {
                    try { FirstReadSucceeded?.Invoke(); } catch { }
                    return;
                }

                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        // -----------------------------
        // Read monitored positions
        // Dn009/Dn010 = command accumulated value
        // Dn011/Dn012 = feedback accumulated value
        // -----------------------------

        private int ReadBase10000Value(int lowAddress)
        {
            int[] regs = _mbc.ReadHoldingRegisters(lowAddress, 2);

            int low = regs[0];
            int high = regs[1];

            return (high * 10000) + low;
        }

        //public int ReadCommandPosition()
        //{
        //    // Dn009 = 377, Dn010 = 378
        //    // return ReadBase10000Value(377);
        //    return ReadBase10000Value(379);
        //}

        public int ReadFeedbackPosition()
        {
            // Dn011 = 379, Dn012 = 380
            return ReadBase10000Value(379);
        }
        
        // Dn013 = 371 (single register) = position deviation (command - feedback)
        public int ReadPositionDeviation()
        {
            return unchecked((short)_mbc.ReadHoldingRegisters(371, 1)[0]);
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
