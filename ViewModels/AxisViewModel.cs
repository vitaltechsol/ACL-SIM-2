using System;
using System.ComponentModel;
using System.Windows.Media;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.ViewModels
{
    public partial class AxisViewModel : INotifyPropertyChanged
    {
        public enum EncoderConnectionState
        {
            NotConfigured,
            Connected,
            Failed
        }

        private readonly Axis _axis;
        private EncoderConnectionState _connectionState = EncoderConnectionState.NotConfigured;

        public AxisViewModel(Axis axis)
        {
            _axis = axis;
            // initialize enabled from persisted settings if present
            try { _enabled = _axis.Settings.Enabled; } catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(_axis.Settings.RS485Ip))
                    _connectionState = EncoderConnectionState.Failed; // will be updated by EncoderManager
            }
            catch { }
        }

        public string Name => _axis.Name;

        public EncoderConnectionState ConnectionState
        {
            get => _connectionState;
            set
            {
                if (_connectionState == value) return;
                _connectionState = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionBrush)));
            }
        }

        // UI-friendly brush for binding to an Ellipse.Fill
        public Brush ConnectionBrush
        {
            get
            {
                return _connectionState switch
                {
                    EncoderConnectionState.Connected => Brushes.Green,
                    EncoderConnectionState.Failed => Brushes.Red,
                    _ => Brushes.Gray,
                };
            }
        }

        public double EncoderPosition
        {
            get => _axis.EncoderPosition;
            set
            {
                if (_axis.EncoderPosition == value) return;
                _axis.UpdateFromEncoder(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncoderPosition)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncoderNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Torque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncoderPercentage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HydraulicsOn)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutopilotOn)));
            }
        }

        public double AxisPosition => _axis.AxisPosition;

        public double Torque => _axis.TorqueTarget;

        private double _currentTorque;
        /// <summary>
        /// Current torque value managed by AxisManager (calculated from encoder position).
        /// </summary>
        public double CurrentTorque
        {
            get => _currentTorque;
            set
            {
                if (Math.Abs(_currentTorque - value) < 1e-6) return;
                _currentTorque = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTorque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalizedFromCurrent)));
            }
        }

        /// <summary>
        /// Encoder position as percentage from center (0-100%), using direction-aware normalization.
        /// </summary>
        public double EncoderPercentage
        {
            get
            {
                // Relative position: 0 at center, negative=left, positive=right
                var relativePos = _axis.EncoderPosition - _axis.EncoderCenterOffset;

                double normalizedDistance;
                if (relativePos >= 0)
                {
                    normalizedDistance = System.Math.Min(1.0, relativePos / System.Math.Max(1e-6, _axis.Settings.FullRightPosition));
                }
                else
                {
                    normalizedDistance = System.Math.Min(1.0, System.Math.Abs(relativePos) / System.Math.Max(1e-6, System.Math.Abs(_axis.Settings.FullLeftPosition)));
                }

                return normalizedDistance * 100.0; // Convert to percentage
            }
        }

        // UI-friendly status properties
        public bool IsActive => _axis.TorqueTarget > 0.0;

        public bool IsReversed => _axis.Settings.ReversedMotor;

        public bool HydraulicsOn => _axis.HydraulicsOn;

        public bool AutopilotOn => _axis.AutopilotOn;

        // Normalized values for UI (0..1)
        public double EncoderNormalized
        {
            get
            {
                // Map encoder to 0-1 range using relative full left/right positions
                var relativePos = _axis.EncoderPosition - _axis.EncoderCenterOffset;
                var range = System.Math.Max(1e-6, _axis.Settings.FullRightPosition - _axis.Settings.FullLeftPosition);
                var normalized = (relativePos - _axis.Settings.FullLeftPosition) / range;
                return System.Math.Max(0.0, System.Math.Min(1.0, normalized)); // clamp to 0-1
            }
            set
            {
                // Convert normalized 0-1 value back to absolute encoder position
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));
                var range = _axis.Settings.FullRightPosition - _axis.Settings.FullLeftPosition;
                var relativePos = _axis.Settings.FullLeftPosition + (normalized * range);
                // Add offset to get raw encoder position
                EncoderPosition = relativePos + _axis.EncoderCenterOffset;
            }
        }

        public double TorqueNormalized
        {
            get
            {
                const double max = AxisSettings.TorqueActualMax;
                return System.Math.Max(0, System.Math.Min(1.0, _axis.TorqueTarget / max));
            }
            set
            {
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));
                const double max = AxisSettings.TorqueActualMax;

                var newTorque = normalized * max;
                if (System.Math.Abs(_axis.TorqueTarget - newTorque) < 1e-9) return;

                _axis.TorqueTarget = newTorque;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Torque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        /// <summary>
        /// Normalized torque value (0-300) based on CurrentTorque for UI display.
        /// CurrentTorque is in display scale (0-100), so we normalize it to 0-1.
        /// </summary>
        public double TorqueNormalizedFromCurrent
        {
            get
            {
                var maxDisplay = _axis.Settings.MaxTorquePercent;
                if (maxDisplay <= 0) return 0;
                return System.Math.Max(0, System.Math.Min(1.0, _currentTorque / maxDisplay));
            }
        }

        public Axis Underlying => _axis;

        /// <summary>
        /// Notify UI that a property has changed. For internal use and by related ViewModels.
        /// </summary>
        public void NotifyPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}