using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ACL_SIM_2.Models;

using ACL_SIM_2.Services;

namespace ACL_SIM_2.ViewModels
{
    public partial class AxisViewModel : INotifyPropertyChanged
    {
        private readonly Axis _axis;
        private CancellationTokenSource? _encoderCts;
        private Task? _encoderTask;

        public AxisViewModel(Axis axis)
        {
            _axis = axis;
            // initialize enabled from persisted settings if present
            try { _enabled = _axis.Settings.Enabled; } catch { }
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Torque)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalized)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public double AxisPosition => _axis.AxisPosition;

        public double Torque => _axis.TorqueTarget;

        // UI-friendly status properties
        public bool IsActive => _axis.TorqueTarget > 0.0;

        public bool HydraulicsOn => _axis.HydraulicsOn;

        public bool AutopilotOn => _axis.AutopilotOn;

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public Axis Underlying => _axis;

        /// <summary>
        /// Attach an AxisEncoder to this view model. The VM will poll the encoder asynchronously
        /// and update its EncoderPosition property so the UI updates in real-time.
        /// </summary>
        public void AttachEncoder(AxisEncoder encoder, int pollMs = 100)
        {
            DetachEncoder();
            if (encoder == null) return;

            _encoderCts = new CancellationTokenSource();
            var ct = _encoderCts.Token;
            _encoderTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var val = await encoder.GetValueAsync().ConfigureAwait(false);
                        // marshal to UI by setting the EncoderPosition property (which raises PropertyChanged)
                        // Use Task.Run to ensure we don't block the encoder loop. EncoderPosition setter uses model update.
                        EncoderPosition = val;
                    }
                    catch
                    {
                        // ignore per-iteration errors
                    }

                    try { await Task.Delay(Math.Max(10, pollMs), ct).ConfigureAwait(false); } catch { break; }
                }
            }, ct);
        }

        public void DetachEncoder()
        {
            try
            {
                _encoderCts?.Cancel();
            }
            catch { }
            try { _encoderTask?.Wait(200); } catch { }
            _encoderTask = null;
            _encoderCts?.Dispose();
            _encoderCts = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}   
