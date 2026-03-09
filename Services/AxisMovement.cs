using System;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Service for controlling axis movement with advanced motion control features.
    /// Handles position targeting with smoothing, rate limiting, and deadband filtering.
    /// Uses AxisSettings for all configuration (OutputIntervalMs, SpeedRateLimitPercent, InputIntervalMs, TargetFilterAlpha, DeadbandDegrees).
    /// </summary>
    public class AxisMovement
    {
        private readonly Axis _axis;

        // Motion control state
        private double _filteredTarget = 0.0; // Smoothed target position
        private double _currentOutput = 0.0; // Current output position
        private DateTime _lastOutputUpdate = DateTime.MinValue;
        private DateTime _lastInputUpdate = DateTime.MinValue;

        public AxisMovement(Axis axis)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));
        }

        /// <summary>
        /// Move the axis to a target position specified as a percentage.
        /// </summary>
        /// <param name="targetPercent">
        /// Target position as percentage:
        /// -100 = Full left position (FullLeftPosition)
        /// 0 = Center position (CenterPosition)
        /// +100 = Full right position (FullRightPosition)
        /// </param>
        public void GoToPosition(double targetPercent)
        {
            // Clamp input to valid range
            targetPercent = Math.Max(-100.0, Math.Min(100.0, targetPercent));

            // Convert percentage to absolute encoder position
            var targetEncoderPosition = PercentToEncoderPosition(targetPercent);

            // Apply target smoothing filter (low-pass filter)
            var now = DateTime.UtcNow;
            if ((now - _lastInputUpdate).TotalMilliseconds >= _axis.Settings.InputIntervalMs)
            {
                _filteredTarget = ApplyTargetFilter(_filteredTarget, targetEncoderPosition);
                _lastInputUpdate = now;
            }

            // Get current encoder position and normalize to percentage
            var currentEncoderPosition = _axis.EncoderPosition;
            var currentPercent = EncoderPositionToPercent(currentEncoderPosition);

            // Calculate error (difference between filtered target and current position)
            var targetPercent_normalized = EncoderPositionToPercent(_filteredTarget);
            var error = targetPercent_normalized - currentPercent;

            // Apply deadband filter - ignore small changes to prevent jitter
            // DeadbandDegrees: Minimum change required before reacting (noise filter).
            // Prevents micro-jitter when the aircraft is stable.
            // Increase if you see small constant twitching. Decrease if fine movements feel unresponsive.
            if (Math.Abs(error) < _axis.Settings.DeadbandDegrees)
            {
                // Within deadband - no movement needed
                return;
            }

            // Check if it's time to update output
            // OutputIntervalMs: Output update rate (milliseconds). Controls how often the output value is sent to the motor.
            // 10 ms = 100 updates per second (100 Hz).
            if ((now - _lastOutputUpdate).TotalMilliseconds < _axis.Settings.OutputIntervalMs)
            {
                // Not time to update yet
                return;
            }
            _lastOutputUpdate = now;

            // Apply speed rate limiting
            // SpeedRateLimitPercent: Maximum needle movement (percentage of maximum speed). Limits how fast the axis can move.
            var maxChange = _axis.Settings.SpeedRateLimitPercent * (_axis.Settings.OutputIntervalMs / 1000.0);
            var desiredChange = error;
            var limitedChange = Math.Max(-maxChange, Math.Min(maxChange, desiredChange));

            // Calculate new output position
            _currentOutput = currentPercent + limitedChange;
            _currentOutput = Math.Max(-100.0, Math.Min(100.0, _currentOutput));

            // TODO: Send output to motor controller
            // This is where we'll implement the actual motor movement
            // We'll need to:
            // 1. Convert _currentOutput percentage to motor commands
            // 2. Determine direction (forward/backward registers)
            // 3. Calculate torque value based on distance from target
            // 4. Write to appropriate Modbus registers
            // 5. Handle motor reversal if needed
            //
            // Example (to be implemented):
            // var torque = CalculateTorqueForMovement(error);
            // var direction = error > 0 ? Direction.Right : Direction.Left;
            // await WriteMotorCommand(torque, direction);
        }

        /// <summary>
        /// Convert a percentage (-100 to +100) to absolute encoder position.
        /// </summary>
        private double PercentToEncoderPosition(double percent)
        {
            var centerPosition = _axis.Settings.CenterPosition;

            if (percent >= 0)
            {
                // Moving right (positive direction)
                var fullRightPosition = _axis.Settings.FullRightPosition;
                var range = Math.Abs(fullRightPosition);
                return centerPosition + (percent / 100.0) * range;
            }
            else
            {
                // Moving left (negative direction)
                var fullLeftPosition = _axis.Settings.FullLeftPosition;
                var range = Math.Abs(fullLeftPosition);
                return centerPosition + (percent / 100.0) * range;
            }
        }

        /// <summary>
        /// Convert absolute encoder position to percentage (-100 to +100).
        /// </summary>
        private double EncoderPositionToPercent(double encoderPosition)
        {
            var centerPosition = _axis.Settings.CenterPosition;
            var offset = encoderPosition - centerPosition;

            if (offset >= 0)
            {
                // Right side (positive)
                var fullRightPosition = _axis.Settings.FullRightPosition;
                var range = Math.Max(1e-6, Math.Abs(fullRightPosition));
                var normalized = Math.Min(1.0, Math.Abs(offset) / range);
                return normalized * 100.0;
            }
            else
            {
                // Left side (negative)
                var fullLeftPosition = _axis.Settings.FullLeftPosition;
                var range = Math.Max(1e-6, Math.Abs(fullLeftPosition));
                var normalized = Math.Min(1.0, Math.Abs(offset) / range);
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
            _filteredTarget = _axis.EncoderPosition;
            _currentOutput = EncoderPositionToPercent(_axis.EncoderPosition);
            _lastOutputUpdate = DateTime.MinValue;
            _lastInputUpdate = DateTime.MinValue;
        }
    }
}
