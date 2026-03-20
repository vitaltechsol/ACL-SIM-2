using System;
using System.Collections.Generic;
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
        // DataRef Constants
        public const string AILERON_LEFT = "aircraft.flightControls.leftAileron";
        public const string AILERON_RIGHT = "aircraft.flightControls.rightAileron";
        public const string ELEVATOR = "aircraft.flightControls.elevator";
        public const string TRIM_ELEVATOR = "aircraft.flightControls.trim.elevator";
        public const string TRIM_AILERON = "aircraft.flightControls.trim.aileron.units";
        public const string TRIM_RUDDER = "aircraft.flightControls.trim.rudder.units";
        public const string IS_STALLING = "system.gates.B_STICKSHAKER";
        public const string SPEED_IAS = "aircraft.speed.ias";
        public const string AILERON_CPTN = "system.analog.A_FC_AILERON_CAPT";
        public const string ELEVATOR_CPTN = "system.analog.A_FC_ELEVATOR_CAPT";
        public const string RUDDER_CAPT = "system.analog.A_FC_RUDDER_CAPT";
        public const string TILLER_CAPT = "system.analog.A_FC_TILLER_CAPT";
        public const string PITCH_CMD = "system.gates.B_PITCH_CMD";
        public const string ROLL_CMD = "system.gates.B_ROLL_CMD";
        public const string HYDRAULICS_AVAILABLE = "system.gates.B_HYDRAULICS_AVAILABLE";
        public const string MCP_AP_DISENGAGE = "system.switches.S_MCP_AP_DISENGAGE";
        public const string PAUSE = "simulator.pause";
        public const string SPEED_GROUND = "aircraft.speed.ground";

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
        private DataRef? _mcpApDisengageWriteRef;
        private DataRef? _pauseWriteRef;

        // DataRef collections
        private readonly Dictionary<string, DataRef> _dataRefs = new();
        private readonly Dictionary<string, double> _doubleValues = new();
        private readonly Dictionary<string, bool> _boolValues = new();

        // DataRef metadata: name -> isBoolean
        private readonly Dictionary<string, bool> _dataRefMetadata = new()
        {
            { AILERON_LEFT, false },
            { AILERON_RIGHT, false },
            { ELEVATOR, false },
            { TRIM_ELEVATOR, false },
            { TRIM_AILERON, false },
            { TRIM_RUDDER, false },
            { IS_STALLING, true },
            { SPEED_IAS, false },
            { AILERON_CPTN, false },
            { ELEVATOR_CPTN, false },
            { RUDDER_CAPT, false },
            { TILLER_CAPT, false },
            { PITCH_CMD, true },
            { ROLL_CMD, true },
            { HYDRAULICS_AVAILABLE, true },
            { MCP_AP_DISENGAGE, true },
            { PAUSE, true },
            { SPEED_GROUND, false }
        };

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

        // Public properties to expose values
        public double AileronLeft => _doubleValues.GetValueOrDefault(AILERON_LEFT) * 10000;
        public double AileronRight => _doubleValues.GetValueOrDefault(AILERON_RIGHT) * 10000;
        public double Elevator => _doubleValues.GetValueOrDefault(ELEVATOR) * 10000;
        public double TrimElevator => _doubleValues.GetValueOrDefault(TRIM_ELEVATOR);
        public double TrimAileron => _doubleValues.GetValueOrDefault(TRIM_AILERON);
        public double TrimRudder => _doubleValues.GetValueOrDefault(TRIM_RUDDER);
        public bool IsStalling => _boolValues.GetValueOrDefault(IS_STALLING);
        public double SpeedIas => _doubleValues.GetValueOrDefault(SPEED_IAS);
        public double AileronCptn => _doubleValues.GetValueOrDefault(AILERON_CPTN);
        public double ElevatorCptn => _doubleValues.GetValueOrDefault(ELEVATOR_CPTN);
        public double RudderCapt => _doubleValues.GetValueOrDefault(RUDDER_CAPT);
        public double TillerCapt => _doubleValues.GetValueOrDefault(TILLER_CAPT);
        public bool PitchCmd => _boolValues.GetValueOrDefault(PITCH_CMD);
        public bool RollCmd => _boolValues.GetValueOrDefault(ROLL_CMD);
        public bool HydraulicsAvailable => _boolValues.GetValueOrDefault(HYDRAULICS_AVAILABLE);
        public bool McpApDisengage => _boolValues.GetValueOrDefault(MCP_AP_DISENGAGE);
        public bool Pause => _boolValues.GetValueOrDefault(PAUSE);
        public double SpeedGround => _doubleValues.GetValueOrDefault(SPEED_GROUND);

        // Events for value changes
        public event EventHandler<DataRefValueChangedEventArgs>? OnAileronLeftChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnAileronRightChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnElevatorChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnTrimElevatorChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnTrimAileronChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnTrimRudderChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnIsStallingChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnSpeedIasChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnAileronCptnChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnElevatorCptnChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnRudderCaptChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnTillerCaptChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnPitchCmdChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnRollCmdChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnHydraulicsAvailableChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnMcpApDisengageChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnPauseChanged;
        public event EventHandler<DataRefValueChangedEventArgs>? OnSpeedGroundChanged;

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

        public void DisengageAP()
        {
            WriteDataRefValue(MCP_AP_DISENGAGE, 1);
        }

        public void PauseSim()
        {
            WriteDataRefValue(PAUSE, 1);
        }

        public void UnpauseSim()
        {
            WriteDataRefValue(PAUSE, 0);
        }

        private void WriteDataRefValue(string dataRefName, double value)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ProSimManager));

            if (State != ConnectionState.Connected)
                throw new InvalidOperationException("Not connected to ProSim");

            try
            {
                var dataRef = GetWritableDataRef(dataRefName);
                dataRef.value = value;

                if (_dataRefMetadata.TryGetValue(dataRefName, out var isBoolean))
                {
                    if (isBoolean)
                    {
                        _boolValues[dataRefName] = value != 0;
                    }
                    else
                    {
                        _doubleValues[dataRefName] = Math.Round(value, 3);
                    }

                    InvokeChangeEvent(dataRefName, value);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to write value for '{dataRefName}': {ex.Message}", ex);
            }
        }

        private DataRef GetWritableDataRef(string dataRefName)
        {
            return dataRefName switch
            {
                MCP_AP_DISENGAGE => _mcpApDisengageWriteRef ??= new DataRef(MCP_AP_DISENGAGE, connection),
                PAUSE => _pauseWriteRef ??= new DataRef(PAUSE, connection),
                _ => new DataRef(dataRefName, connection)
            };
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
            InitializeDataRefs();
        }

        private void Connection_onDisconnect()
        {
            // Called when ProSim notifies disconnection
            SetStatus(ConnectionState.Disconnected, "Disconnected from ProSim");
            CleanupDataRefs();
        }

        /// <summary>
        /// Initialize all DataRef subscriptions when connected.
        /// </summary>
        private void InitializeDataRefs()
        {
            try
            {
                foreach (var (dataRefName, _) in _dataRefMetadata)
                {
                    var dataRef = new DataRef(dataRefName, 5, connection);
                    dataRef.onDataChange += DataRef_onDataChange;
                    _dataRefs[dataRefName] = dataRef;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing DataRefs: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanup all DataRef subscriptions.
        /// </summary>
        private void CleanupDataRefs()
        {
            try
            {
                foreach (var (_, dataRef) in _dataRefs)
                {
                    dataRef.onDataChange -= DataRef_onDataChange;
                }
                _dataRefs.Clear();
                _doubleValues.Clear();
                _boolValues.Clear();
                _mcpApDisengageWriteRef = null;
                _pauseWriteRef = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning up DataRefs: {ex.Message}");
            }
        }

        // Generic DataRef change handler
        private void DataRef_onDataChange(DataRef dataRef)
        {
            // Find the dataRef name by searching the dictionary
            string? dataRefName = null;
            foreach (var kvp in _dataRefs)
            {
                if (kvp.Value == dataRef)
                {
                    dataRefName = kvp.Key;
                    break;
                }
            }

            if (dataRefName == null)
                return;

            var rawValue = Convert.ToDouble(dataRef.value);
            var roundedValue = Math.Round(rawValue, 3);

            if (_dataRefMetadata.TryGetValue(dataRefName, out var isBoolean))
            {
                if (isBoolean)
                {
                    _boolValues[dataRefName] = rawValue != 0;
                }
                else
                {
                    _doubleValues[dataRefName] = roundedValue;
                }

                // Invoke the specific event
                InvokeChangeEvent(dataRefName, rawValue);
            }
        }

        // Helper to invoke the appropriate change event
        private void InvokeChangeEvent(string dataRefName, double value)
        {
            var eventArgs = new DataRefValueChangedEventArgs(dataRefName, value);

            switch (dataRefName)
            {
                case AILERON_LEFT: OnAileronLeftChanged?.Invoke(this, eventArgs); break;
                case AILERON_RIGHT: OnAileronRightChanged?.Invoke(this, eventArgs); break;
                case ELEVATOR: OnElevatorChanged?.Invoke(this, eventArgs); break;
                case TRIM_ELEVATOR: OnTrimElevatorChanged?.Invoke(this, eventArgs); break;
                case TRIM_AILERON: OnTrimAileronChanged?.Invoke(this, eventArgs); break;
                case TRIM_RUDDER: OnTrimRudderChanged?.Invoke(this, eventArgs); break;
                case IS_STALLING: OnIsStallingChanged?.Invoke(this, eventArgs); break;
                case SPEED_IAS: OnSpeedIasChanged?.Invoke(this, eventArgs); break;
                case AILERON_CPTN: OnAileronCptnChanged?.Invoke(this, eventArgs); break;
                case ELEVATOR_CPTN: OnElevatorCptnChanged?.Invoke(this, eventArgs); break;
                case RUDDER_CAPT: OnRudderCaptChanged?.Invoke(this, eventArgs); break;
                case TILLER_CAPT: OnTillerCaptChanged?.Invoke(this, eventArgs); break;
                case PITCH_CMD: OnPitchCmdChanged?.Invoke(this, eventArgs); break;
                case ROLL_CMD: OnRollCmdChanged?.Invoke(this, eventArgs); break;
                case HYDRAULICS_AVAILABLE: OnHydraulicsAvailableChanged?.Invoke(this, eventArgs); break;
                case MCP_AP_DISENGAGE: OnMcpApDisengageChanged?.Invoke(this, eventArgs); break;
                case PAUSE: OnPauseChanged?.Invoke(this, eventArgs); break;
                case SPEED_GROUND: OnSpeedGroundChanged?.Invoke(this, eventArgs); break;
            }
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
                CleanupDataRefs();

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

    /// <summary>
    /// Event args for DataRef value changes.
    /// </summary>
    public class DataRefValueChangedEventArgs : EventArgs
    {
        public string DataRefName { get; }
        public double Value { get; }

        public DataRefValueChangedEventArgs(string dataRefName, double value)
        {
            DataRefName = dataRefName;
            Value = value;
        }
    }
}
