using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ACL_SIM_2.Models;
using EasyModbus;

namespace ACL_SIM_2.Services
{
    public enum AccelMode
    {
        None = 0,      // Pn109=0
        Linear = 1,    // Pn109=1, Pn110=time constant (ms)
        SCurve = 2     // Pn109=2, Pn111=Ta (ms), Pn112=Ts (ms)
    }

    /// <summary>
    /// Service for controlling axis movement with advanced motion control features.
    /// Handles position targeting with smoothing, rate limiting, and deadband filtering.
    /// Uses AxisSettings for all configuration (OutputIntervalMs, SpeedRateLimitPercent, InputIntervalMs, TargetFilterAlpha, DeadbandDegrees).
    /// Includes integrated AASD servo control via RS-485 Modbus.
    /// </summary>
    public class AxisMovement
    {
        private readonly Axis _axis;
        private readonly ModbusClient? _modbusClient;
        private readonly AxisTorqueControl? _torqueControl;
        private readonly object _servoLock = new object();
        private readonly object? _modbusLock; // Shared lock for thread-safe Modbus access
        private bool _servoInitialized = false;

        // Closed-loop control state
        private CancellationTokenSource? _controlLoopCts;
        private double _currentTarget = 0.0;
        private readonly object _targetLock = new object();

        // Timing constants for servo control (minimized for fast response)
        private const int SERVO_TRIGGER_PULSE_MS = 10;  // Reduced from 30ms to 10ms (minimum required)
        private const int SERVO_TRIGGER_SETUP_MS = 2;   // Reduced from 5ms to 2ms
        private const int SERVO_STOP_PULSE_MS = 10;     // Reduced from 20ms to 10ms
        private const int MIN_TRIGGER_PULSE_MS = 10;
        /// <summary>
        /// Minimum encoder-position error required before issuing or updating a position command.
        /// Prevents tiny corrections from retriggering the servo during tracking.
        /// </summary>
        private const double POSITION_TOLERANCE_UNITS = 10.0;

        // AASD Servo Pn parameters
        private const int Pn002 = 2;   // Control mode
        private const int Pn117 = 117; // Location command source
        private const int Pn068 = 68;  // Low bank (e.g., Son)
        private const int Pn069 = 69;  // High bank (e.g., Pos1/Pos2/Ptriger/Pstop)
        private const int Pn070 = 70;  // Includes Son, etc.
        private const int Pn071 = 71;  // Includes Pos1/Pos2/Ptriger/Pstop
        private const int Pn109 = 109; // Accel/decel mode
        private const int Pn110 = 110; // Linear filter time constant (ms)
        private const int Pn111 = 111; // S-curve Ta (ms)
        private const int Pn112 = 112; // S-curve Ts (ms)

        // Internal position slots — pulses (high/low) and speed (rpm)
        private static readonly int[,] SlotPulsePn = new int[,]
        {
            {120, 121}, // slot 0: high, low
            {122, 123}, // slot 1
            {124, 125}, // slot 2
            {126, 127}, // slot 3
        };
        private static readonly int[] SlotSpeedPn = { 128, 129, 130, 131 };

        // Bit positions (active-low logic: 0 = ON, 1 = OFF)
        private const int BIT_SON = 0;      // Pn070 bit0: Servo On
        private const int BIT_POS1 = 8;     // Pn071 bit8: Position slot bit 1
        private const int BIT_POS2 = 9;     // Pn071 bit9: Position slot bit 2
        private const int BIT_PTRIGER = 10; // Pn071 bit10: Position trigger
        private const int BIT_PSTOP = 11;   // Pn071 bit11: Position stop

        // Motion control state
        private double _filteredTarget = 0.0; // Smoothed target position
        private double _currentOutput = 0.0; // Current output position
        private double _lastCommandedPosition = 0.0; // Last position we commanded the motor to move to
        private DateTime _lastOutputUpdate = DateTime.MinValue;
        private DateTime _lastInputUpdate = DateTime.MinValue;
        private DateTime _lastMotorCommand = DateTime.MinValue; // Track last time motor command was sent

