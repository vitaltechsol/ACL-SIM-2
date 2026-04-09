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
        private readonly IAppLogger? _logger;
        private bool _servoInitialized = false;

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
        private const double POSITION_TOLERANCE_UNITS = 5.0;

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

        // Acceleration ramp state
        private long _moveStartTick = 0;
        private AccelMode _lastConfiguredAccelMode = AccelMode.None;
        private int _lastConfiguredAccelParam1 = 0;
        private int _lastConfiguredAccelParam2 = 0;

        // Stall / timeout detection
        private double _prevMoveEncoder = double.NaN;
        private int _stallCount;
        private const int MAX_STALL_ITERATIONS = 10;
        private const int MAX_MOVE_TIMEOUT_MS = 30000;
        private long _timeoutTick = 0;

        // Overshoot / deceleration
        private int _prevDeltaSign;
        private const double DECEL_ZONE_UNITS = 2000.0;

        /// <summary>Minimum RPM the software ramp will issue during acceleration and deceleration.</summary>
        private const int MIN_RAMP_RPM = 1;

        public AxisMovement(Axis axis, ModbusClient? modbusClient = null, AxisTorqueControl? torqueControl = null, object? modbusLock = null, IAppLogger? logger = null)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));
            _modbusClient = modbusClient;
            _torqueControl = torqueControl;
            _modbusLock = modbusLock;
            _logger = logger;
        }

        /// <summary>
        /// Move the axis to a target position immediately at the specified or configured RPM.
        /// Stops any active movement first. No acceleration ramp is applied.
        /// </summary>
        /// <param name="targetPercent">Target position as percentage (-100 to +100).</param>
        /// <param name="rpmOverride">Optional RPM override. When null, MotorSpeedRpm is used.</param>
        public void GoToPosition(double targetPercent, int? rpmOverride = null)
        {
            targetPercent = Math.Clamp(targetPercent, -100.0, 100.0);

            if (_modbusClient == null || !_modbusClient.Connected)
            {
                _logger?.Log($"[{_axis.Name}] GoToPosition: ModbusClient unavailable");
                return;
            }

            if (!ValidateMotorSettings()) return;
            EnsureServoInitialized();

            lock (_targetLock)
            {
                _currentTarget = targetPercent;
            }

            // Stop any active movement first
            // actual stopped position rather than a mid-move value.
            Stop();

            var targetEncoder = PercentToEncoderPosition(targetPercent);
            targetEncoder = ClampToAxisRange(targetEncoder);

            var currentEncoder = _axis.EncoderPosition;
            var delta = targetEncoder - currentEncoder;

            if (Math.Abs(delta) < POSITION_TOLERANCE_UNITS)
                return;

            var pulses = (int)Math.Round(delta);
            if (pulses == 0) return;

            if (_axis.Settings.ReversedMotor)
                pulses = -pulses;

            var rpm = rpmOverride ?? _axis.Settings.MotorSpeedRpm;
            Debug.WriteLine($"[{_axis.Name}] GoToPosition {targetPercent:F1}% → {pulses} pulses @ {rpm} RPM (ReversedMotor={_axis.Settings.ReversedMotor})");
            ServoMoveTo(pulses, rpm);
        }

        /// <summary>
        /// Reset the acceleration ramp timer. Call when starting a new movement or
        /// when the target changes to restart acceleration from 1 RPM.
        /// </summary>
        public void BeginMove()
        {
            _moveStartTick = Environment.TickCount64;
            _timeoutTick = _moveStartTick;
            _prevMoveEncoder = double.NaN;
            _stallCount = 0;
            _prevDeltaSign = 0;
        }

        /// <summary>
        /// Refresh the move timeout and stall counter without resetting the RPM ramp.
        /// Call when the target changes while the motor is already moving so the
        /// acceleration ramp continues uninterrupted.
        /// </summary>
        public void RefreshMoveTimeout()
        {
            _timeoutTick = Environment.TickCount64;
            _stallCount = 0;
        }

        /// <summary>
        /// Move one step toward the target position using the software RPM ramp.
        /// Call this repeatedly (e.g. every 100 ms) from a tracking loop.
        /// RPM ramps from <see cref="MIN_RAMP_RPM"/> to <see cref="AxisSettings.MotorSpeedRpm"/>
        /// over <see cref="AxisSettings.MotorAccelParam1Ms"/> milliseconds since
        /// the last <see cref="BeginMove"/> call.
        /// Returns <c>true</c> when the target has been reached.
        /// </summary>
        public bool MoveToward(double targetPercent)
        {
            targetPercent = Math.Clamp(targetPercent, -100.0, 100.0);

            if (_modbusClient == null || !_modbusClient.Connected)
            {
                _logger?.Log($"[{_axis.Name}] MoveToward: ModbusClient unavailable");
                return true;
            }

            if (!ValidateMotorSettings()) return true;
            EnsureServoInitialized();

            var targetEncoder = PercentToEncoderPosition(targetPercent);
            targetEncoder = ClampToAxisRange(targetEncoder);

            var currentEncoder = _axis.EncoderPosition;
            var delta = targetEncoder - currentEncoder;

            lock (_targetLock)
            {
                _currentTarget = targetPercent;
            }

            if (Math.Abs(delta) < POSITION_TOLERANCE_UNITS)
            {
                ServoStop();
                Debug.WriteLine($"[{_axis.Name}] Target reached {targetPercent:F1}% (Enc: {currentEncoder:F0}→{targetEncoder:F0})");
                return true;
            }

            // Stall detection: if encoder hasn't moved significantly, motor is stuck
            if (!double.IsNaN(_prevMoveEncoder) && Math.Abs(currentEncoder - _prevMoveEncoder) < 5.0)
            {
                _stallCount++;
                if (_stallCount >= MAX_STALL_ITERATIONS)
                {
                    ServoStop();
                    _logger?.Log($"[{_axis.Name}] Motor stall detected ({_stallCount} iterations with no movement)");
                    _stallCount = 0;
                    return true;
                }
            }
            else
            {
                _stallCount = 0;
            }
            _prevMoveEncoder = currentEncoder;

            // Timeout safety: stop if move has taken too long
            if (_timeoutTick > 0)
            {
                var totalElapsedMs = Environment.TickCount64 - _timeoutTick;
                if (totalElapsedMs > MAX_MOVE_TIMEOUT_MS)
                {
                    ServoStop();
                    _logger?.Log($"[{_axis.Name}] Move timeout after {totalElapsedMs}ms");
                    return true;
                }
            }

            // Overshoot detection: if delta sign flipped, motor passed the target
            var currentDeltaSign = Math.Sign(delta);
            if (_prevDeltaSign != 0 && currentDeltaSign != 0 && currentDeltaSign != _prevDeltaSign)
            {
                ServoStop();
                _prevDeltaSign = currentDeltaSign;
                _moveStartTick = Environment.TickCount64;
                _prevMoveEncoder = double.NaN;
                _stallCount = 0;
                Debug.WriteLine($"[{_axis.Name}] Overshoot detected at {currentEncoder:F0}, restarting approach");
                return false;
            }
            _prevDeltaSign = currentDeltaSign;

            var pulses = (int)Math.Round(delta);
            if (pulses == 0) return true;

            // Apply motor direction reversal
            if (_axis.Settings.ReversedMotor)
                pulses = -pulses;

            // Software RPM ramp: 1 → maxRpm over accelMs, with optional S-curve blend
            var maxRpm = _axis.Settings.MotorSpeedRpm;
            var accelMs = (double)Math.Max(1, _axis.Settings.MotorAccelParam1Ms);
            var elapsedMs = (double)(Environment.TickCount64 - _moveStartTick);
            var linearFraction = Math.Min(1.0, elapsedMs / accelMs);
            var ts = (double)_axis.Settings.MotorAccelParam2Ms;
            double rampFraction;
            if (ts > 0.0)
            {
                // Blend linear ramp with smoothstep S-curve; tsRatio=1 when Ts reaches its max (accelMs/2)
                var tsRatio = Math.Min(1.0, ts / (accelMs / 2.0));
                var smoothFraction = linearFraction * linearFraction * (3.0 - 2.0 * linearFraction);
                rampFraction = (1.0 - tsRatio) * linearFraction + tsRatio * smoothFraction;
            }
            else
            {
                rampFraction = linearFraction;
            }
            var rpm = Math.Max(MIN_RAMP_RPM, (int)Math.Round(maxRpm * rampFraction));

            // Proximity-based deceleration: scale RPM down as we approach target
            var absDelta = Math.Abs(delta);
            if (absDelta < DECEL_ZONE_UNITS)
            {
                var decelRpm = Math.Max(MIN_RAMP_RPM, (int)Math.Round(rpm * (absDelta / DECEL_ZONE_UNITS)));
                rpm = Math.Min(rpm, decelRpm);
            }

            Debug.WriteLine($"[{_axis.Name}] MoveToward {targetPercent:F1}% → Enc: {currentEncoder:F0}→{targetEncoder:F0}, Δ{delta:F0} ({pulses} pulses) @ {rpm}/{maxRpm} RPM ({(int)elapsedMs}ms/{(int)accelMs}ms)");

            try
            {
                ServoMoveTo(pulses, rpm);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[{_axis.Name}] MoveToward error: {ex.Message}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Re-issue a move command for the last known target. Resets the ramp.
        /// Used when motion tuning settings change mid-movement.
        /// </summary>
        public void ReapplyCurrentTarget()
        {
            double targetPercent;
            lock (_targetLock)
            {
                targetPercent = _currentTarget;
            }

            BeginMove();
            MoveToward(targetPercent);
        }

        private void EnsureServoInitialized()
        {
            lock (_servoLock)
            {
                if (!_servoInitialized)
                {
                    _logger?.Log($"[{_axis.Name}] Initializing servo for first use...");
                    InitServo();
                    _servoInitialized = true;
                }
            }
        }

        private double ClampToAxisRange(double encoderPosition)
        {
            var min = _axis.Settings.FullLeftPosition + _axis.EncoderCenterOffset;
            var max = _axis.Settings.FullRightPosition + _axis.EncoderCenterOffset;
            return Math.Clamp(encoderPosition, min, max);
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
        /// Reset motion control state. Clears the current target and ramp timer.
        /// </summary>
        public void Reset()
        {
            _moveStartTick = 0;
            _prevMoveEncoder = double.NaN;
            _stallCount = 0;
            _prevDeltaSign = 0;
            _lastConfiguredAccelMode = AccelMode.None;
            _lastConfiguredAccelParam1 = 0;
            _lastConfiguredAccelParam2 = 0;

            lock (_targetLock)
            {
                _currentTarget = 0.0;
            }
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
                    _logger?.Log($"[{_axis.Name}] Stop command failed: {ex.Message}");
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
                _logger?.Log($"[{_axis.Name}] MoveByUnits: ModbusClient is null");
                return;
            }

            if (!_modbusClient.Connected)
            {
                _logger?.Log($"[{_axis.Name}] MoveByUnits: ModbusClient not connected");
                return;
            }

            if (!ValidateMotorSettings()) return;

            Stop();
            EnsureServoInitialized();

            var pulses = encoderUnits;
            if (_axis.Settings.ReversedMotor)
                pulses = -pulses;

            var requestedRpm = _axis.Settings.MotorSpeedRpm;

            Debug.WriteLine($"[{_axis.Name}] MoveByUnits: {encoderUnits} enc → {pulses} pulses @ {requestedRpm} RPM");

            try
            {
                ServoMoveTo(pulses, requestedRpm);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[{_axis.Name}] MoveByUnits error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates motor settings before attempting control.
        /// </summary>
        private bool ValidateMotorSettings()
        {
            if (_axis.Settings.MotorSpeedRpm < 0 || _axis.Settings.MotorSpeedRpm > 3000)
            {
                _logger?.Log($"[{_axis.Name}] Invalid MotorSpeedRpm: {_axis.Settings.MotorSpeedRpm}");
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

            _logger?.Log($"[{_axis.Name}] Servo initialized successfully");
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

        private void ServoMoveTo(int pulses, int rpm)
        {
            int slot = 0;
            // Set slot speed (clamped to valid range)
            WritePn(SlotSpeedPn[slot], Clamp(rpm, 0, 3000));

            // Program slot pulses
            WriteSlotPulses(slot, pulses);

            // Select slot and trigger movement
            SelectSlot(slot);
            PulsePtriger(SERVO_TRIGGER_PULSE_MS);
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
