using System.ComponentModel;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.ViewModels
{
    public class AxisViewModel : INotifyPropertyChanged
    {
        private readonly Axis _axis;

        public AxisViewModel(Axis axis)
        {
            _axis = axis;
        }

        public string Name => _axis.Name;

        public double EncoderPosition
        {
            get => _axis.EncoderPosition;
            set
            {
                if (_axis.EncoderPosition == value) return;
                _axis.UpdateFromEncoder(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncoderPosition)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncoderNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
            }
        }

        public double AxisPosition => _axis.AxisPosition;

        public double Torque => _axis.TorqueTarget;

        // Normalized values for UI (0..1)
        public double EncoderNormalized
        {
            get
            {
                var center = 0.0;
                var range = System.Math.Max(1e-6, System.Math.Max(System.Math.Abs(_axis.Settings.MinPosition - center), System.Math.Abs(_axis.Settings.MaxPosition - center)));
                var v = (_axis.EncoderPosition - center) / range; // approx -1..1
                return (v + 1) / 2.0;
            }
            set
            {
                // Clamp input to 0..1 to avoid invalid values
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));

                var center = 0.0;
                var range = System.Math.Max(1e-6, System.Math.Max(System.Math.Abs(_axis.Settings.MinPosition - center), System.Math.Abs(_axis.Settings.MaxPosition - center)));

                // map 0..1 -> -1..1
                var v = (normalized * 2.0) - 1.0;

                // convert back to encoder units and assign using existing setter
                var encoder = (v * range) + center;
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
                // Clamp input to 0..1 to avoid invalid values
                var normalized = System.Math.Max(0.0, System.Math.Min(1.0, value));

                var max = _axis.Settings.TorqueActualMax;
                if (max <= 0)
                {
                    // Nothing to set if max invalid; still notify UI in case it expects update
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                    return;
                }

                var newTorque = normalized * max;
                if (System.Math.Abs(_axis.TorqueTarget - newTorque) < 1e-9) return;

                _axis.TorqueTarget = newTorque;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Torque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
            }
        }

        public Axis Underlying => _axis;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
