using System;

namespace ACL_SIM_2.Models
{
    // Settings that can be persisted and adjusted by the user.
    public class AxisSettings
    {
        // Encoder position calibration
        // CenterPosition is always 0 — the raw encoder value at center is not persisted.
        // EncoderCenterOffset is initialized to 0 on startup and updated at runtime after centering.
        public double CenterPosition { get; set; } = 0.0;
        // FullLeftPosition / FullRightPosition: relative distances from center.
        // FullLeftPosition is always negative, FullRightPosition is always positive.
        // Range sliders are presented 0..100 in UI. Actual motor uses different ranges (example torque 0..300).
        public double FullLeftPosition { get; set; } = -2000; // relative distance from center (negative)
        public double FullRightPosition { get; set; } = 2000;  // relative distance from center (positive)
        public double MinTorquePercent { get; set; } = 5; // 0..100
        public double MaxTorquePercent { get; set; } = 30; // 0..100
        public bool ReversedMotor { get; set; } = false;
        public double MovingTorquePercentage { get; set; } = 20; // 0..100 (display value for UI slider)
        public double SelfCenteringSpeed { get; set; } = 50; // 1..100
        public double Dampening { get; set; } = 10; // 0..100
        public double HydraulicOffTorquePercent { get; set; } = 50; // 0..100
        public double AutopilotOverridePercent { get; set; } = 50; // 1..100 (only meaningful for pitch/roll)

        // Advanced motion tweak settings - used by Axis
        //
        //
        // service

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
        /// 0.0 = maximum smoothing (very slow response), 1.0 = no smoothing (instant response).
        /// Default 0.2 provides 80% smoothing for smooth autopilot tracking.
        /// For manual slider control: Use higher values (0.7-1.0) for immediate response
        /// For autopilot/simulator tracking: Use lower values (0.1-0.3) for smooth motion
        /// </summary>
        public double TargetFilterAlpha { get; set; } = 0.2;

        /// <summary>
        /// Minimum time between motor commands (milliseconds). Prevents overwhelming the servo with commands.
        /// When tracking targets rapidly, commands are throttled to this interval.
        /// Lower = more responsive, Higher = smoother motion with less command churn
        /// Default: 100ms for smooth autopilot tracking (matches well with 250ms autopilot loop)
        /// </summary>
        public int MinMotorCommandIntervalMs { get; set; } = 100;

        // Servo motor position control settings

        /// <summary>
        /// Motor speed in RPM for position movements.
        /// Setup UI exposes a low-speed tuning range of 1-15 RPM for smoother motion.
        /// </summary>
        public int MotorSpeedRpm { get; set; } = 8;

        /// <summary>
        /// Motor acceleration mode (not used by servo — always AccelMode.None; software RPM ramp controls acceleration).
        /// </summary>
        public int MotorAccelMode { get; set; } = 0;

        /// <summary>
        /// Software acceleration ramp time (ms): time to ramp from 0 to max RPM.
        /// Default 1000ms for visible aircraft-like smooth acceleration.
        /// </summary>
        public int MotorAccelParam1Ms { get; set; } = 1000;

        /// <summary>
        /// Curve smoothness: cosine transition width within the software ramp (ms).
        /// 0 = pure linear ramp. Must be ≤ Param1/2. Default 300ms for a balanced S-curve.
        /// </summary>
        public int MotorAccelParam2Ms { get; set; } = 300;

        // Conversion constants (example). These map display [0..100] to actual values used by motors.
        public const double TorqueActualMax = 300.0;
        public const double CenteringSpeedActualMax = 250;
        public const double DampeningActualMin = 5.0;
        public const double DampeningActualMax = 1000.0;

        public static int ConvertCenteringSpeedToActual(double display)
        {
            var clamped = Math.Max(0, Math.Min(100.0, display));
            return (int)Math.Round(clamped / 100.0 * CenteringSpeedActualMax);
        }

        public static int ConvertDampeningToActual(double display)
        {
            var clamped = Math.Max(0, Math.Min(100.0, display));
            return (int)Math.Round(DampeningActualMin + (clamped / 100.0 * (DampeningActualMax - DampeningActualMin)));
        }
        // Persisted flag whether this axis is enabled for user interaction
        public bool Enabled { get; set; } = false;

        // Communication / driver settings for this axis
        public string RS485Ip { get; set; } = "";
        public int DriverId { get; set; } = 1;

        // Pitch-specific tuning
        public double AirspeedAdditionalTorquePercent { get; set; } = 10.0; // 1..50
        public double StallAdditionalTorquePercent { get; set; } = 30.0; // 30..120

        public double ConvertTorqueDisplayToActual(double display)
        {
            const double TorqueDisplayMax = 100.0;
            var clamped = Math.Max(0, Math.Min(TorqueDisplayMax, display));
            return (clamped / TorqueDisplayMax) * TorqueActualMax;
        }

        public AxisSettings Clone()
        {
            return new AxisSettings().CopyFrom(this);
        }

