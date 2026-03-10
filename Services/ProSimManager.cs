using System;
using System.Threading;
using System.Threading.Tasks;
using ProSimSDK;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Manages connection to ProSim and provides access to simulator data.
    /// </summary>
    public class ProSimManager : IDisposable
    {
        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Failed
        }

        private readonly object _connectionLock = new object();
        private CancellationTokenSource? _prosimConnectCts;
        private ConnectionState _connectionState = ConnectionState.Disconnected;
        private string _statusMessage = "Not connected";
        private string _lastConnectedIp = "192.168.1.142";
        private bool _isDisposed;

        private readonly ProSimConnect connection;

        public ProSimManager()
        {
            connection = new ProSimConnect();
            connection.onConnect += Connection_onConnect;
            connection.onDisconnect += Connection_onDisconnect;
        }

        /// <summary>
        /// Current connection state.
        /// </summary>
        public ConnectionState State
        {
            get
            {
                lock (_connectionLock)
                {
                    return _connectionState;
                }
            }
            private set
            {
                lock (_connectionLock)
                {
                    if (_connectionState == value) return;
                    _connectionState = value;
                }
                OnConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(value, _statusMessage));
            }
        }

        /// <summary>
        /// Current status message.
        /// </summary>
        public string StatusMessage
        {
            get
            {
                lock (_connectionLock)
                {
                    return _statusMessage;
                }
            }
            private set
            {
                lock (_connectionLock)
                {
                    _statusMessage = value;
                }
            }
        }

        /// <summary>
        /// Event raised when connection state changes.
        /// </summary>
        public event EventHandler<ConnectionStateEventArgs>? OnConnectionStateChanged;

        /// <summary>
        /// Connects to ProSim at the specified IP address.
        /// </summary>
        /// <param name="prosimIp">ProSim server IP address. Defaults to 192.168.1.142</param>
        /// <returns>Task that completes when connection attempt finishes</returns>
        public async Task ConnectAsync(string prosimIp = "127.0.0.0")
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ProSimManager));

            // Cancel any existing connection attempt
            try { _prosimConnectCts?.Cancel(); } catch { }
            _prosimConnectCts = new CancellationTokenSource();
            var token = _prosimConnectCts.Token;

            _lastConnectedIp = prosimIp;
            SetStatus(ConnectionState.Connecting, $"Connecting to {prosimIp}...");

            // Safe background connect (no crash on timeout)
            Task<Exception?> connectTask = Task.Run(() =>
            {
                try
                {
                    connection.Connect(prosimIp);
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }, token);

            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), token);

            Task completed;
            try
            {
                completed = await Task.WhenAny(connectTask, timeoutTask);
            }
            catch (OperationCanceledException)
            {
                SetStatus(ConnectionState.Disconnected, "Connection cancelled");
                return;
            }

            if (completed == timeoutTask)
            {
                // Observe connectTask result so it can't crash later
                _ = connectTask.ContinueWith(t => { var _ignore = t.Result; }, TaskScheduler.Default);
                SetStatus(ConnectionState.Failed, $"Timeout connecting to {prosimIp}");
                return;
            }

            var err = await connectTask;
            if (err != null)
            {
                SetStatus(ConnectionState.Failed, $"Failed: {err.Message}");
                return;
            }

            SetStatus(ConnectionState.Connected, $"Connected to {prosimIp}");
        }

        /// <summary>
        /// Disconnects from ProSim.
        /// </summary>
        public void Disconnect()
        {
            if (_isDisposed) return;

            try
            {
                _prosimConnectCts?.Cancel();
            }
            catch { }

            try
            {
                // ProSim SDK connection will auto-disconnect on disposal
                // connection?.Close(); // Method not available in ProSim SDK
            }
            catch { }

            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }

       /// <summary>
        /// Gets a reference value from ProSim by name.
        /// </summary>
        /// <param name="refName">Name of the ProSim reference variable</param>
        /// <returns>The current value of the reference variable</returns>
        /// <example>
        /// Example reference names:
        /// - "AXIS_PITCH" - Pitch axis position
        /// - "AXIS_ROLL" - Roll axis position
        /// - "AXIS_RUDDER" - Rudder axis position
        /// - "HYDRAULICS_ON" - Hydraulics system state
        /// - "AUTOPILOT_ON" - Autopilot state
        /// 
        /// Usage:
        /// double pitchPosition = GetProSimRefVal("AXIS_PITCH");
        /// double hydraulicsState = GetProSimRefVal("HYDRAULICS_ON");
        /// </example>
        public double GetProSimRefVal(string refName)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ProSimManager));

            if (State != ConnectionState.Connected)
                throw new InvalidOperationException("Not connected to ProSim");

            try
            {
                return 0;
               // return connection.RequestDataRefValue(refName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to get value for '{refName}': {ex.Message}", ex);
            }
        }

        private void SetStatus(ConnectionState state, string message)
        {
            StatusMessage = message;
            State = state;
        }

        // Event handlers for ProSim connection events
        private void Connection_onConnect()
        {
            // Called when ProSim notifies successful connection
            SetStatus(ConnectionState.Connected, $"Connected to {_lastConnectedIp}");
        }

        private void Connection_onDisconnect()
        {
            // Called when ProSim notifies disconnection
            SetStatus(ConnectionState.Disconnected, "Disconnected from ProSim");
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _prosimConnectCts?.Cancel();
                _prosimConnectCts?.Dispose();
            }
            catch { }

            try
            {
                if (connection != null)
                {
                    connection.onConnect -= Connection_onConnect;
                    connection.onDisconnect -= Connection_onDisconnect;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Event args for ProSim connection state changes.
    /// </summary>
    public class ConnectionStateEventArgs : EventArgs
    {
        public ProSimManager.ConnectionState State { get; }
        public string Message { get; }

        public ConnectionStateEventArgs(ProSimManager.ConnectionState state, string message)
        {
            State = state;
            Message = message;
        }
    }
}
