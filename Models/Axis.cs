using System;

namespace ACL_SIM_2.Models
{
    // Settings that can be persisted and adjusted by the user.
    public class AxisSettings
    {
        // Encoder position calibration (absolute encoder values with rollover tracking)
        public double CenterPosition { get; set; } = 0.0; // absolute encoder value at center
        // Range sliders are presented 0..100 in UI. Actual motor uses different ranges (example torque 0..300).
        public double FullLeftPosition { get; set; } = -2000; // encoder relative to center (left/negative direction)
        public double FullRightPosition { get; set; } = 2000; // encoder relative to center (right/positive direction)
        public double MinTorqueDisplay { get; set; } = 0; // 0..100
        public double MaxTorqueDisplay { get; set; } = 100; // 0..100
        public bool ReversedMotor { get; set; } = false;
        public double MovingTorqueDisplay { get; set; } = 10; // 0..100 (display value for UI slider)
        public double MovingTorque { get; set; } = 30.0; // 0..300 (actual motor value)
        public double SelfCenteringSpeed { get; set; } = 50; // 1..100
        public double Dampening { get; set; } = 10; // 0..100
        public double HydraulicOffTorqueDisplay { get; set; } = 80; // 0..100
        public double AutopilotOverridePercent { get; set; } = 5; // 1..100 (only meaningful for pitch/roll)

        // Advanced motion tweak settings - used by AxisMovement service

        /// <summary>
        /// Output update rate (milliseconds). Controls how often the output value is sent to the motor.
        /// 10 ms = 100 updates per second (100 Hz).
        /// </summary>
        public int OutputIntervalMs { get; set; } = 10;

        /// <summary>
        /// Maximum needle movement (percentage of maximum speed). Limits how fast the axis can move.
        /// Range: 0-100. Higher values = faster movement.
        /// </summary>
        public int SpeedRateLimitPercent { get; set; } = 50;

        /// <summary>
        /// How often the system samples new target values (milliseconds).
        /// 10 ms = 100 Hz target updates.
        /// </summary>
        public int InputIntervalMs { get; set; } = 10;

        /// <summary>
        /// Target smoothing factor (0–1). Low-pass filter applied to incoming data before the axis moves toward it.
        /// 0.0 = no smoothing (instant response), 1.0 = maximum smoothing (slow response).
        /// For slider control: Use high value (0.8-1.0) for immediate response
        /// For simulator data: Use low value (0.1-0.3) for smooth motion
        /// </summary>
        public double TargetFilterAlpha { get; set; } = 0.9;

        /// <summary>
        /// Minimum time between motor commands (milliseconds). Prevents overwhelming the servo with commands.
        /// When slider moves rapidly, commands are throttled to this interval.
        /// Lower = more responsive, Higher = more stable
        /// Recommended: 50ms for closed-loop feedback control
        /// </summary>
        public int MinMotorCommandIntervalMs { get; set; } = 50;

        // Servo motor position control settings

        /// <summary>
        /// Motor speed in RPM for position movements.
        /// Range: 0-3000 rpm. Higher values = faster movement.
        /// Default: 10 RPM for very smooth, controlled movements.
        /// </summary>
        public int MotorSpeedRpm { get; set; } = 8;

        /// <summary>
        /// Motor acceleration mode: 0=None, 1=Linear, 2=S-Curve
        /// </summary>
        public int MotorAccelMode { get; set; } = 1; // Linear by default

        /// <summary>
        /// Motor acceleration parameter 1 (ms).
        /// Linear mode: time constant (5-500ms)
        /// S-Curve mode: Ta parameter (5-340ms)
        /// </summary>
        public int MotorAccelParam1Ms { get; set; } = 50;

        /// <summary>
        /// Motor acceleration parameter 2 (ms).
        /// S-Curve mode only: Ts parameter (5-150ms)
        /// Ignored for None/Linear modes.
        /// </summary>
        public int MotorAccelParam2Ms { get; set; } = 30;

        // Conversion constants (example). These map display [0..100] to actual values used by motors.
        public double TorqueDisplayMax { get; set; } = 100.0;
        public double TorqueActualMax { get; set; } = 300.0;
        // Persisted flag whether this axis is enabled for user interaction
        public bool Enabled { get; set; } = true;

        // Communication / driver settings for this axis
        public string RS485Ip { get; set; } = "127.0.0.1";
        public int DriverId { get; set; } = 1;

        // Pitch-specific tuning
        public double AirspeedAdditionalTorquePercent { get; set; } = 10.0; // 1..100
        public double StallAdditionalTorquePercent { get; set; } = 10.0; // 1..100

        public double ConvertTorqueDisplayToActual(double display)
        {
            var clamped = Math.Max(0, Math.Min(TorqueDisplayMax, display));
            return (clamped / TorqueDisplayMax) * TorqueActualMax;
        }
    }

    // Dynamic axis state and basic calculations.
    public class Axis
    {
        public string Name { get; }
        public AxisSettings Settings { get; }

        // Dynamic values
        public double EncoderPosition { get; set; } // raw encoder units
        public double AxisPosition { get; set; } // simulator axis position (degrees)
        public bool AutopilotOn { get; set; }
        public double AutopilotTarget { get; set; }
        public bool HydraulicsOn { get; set; }

        public double TorqueTarget { get; set; }

        public Axis(string name, AxisSettings? settings = null)
        {
            Name = name;
            Settings = settings ?? new AxisSettings();
        }

        // Update calculation of torque target based on encoder position and settings.
        public void RecalculateTorqueTarget()
        {
            // If AutopilotOn (test mode), use fixed MovingTorque (actual motor value 0-300)
            if (AutopilotOn)
            {
                TorqueTarget = Settings.MovingTorque;
                return;
            }

            // Normalize encoder position relative to calibrated center -> -1 .. 1
            var center = Settings.CenterPosition;
            var range = Math.Max(1e-6, Math.Max(Math.Abs(Settings.FullLeftPosition), Math.Abs(Settings.FullRightPosition)));
            var pos = (EncoderPosition - center) / range; // approx -1..1

            // Map pos 0..1 magnitude from center
            var magnitude = Math.Min(1.0, Math.Abs(pos));

            // Interpolate torque display between min and max display settings
            var minDisp = Settings.MinTorqueDisplay;
            var maxDisp = Settings.MaxTorqueDisplay;
            var displayTorque = minDisp + (maxDisp - minDisp) * magnitude;

            // Convert to actual torque scale
            var actualTorque = Settings.ConvertTorqueDisplayToActual(displayTorque);

            // If hydraulics are off, use specified hydraulic off torque (converted)
            if (!HydraulicsOn)
            {
                actualTorque = Settings.ConvertTorqueDisplayToActual(Settings.HydraulicOffTorqueDisplay);
            }

            TorqueTarget = actualTorque;
        }

        // Placeholder: called periodically to update from simulator data (TODO implement real data sources)
        public void UpdateFromSimulator(double axisPosition, bool autopilotOn, double autopilotTarget, bool hydraulicsOn)
        {
            AxisPosition = axisPosition;
            AutopilotOn = autopilotOn;
            AutopilotTarget = autopilotTarget;
            HydraulicsOn = hydraulicsOn;
            RecalculateTorqueTarget();
        }

        // Placeholder: update from encoder reading (TODO implement real encoder read)
        public void UpdateFromEncoder(double encoder)
        {
            EncoderPosition = encoder;
            RecalculateTorqueTarget();
        }
    }
}
