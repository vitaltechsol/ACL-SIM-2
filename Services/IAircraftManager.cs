using System;
using System.Threading.Tasks;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Defines the contract for aircraft simulator managers.
    /// This interface allows for easy swapping between different aircraft SDKs (ProSim, PMDG, etc.).
    /// </summary>
    public interface IAircraftManager : IDisposable
    {
        // Connection Management
        /// <summary>
        /// Current connection state.
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// Current status message.
        /// </summary>
        string StatusMessage { get; }

        /// <summary>
        /// Event raised when connection state changes.
        /// </summary>
        event EventHandler<ConnectionStateEventArgs>? OnConnectionStateChanged;

        /// <summary>
        /// Connects to the aircraft simulator at the specified IP address.
        /// </summary>
        /// <param name="ipAddress">Simulator server IP address.</param>
        /// <returns>Task that completes when connection attempt finishes</returns>
        Task ConnectAsync(string ipAddress = "127.0.0.0");

        /// <summary>
        /// Disconnects from the aircraft simulator.
        /// </summary>
        void Disconnect();

        // Flight Control Positions (Autopilot/Actuator positions)
        double AileronLeft { get; }
        double AileronRight { get; }
        double Elevator { get; }

        // Trim Positions
        double TrimElevator { get; }
        double TrimAileron { get; }
        double TrimRudder { get; }

        // Flight State
        bool IsStalling { get; }
        double SpeedIas { get; }

        // Captain's Control Positions (Physical control positions)
        double AileronCptn { get; }
        double ElevatorCptn { get; }
        double RudderCapt { get; }
        double TillerCapt { get; }

        // Autopilot State
        bool PitchCmd { get; }
        bool RollCmd { get; }

        // Aircraft Systems
        bool HydraulicsAvailable { get; }
        bool McpApDisengage { get; }
        bool Pause { get; }
        double SpeedGround { get; }

        // Events for value changes
        event EventHandler<DataRefValueChangedEventArgs>? OnAileronLeftChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnAileronRightChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnElevatorChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnTrimElevatorChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnTrimAileronChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnTrimRudderChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnIsStallingChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnSpeedIasChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnAileronCptnChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnElevatorCptnChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnRudderCaptChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnTillerCaptChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnPitchCmdChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnRollCmdChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnHydraulicsAvailableChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnMcpApDisengageChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnPauseChanged;
        event EventHandler<DataRefValueChangedEventArgs>? OnSpeedGroundChanged;

        // Control Methods
        /// <summary>
        /// Disengage the autopilot.
        /// </summary>
        void DisengageAP();

        /// <summary>
        /// Pause the simulator.
        /// </summary>
        void PauseSim();

        /// <summary>
        /// Unpause the simulator.
        /// </summary>
        void UnpauseSim();

        /// <summary>
        /// Gets a reference value from the simulator by name.
        /// </summary>
        /// <param name="refName">Name of the reference variable</param>
        /// <returns>The current value of the reference variable</returns>
        double GetProSimRefVal(string refName);
    }

    /// <summary>
    /// Connection state for aircraft simulator.
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Failed
    }

    /// <summary>
    /// Event args for simulator connection state changes.
    /// </summary>
    public class ConnectionStateEventArgs : EventArgs
    {
        public ConnectionState State { get; }
        public string Message { get; }

        public ConnectionStateEventArgs(ConnectionState state, string message)
        {
            State = state;
            Message = message;
        }
    }

    /// <summary>
    /// Event args for data reference value changes.
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
