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
                                Debug.WriteLine("encoder read: " + regs[0]);
                                if (regs != null && regs.Length > 0)
                                {
                                    lock (_sync)
                                    {
                                        _currentValue = regs[0];
                                    }
                                    try { ValueUpdated?.Invoke(_currentValue); } catch { }
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
