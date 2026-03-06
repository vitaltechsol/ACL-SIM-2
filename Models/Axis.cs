using System;

namespace ACL_SIM_2.Models
{
    // Settings that can be persisted and adjusted by the user.
    public class AxisSettings
    {
        // Range sliders are presented 0..100 in UI. Actual motor uses different ranges (example torque 0..300).
        public double MinPosition { get; set; } = -30.0; // degrees relative to center
        public double MaxPosition { get; set; } = 30.0;
        public double MinTorqueDisplay { get; set; } = 0; // 0..100
        public double MaxTorqueDisplay { get; set; } = 100; // 0..100
        public bool ReversedMotor { get; set; } = false;
        public double MovingTorqueDisplay { get; set; } = 10; // 0..100
        public double SelfCenteringSpeed { get; set; } = 50; // 1..100
        public double Dampening { get; set; } = 10; // 0..100
        public double HydraulicOffTorqueDisplay { get; set; } = 80; // 0..100
        public double AutopilotOverridePercent { get; set; } = 5; // 1..100 (only meaningful for pitch/roll)

        // Advanced motion tweak settings
        public int OutputIntervalMs { get; set; } = 10;
        public int SpeedRateLimitPercent { get; set; } = 50;
        public int InputIntervalMs { get; set; } = 10;
        public double TargetFilterAlpha { get; set; } = 0.25;
        public double DeadbandDegrees { get; set; } = 0.02;

        // Conversion constants (example). These map display [0..100] to actual values used by motors.
        public double TorqueDisplayMax { get; set; } = 100.0;
        public double TorqueActualMax { get; set; } = 300.0;

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
        public double EncoderPosition { get; set; } // raw encoder units or degrees
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
            // Normalize encoder position relative to center -> -1 .. 1
            var center = 0.0;
            var range = Math.Max(1e-6, Math.Max(Math.Abs(Settings.MinPosition - center), Math.Abs(Settings.MaxPosition - center)));
            var pos = (EncoderPosition - center) / range; // approx -1..1
            if (Settings.ReversedMotor) pos = -pos;

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