        public AxisSettings CopyFrom(AxisSettings other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            CenterPosition = other.CenterPosition;
            FullLeftPosition = other.FullLeftPosition;
            FullRightPosition = other.FullRightPosition;
            MinTorquePercent = other.MinTorquePercent;
            MaxTorquePercent = other.MaxTorquePercent;
            ReversedMotor = other.ReversedMotor;
            MovingTorquePercentage = other.MovingTorquePercentage;
            SelfCenteringSpeed = other.SelfCenteringSpeed;
            Dampening = other.Dampening;
            HydraulicOffTorquePercent = other.HydraulicOffTorquePercent;
            AutopilotOverridePercent = other.AutopilotOverridePercent;
            OutputIntervalMs = other.OutputIntervalMs;
            SpeedRateLimitPercent = other.SpeedRateLimitPercent;
            InputIntervalMs = other.InputIntervalMs;
            TargetFilterAlpha = other.TargetFilterAlpha;
            MinMotorCommandIntervalMs = other.MinMotorCommandIntervalMs;
            MotorSpeedRpm = other.MotorSpeedRpm;
            MotorAccelMode = other.MotorAccelMode;
            MotorAccelParam1Ms = other.MotorAccelParam1Ms;
            MotorAccelParam2Ms = other.MotorAccelParam2Ms;
            Enabled = other.Enabled;
            RS485Ip = other.RS485Ip;
            DriverId = other.DriverId;
            AirspeedAdditionalTorquePercent = other.AirspeedAdditionalTorquePercent;
            StallAdditionalTorquePercent = other.StallAdditionalTorquePercent;

            return this;
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
        public bool CalibrationMode { get; set; }

        public double TorqueTarget { get; set; }
        public double AirspeedIas { get; set; }
        public double AirspeedAdditionalTorqueAppliedPercent { get; set; }

        /// <summary>
        /// Indicates the motor is actively being moved (centering, position test, or autopilot movement).
        /// When true, <see cref="AxisSettings.MovingTorquePercentage"/> is used for torque.
        /// </summary>
        public bool MotorIsMoving { get; set; }

        public bool IsTrimming { get; set; }

        /// <summary>
        /// Encoder offset applied so that (RawEncoder - EncoderCenterOffset) == 0 when the axis is at center.
        /// Initialized to 0 on startup. Updated to the actual raw encoder value after motor centering
        /// completes (via <see cref="Services.AxisManager.CenterToProSimPositionAsync"/>).
        /// </summary>
        public double EncoderCenterOffset { get; set; } = 0.0;

        public Axis(string name, AxisSettings? settings = null)
        {
            Name = name;
            Settings = settings ?? new AxisSettings();
            HydraulicsOn = true; // Default to hydraulics on

            // Offset starts at 0; physical centering at startup will update it
            // to the actual raw encoder value at center.
            EncoderCenterOffset = Settings.CenterPosition;
        }

        public double CalculateEncoderBaseTorqueDisplayPercent()
        {
            // Relative position: 0 at center, negative=left, positive=right
            var relativePos = EncoderPosition - EncoderCenterOffset;

            // Direction-aware normalization using relative distances stored in settings
            double magnitude;
            if (relativePos >= 0)
            {
                magnitude = Math.Min(1.0, relativePos / Math.Max(1e-6, Settings.FullRightPosition));
            }
            else
            {
                magnitude = Math.Min(1.0, Math.Abs(relativePos) / Math.Max(1e-6, Math.Abs(Settings.FullLeftPosition)));
            }

            // Interpolate torque display between min and max display settings
            var minDisp = Settings.MinTorquePercent;
            var maxDisp = Settings.MaxTorquePercent;
            return minDisp + (maxDisp - minDisp) * magnitude;
        }

        public double CalculateAirspeedAdditionalTorqueDisplayPercent()
        {
            if (!string.Equals(Name, "Pitch", StringComparison.OrdinalIgnoreCase))
            {
                return 0.0;
            }

            var clampedSpeed = Math.Max(0.0, Math.Min(500.0, AirspeedIas));
            var configuredAdditionalPercent = Math.Max(1.0, Math.Min(50.0, Settings.AirspeedAdditionalTorquePercent));
            return (clampedSpeed / 500.0) * configuredAdditionalPercent;
        }

        public double CalculateTotalEncoderTorqueDisplayPercent()
        {
            var baseDisplayTorque = CalculateEncoderBaseTorqueDisplayPercent();
            var additionalDisplayTorque = CalculateAirspeedAdditionalTorqueDisplayPercent();
            return Math.Max(0.0, Math.Min(100.0, baseDisplayTorque + additionalDisplayTorque));
        }

        // Update calculation of torque target based on encoder position and settings.
        public void RecalculateTorqueTarget()
        {
            // If in calibration mode, set torque to 0 and ignore all other calculations
            if (CalibrationMode)
            {
                AirspeedAdditionalTorqueAppliedPercent = 0.0;
                TorqueTarget = 0.0;
                return;
            }

            // If MotorIsMoving or IsTrimming, use MovingTorquePercentage
            if (MotorIsMoving || IsTrimming)
            {
                AirspeedAdditionalTorqueAppliedPercent = 0.0;
                TorqueTarget = Settings.ConvertTorqueDisplayToActual(Settings.MovingTorquePercentage);
                return;
            }

            // If hydraulics are off, use specified hydraulic off torque (fixed value)
            if (!HydraulicsOn)
            {
                AirspeedAdditionalTorqueAppliedPercent = 0.0;
                TorqueTarget = Settings.ConvertTorqueDisplayToActual(Settings.HydraulicOffTorquePercent);
                return;
            }

            AirspeedAdditionalTorqueAppliedPercent = CalculateAirspeedAdditionalTorqueDisplayPercent();
            var displayTorque = CalculateTotalEncoderTorqueDisplayPercent();

            // Convert to actual torque scale
            TorqueTarget = Settings.ConvertTorqueDisplayToActual(displayTorque);
        }

        // Update from encoder reading
        public void UpdateFromEncoder(double encoder)
        {
            EncoderPosition = encoder;
            RecalculateTorqueTarget();
        }
    }
}

