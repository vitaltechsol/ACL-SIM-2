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
            }
        }

        /// <summary>
        /// Encoder position as percentage from center (0-100%), using direction-aware normalization.
        /// </summary>
        public double EncoderPercentage
        {
            get
            {
                var centerPosition = _axis.Settings.CenterPosition;
                var offsetFromCenter = _axis.EncoderPosition - centerPosition;

                double normalizedDistance;
                if (offsetFromCenter >= 0)
                {
                    // Positive (right): use FullRightPosition denominator
                    var maxPositive = System.Math.Max(1e-6, System.Math.Abs(_axis.Settings.FullRightPosition));
                    normalizedDistance = System.Math.Min(1.0, System.Math.Abs(offsetFromCenter) / maxPositive);
                }
                else
                {
                    // Negative (left): use FullLeftPosition denominator
                    var maxNegative = System.Math.Max(1e-6, System.Math.Abs(_axis.Settings.FullLeftPosition));
                    normalizedDistance = System.Math.Min(1.0, System.Math.Abs(offsetFromCenter) / maxNegative);
                }

                return normalizedDistance * 100.0; // Convert to percentage
            }
        }

        // UI-friendly status properties
        public bool IsActive => _axis.TorqueTarget > 0.0;

        public bool HydraulicsOn => _axis.HydraulicsOn;

        public bool AutopilotOn => _axis.AutopilotOn;

        // Normalized values for UI (0..1)
        public double EncoderNormalized
        {
            get
            {
                // Map encoder position to 0-1 range using actual full left/right positions (supports asymmetric ranges)
                var actualLeft = _axis.Settings.CenterPosition + _axis.Settings.FullLeftPosition;
                var actualRight = _axis.Settings.CenterPosition + _axis.Settings.FullRightPosition;
                var range = System.Math.Max(1e-6, actualRight - actualLeft);
                var normalized = (_axis.EncoderPosition - actualLeft) / range;
                return System.Math.Max(0.0, System.Math.Min(1.0, normalized)); // clamp to 0-1
            }
            set
            {
                // Convert normalized 0-1 value back to absolute encoder position
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));
                var actualLeft = _axis.Settings.CenterPosition + _axis.Settings.FullLeftPosition;
                var actualRight = _axis.Settings.CenterPosition + _axis.Settings.FullRightPosition;
                var range = actualRight - actualLeft;
                var encoder = actualLeft + (normalized * range);
                EncoderPosition = encoder;
            }
        }

        public double TorqueNormalized
        {
            get
            {
                var max = _axis.Settings.TorqueActualMax;
                if (max <= 0) return 0;
                return System.Math.Max(0, System.Math.Min(1.0, _axis.TorqueTarget / max));
            }
            set
            {
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));
                var max = _axis.Settings.TorqueActualMax;
                if (max <= 0)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                    return;
                }

                var newTorque = normalized * max;
                if (System.Math.Abs(_axis.TorqueTarget - newTorque) < 1e-9) return;

                _axis.TorqueTarget = newTorque;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Torque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public Axis Underlying => _axis;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}