        public AxisMovement(Axis axis, ModbusClient? modbusClient = null, AxisTorqueControl? torqueControl = null, object? modbusLock = null)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));
            _modbusClient = modbusClient;
            _torqueControl = torqueControl;
            _modbusLock = modbusLock;
        }

        /// <summary>
        /// Move the axis to a target position specified as a percentage.
        /// Uses the servo's built-in positioning - calculates pulse distance and sends single command.
        /// Calling this method again with a new target will cancel the previous movement.
        /// </summary>
        /// <param name="targetPercent">
        /// Target position as percentage:
        /// -100 = Full left position (FullLeftPosition)
        /// 0 = Center position (CenterPosition)
        /// +100 = Full right position (FullRightPosition)
        /// </param>
        /// <param name="rpmOverride">
        /// Optional RPM value that overrides <see cref="AxisSettings.MotorSpeedRpm"/>.
        /// When null, the configured MotorSpeedRpm is used.
        /// </param>
        public void GoToPosition(double targetPercent, int? rpmOverride = null)
        {
            CommandPosition(targetPercent, rpmOverride, applyFiltering: false, stopActiveMove: true);
        }

        /// <summary>
        /// Tracks a continuously changing target more smoothly by filtering updates and
        /// avoiding a stop/restart cycle for every new command.
        /// </summary>
        /// <param name="targetPercent">Target position as percentage (-100 to +100).</param>
        /// <param name="rpmOverride">
        /// Optional RPM value that overrides <see cref="AxisSettings.MotorSpeedRpm"/>.
        /// When null, the configured MotorSpeedRpm is used.
        /// </param>
        public void TrackTargetPosition(double targetPercent, int? rpmOverride = null)
        {
            CommandPosition(targetPercent, rpmOverride, applyFiltering: true, stopActiveMove: false);
        }

        private void CommandPosition(double targetPercent, int? rpmOverride, bool applyFiltering, bool stopActiveMove)
        {
            targetPercent = Math.Max(-100.0, Math.Min(100.0, targetPercent));
            var now = DateTime.UtcNow;

            if (_modbusClient == null || !_modbusClient.Connected)
            {
                Debug.WriteLine($"[{_axis.Name}] GoToPosition: ModbusClient unavailable");
                return;
            }

            if (!ValidateMotorSettings())
            {
                Debug.WriteLine($"[{_axis.Name}] ERROR: Motor settings validation failed!");
                return;
            }

            lock (_servoLock)
            {
                if (!_servoInitialized)
                {
                    Debug.WriteLine($"[{_axis.Name}] Initializing servo for first use...");
                    try
                    {
                        InitServo();
                        _servoInitialized = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{_axis.Name}] Servo initialization failed: {ex.Message}");
                        return;
                    }
                }
            }

            var targetEncoderPosition = PercentToEncoderPosition(targetPercent);

            var minEncoderPosition = _axis.Settings.FullLeftPosition + _axis.EncoderCenterOffset;
            var maxEncoderPosition = _axis.Settings.FullRightPosition + _axis.EncoderCenterOffset;
            targetEncoderPosition = Math.Max(minEncoderPosition, Math.Min(maxEncoderPosition, targetEncoderPosition));

            var currentEncoderPos = _axis.EncoderPosition;

            lock (_targetLock)
            {
                _currentTarget = targetPercent;

                if (applyFiltering)
                {
                    if (_lastMotorCommand == DateTime.MinValue)
                    {
                        _filteredTarget = currentEncoderPos;
                        _lastCommandedPosition = currentEncoderPos;
                    }

                    _filteredTarget = ApplyTargetFilter(_filteredTarget, targetEncoderPosition);
                    targetEncoderPosition = _filteredTarget;

                    if (Math.Abs(targetEncoderPosition - _lastCommandedPosition) < POSITION_TOLERANCE_UNITS)
                    {
                        return;
                    }
                }
                else
                {
                    _filteredTarget = targetEncoderPosition;
                }
            }

            var deltaEncoderUnits = targetEncoderPosition - currentEncoderPos;
            if (Math.Abs(deltaEncoderUnits) < POSITION_TOLERANCE_UNITS)
            {
                return;
            }

            var minMotorCommandIntervalMs = Math.Max(0, _axis.Settings.MinMotorCommandIntervalMs);
            if (applyFiltering &&
                minMotorCommandIntervalMs > 0 &&
                _lastMotorCommand != DateTime.MinValue &&
                (now - _lastMotorCommand).TotalMilliseconds < minMotorCommandIntervalMs)
            {
                return;
            }

            if (stopActiveMove)
            {
                if (_controlLoopCts != null)
                {
                    _controlLoopCts.Cancel();
                    try
                    {
                        ServoStop();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{_axis.Name}] Error stopping servo: {ex.Message}");
                    }
                    _controlLoopCts.Dispose();
                }
                _controlLoopCts = new CancellationTokenSource();
            }

            var pulses = (int)Math.Round(deltaEncoderUnits);
            if (_axis.Settings.ReversedMotor)
            {
                pulses = -pulses;
            }

            if (Math.Abs(pulses) < 1)
            {
                return;
            }

            var rpm = rpmOverride ?? _axis.Settings.MotorSpeedRpm;
            var accelMode = (AccelMode)_axis.Settings.MotorAccelMode;
            var accelParam1 = _axis.Settings.MotorAccelParam1Ms;
            var accelParam2 = _axis.Settings.MotorAccelParam2Ms;
            var commandType = applyFiltering ? "TrackTargetPosition" : "GoToPosition";

            Debug.WriteLine($"[{_axis.Name}] {commandType} {targetPercent:F1}% → Enc: {currentEncoderPos:F0}→{targetEncoderPosition:F0}, Δ{deltaEncoderUnits:F0} ({pulses} pulses) @ {rpm} RPM");

            try
            {
                ServoMoveTo(pulses, rpm, accelMode, accelParam1, accelParam2, slot: 0);

                lock (_targetLock)
                {
                    _lastCommandedPosition = targetEncoderPosition;
                    _lastMotorCommand = now;
                    _currentOutput = EncoderPositionToPercent(currentEncoderPos);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_axis.Name}] Movement error: {ex.Message}");
            }
        }

        public void ReapplyCurrentTarget(bool applyFiltering)
        {
            double targetPercent;

            lock (_targetLock)
            {
                targetPercent = _currentTarget;
                _lastMotorCommand = DateTime.MinValue;
            }

            CommandPosition(targetPercent, rpmOverride: null, applyFiltering: applyFiltering, stopActiveMove: true);
        }

        /// <summary>
        /// Convert a percentage (-100 to +100) to absolute encoder position.
        /// Uses relative FullLeft/FullRight distances from center plus EncoderCenterOffset.
        /// </summary>
        private double PercentToEncoderPosition(double percent)
        {
            double relativeTarget;
            if (percent >= 0)
            {
                // Interpolate from center (0) toward full right (positive relative distance)
                relativeTarget = (percent / 100.0) * _axis.Settings.FullRightPosition;
            }
            else
            {
                // Interpolate from center (0) toward full left (negative relative distance)
                relativeTarget = (percent / 100.0) * Math.Abs(_axis.Settings.FullLeftPosition);
            }

            // Add offset to convert from relative to absolute encoder position
            return relativeTarget + _axis.EncoderCenterOffset;
        }

        /// <summary>
        /// Convert absolute encoder position to percentage (-100 to +100).
        /// </summary>
        private double EncoderPositionToPercent(double encoderPosition)
        {
            // Relative position: 0 at center
            var relativePos = encoderPosition - _axis.EncoderCenterOffset;

            if (relativePos >= 0)
            {
                var range = Math.Max(1e-6, _axis.Settings.FullRightPosition);
                var normalized = Math.Min(1.0, relativePos / range);
                return normalized * 100.0;
            }
            else
            {
                var range = Math.Max(1e-6, Math.Abs(_axis.Settings.FullLeftPosition));
                var normalized = Math.Min(1.0, Math.Abs(relativePos) / range);
                return -normalized * 100.0;
            }
        }

        /// <summary>
        /// Apply exponential moving average filter to smooth target changes.
        /// TargetFilterAlpha: Target smoothing factor (0–1). Low-pass filter applied to incoming data before the axis moves toward it.
        /// 0.0 = no smoothing (instant response), 1.0 = maximum smoothing (slow response).
        /// Recommended: 0.1 - 0.5 for smooth motion.
        /// </summary>
        /// <param name="currentFiltered">Current filtered value</param>
        /// <param name="newTarget">New target value</param>
        /// <returns>Filtered target value</returns>
        private double ApplyTargetFilter(double currentFiltered, double newTarget)
        {
            // Exponential moving average: filtered = (alpha * new) + ((1 - alpha) * current)
            // Alpha close to 1 = fast response (less smoothing)
            // Alpha close to 0 = slow response (more smoothing)
            var alpha = Math.Max(0.0, Math.Min(1.0, _axis.Settings.TargetFilterAlpha));
            return (alpha * newTarget) + ((1.0 - alpha) * currentFiltered);
        }

        /// <summary>
        /// Gets the current filtered target position (encoder units).
        /// </summary>
        public double FilteredTargetPosition => _filteredTarget;

        /// <summary>
        /// Gets the current output position percentage (-100 to +100).
        /// </summary>
        public double CurrentOutputPercent => _currentOutput;

        /// <summary>
        /// Reset motion control state (clears filters and output).
        /// </summary>
        public void Reset()
        {
            // Set filtered target to current encoder position
            _filteredTarget = _axis.EncoderPosition;

            // Apply safety limits using relative positions offset to absolute
            var minEncoderPosition = _axis.Settings.FullLeftPosition + _axis.EncoderCenterOffset;
            var maxEncoderPosition = _axis.Settings.FullRightPosition + _axis.EncoderCenterOffset;
            _filteredTarget = Math.Max(minEncoderPosition, Math.Min(maxEncoderPosition, _filteredTarget));

            // Initialize last commanded position to current encoder position
            _lastCommandedPosition = _axis.EncoderPosition;

            _currentOutput = EncoderPositionToPercent(_axis.EncoderPosition);
            _lastOutputUpdate = DateTime.MinValue;
            _lastInputUpdate = DateTime.MinValue;
            _lastMotorCommand = DateTime.MinValue;
        }

        /// <summary>
        /// Stop motor movement immediately.
        /// </summary>
        public void Stop()
        {
            if (_modbusClient != null && _modbusClient.Connected)
            {
                try
                {
                    ServoStop();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{_axis.Name}] Stop command failed: {ex.Message}");
                }
            }
            else if (_modbusClient != null && !_modbusClient.Connected)
            {
                Debug.WriteLine($"[{_axis.Name}] Stop command skipped: ModbusClient is not connected");
            }
        }

        /// <summary>
        /// Move the axis by a specified number of encoder units relative to its current position.
        /// Unlike <see cref="GoToPosition"/>, this does not depend on <c>EncoderCenterOffset</c>
        /// or the calibrated <c>FullLeftPosition</c>/<c>FullRightPosition</c> range, making it
        /// suitable for the initial centering phase before those values are known.
        /// Positive values increase the raw encoder reading; negative values decrease it.
        /// The <c>ReversedMotor</c> setting is applied internally.
        /// </summary>
        /// <param name="encoderUnits">Signed number of encoder units to move.</param>
        public void MoveByUnits(int encoderUnits)
        {
            if (encoderUnits == 0) return;

            if (_modbusClient == null)
            {
                Debug.WriteLine($"[{_axis.Name}] MoveByUnits: ModbusClient is null");
                return;
            }

            if (!_modbusClient.Connected)
            {
                Debug.WriteLine($"[{_axis.Name}] MoveByUnits: ModbusClient not connected");
                return;
            }

            if (!ValidateMotorSettings()) return;

            // Cancel any existing movement
            if (_controlLoopCts != null)
            {
                _controlLoopCts.Cancel();
                try { ServoStop(); }
                catch { }
                _controlLoopCts.Dispose();
            }
            _controlLoopCts = new CancellationTokenSource();

            // Initialize servo on first use
            lock (_servoLock)
            {
                if (!_servoInitialized)
                {
                    try
                    {
                        InitServo();
                        _servoInitialized = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{_axis.Name}] MoveByUnits: servo init failed: {ex.Message}");
                        return;
                    }
                }
            }

            var pulses = encoderUnits;
            if (_axis.Settings.ReversedMotor)
                pulses = -pulses;

            var rpm = _axis.Settings.MotorSpeedRpm;
            var accelMode = (AccelMode)_axis.Settings.MotorAccelMode;
            var accelParam1 = _axis.Settings.MotorAccelParam1Ms;
            var accelParam2 = _axis.Settings.MotorAccelParam2Ms;

            Debug.WriteLine($"[{_axis.Name}] MoveByUnits: {encoderUnits} enc → {pulses} pulses @ {rpm} RPM");

            try
            {
                ServoMoveTo(pulses, rpm, accelMode, accelParam1, accelParam2, slot: 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_axis.Name}] MoveByUnits error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates motor settings before attempting control.
        /// </summary>
        private bool ValidateMotorSettings()
        {
            if (_axis.Settings.MotorSpeedRpm < 0 || _axis.Settings.MotorSpeedRpm > 3000)
            {
                System.Diagnostics.Debug.WriteLine($"[{_axis.Name}] Invalid MotorSpeedRpm: {_axis.Settings.MotorSpeedRpm}");
                return false;
            }

            return true;
        }

        #region Servo Control Methods

        private ushort ReadPn(int pn)
        {
            if (_modbusClient == null)
                throw new InvalidOperationException("ModbusClient is not available");

            if (!_modbusClient.Connected)
                throw new InvalidOperationException("ModbusClient is not connected");

            if (_modbusLock != null)
            {
                lock (_modbusLock)
                {
                    return unchecked((ushort)_modbusClient.ReadHoldingRegisters(pn, 1)[0]);
                }
            }
            return unchecked((ushort)_modbusClient.ReadHoldingRegisters(pn, 1)[0]);
        }

        private void WritePn(int pn, int val)
        {
            if (_modbusClient == null)
                throw new InvalidOperationException("ModbusClient is not available");

            if (!_modbusClient.Connected)
                throw new InvalidOperationException("ModbusClient is not connected");

            if (_modbusLock != null)
            {
                lock (_modbusLock)
                {
                    _modbusClient.WriteSingleRegister(pn, val);
                }
            }
            else
            {
                _modbusClient.WriteSingleRegister(pn, val);
            }
        }

        private void SetBitLow(int pn, int bit)
        {
            var cur = ReadPn(pn);
            var next = (ushort)(cur & ~(1 << bit));
            if (next != cur) WritePn(pn, next);
        }

        private void SetBitHigh(int pn, int bit)
        {
            var cur = ReadPn(pn);
            var next = (ushort)(cur | (1 << bit));
            if (next != cur) WritePn(pn, next);
        }

        private void InitServo()
        {
            WritePn(Pn002, 2);      // Position mode
            WritePn(Pn117, 1);      // Internal position source
            WritePn(Pn068, 0x0001); // Enable comm control for Son
            WritePn(Pn069, 0x0F00); // Enable comm control for Pos1/Pos2/Ptriger/Pstop
            SetBitLow(Pn070, BIT_SON); // Enable servo (active-low)

            // Set torque limit for position movements using MovingTorqueDisplay
            SetMovingTorqueLimit();

            System.Diagnostics.Debug.WriteLine($"[{_axis.Name}] Servo initialized successfully");
        }

        /// <summary>
        /// Sets the moving torque limit for position movements based on MovingTorqueDisplay setting.
        /// MovingTorqueDisplay is 0-100 (display scale) which maps to 0-300 (actual motor torque scale).
        /// Uses SetTorqueRight and SetTorqueLeft to set the torque for both directions.
        /// </summary>
        private void SetMovingTorqueLimit()
        {
            if (_torqueControl == null)
            {
                System.Diagnostics.Debug.WriteLine($"[{_axis.Name}] TorqueControl not available, skipping moving torque limit");
                return;
            }

            // Convert MovingTorquePercentage (0-100) to actual motor torque (0-300)
            var torqueActual = _axis.Settings.ConvertTorqueDisplayToActual(_axis.Settings.MovingTorquePercentage);
            var torqueInt = (int)Math.Round(torqueActual);

            // Clamp to valid range (0-300)
            torqueInt = Math.Max(0, Math.Min(300, torqueInt));

            // Set torque for both directions using AxisTorqueControl
            _torqueControl.SetTorqueBoth(torqueInt, torqueInt);

            System.Diagnostics.Debug.WriteLine($"[{_axis.Name}] Moving torque limit set to {torqueInt} (from display value {_axis.Settings.MovingTorquePercentage})");
        }

        private void ServoMoveTo(int pulses, int rpm, AccelMode accelMode, int accelParam1Ms, int accelParam2Ms, int slot)
        {
            if (slot < 0 || slot > 3)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Slot must be 0-3, got {slot}");

            // Configure acceleration shaping
            ConfigureAcceleration(accelMode, accelParam1Ms, accelParam2Ms);

            // Set slot speed (clamped to valid range)
            WritePn(SlotSpeedPn[slot], Clamp(rpm, 0, 3000));

            // Program slot pulses
            WriteSlotPulses(slot, pulses);

            // Select slot and trigger movement
            SelectSlot(slot);
            PulsePtriger(SERVO_TRIGGER_PULSE_MS);
        }

        private void ConfigureAcceleration(AccelMode accelMode, int accelParam1Ms, int accelParam2Ms)
        {
            switch (accelMode)
            {
                case AccelMode.None:
                    WritePn(Pn109, 0);
                    break;

                case AccelMode.Linear:
                    WritePn(Pn109, 1);
                    WritePn(Pn110, Clamp(accelParam1Ms, 5, 500));
                    break;

                case AccelMode.SCurve:
                    WritePn(Pn109, 2);
                    WritePn(Pn111, Clamp(accelParam1Ms, 5, 340));
                    WritePn(Pn112, Clamp(accelParam2Ms, 5, 150));
                    break;

                default:
                    throw new ArgumentException($"Unknown acceleration mode: {accelMode}");
            }
        }

        private void WriteSlotPulses(int slot, int pulses)
        {
            int highPn = SlotPulsePn[slot, 0];
            int lowPn = SlotPulsePn[slot, 1];
            int high = pulses / 10000;
            int low = pulses % 10000;
            if (pulses < 0 && low != 0)
            {
                high -= 1;
                low = 10000 + low;
            }
            WritePn(highPn, high);
            WritePn(lowPn, low);
        }

        private void SelectSlot(int slot)
        {
            // Slot selection via 2-bit binary encoding (active-low)
            // slot=0: pos1=OFF, pos2=OFF | slot=1: pos1=ON, pos2=OFF
            // slot=2: pos1=OFF, pos2=ON  | slot=3: pos1=ON, pos2=ON
            bool pos1On = (slot & 0x01) != 0; // Check bit 0
            bool pos2On = (slot & 0x02) != 0; // Check bit 1

            if (pos1On) SetBitLow(Pn071, BIT_POS1); else SetBitHigh(Pn071, BIT_POS1);
            if (pos2On) SetBitLow(Pn071, BIT_POS2); else SetBitHigh(Pn071, BIT_POS2);
        }

        private void PulsePtriger(int pulseMs)
        {
            // Pulse sequence: OFF → ON → OFF (active-low logic)
            SetBitHigh(Pn071, BIT_PTRIGER);         // Ensure OFF state
            Thread.Sleep(SERVO_TRIGGER_SETUP_MS);   // Setup time

            SetBitLow(Pn071, BIT_PTRIGER);          // Trigger ON
            Thread.Sleep(Math.Max(MIN_TRIGGER_PULSE_MS, pulseMs)); // Hold time

            SetBitHigh(Pn071, BIT_PTRIGER);         // Trigger OFF
        }

        private void ServoStop()
        {
            // Pulse stop signal (active-low)
            SetBitLow(Pn071, BIT_PSTOP);
            Thread.Sleep(SERVO_STOP_PULSE_MS);
            SetBitHigh(Pn071, BIT_PSTOP);
        }

        private static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);

        #endregion
    }
}
