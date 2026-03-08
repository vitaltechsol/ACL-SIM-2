using System;
using System.Threading.Tasks;
using ACL_SIM_2.ViewModels;
using EasyModbus;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Manages axis torque and position control.
    /// Calculates torque based on encoder position relative to center.
    /// Torque increases as encoder moves away from center (min at center, max at limits).
    /// </summary>
    public class AxisManager : IDisposable
    {
        private readonly AxisViewModel _axisVm;
        private readonly AxisTorqueControl? _torqueControl;
        private readonly string _name;
        private bool _isDisposed;

        public AxisManager(string name, AxisViewModel axisVm, ModbusClient? modbusClient = null)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));

            // Create torque control if ModbusClient is provided
            if (modbusClient != null && !string.IsNullOrWhiteSpace(axisVm.Underlying.Settings.RS485Ip))
            {
                try
                {
                    _torqueControl = new AxisTorqueControl(
                        enabled: axisVm.Enabled,
                        driverId: axisVm.Underlying.Settings.DriverId,
                        sharedClient: modbusClient
                    );
                }
                catch
                {
                    // Torque control creation failed, will operate without it
                }
            }

            // Subscribe to encoder position changes
            _axisVm.PropertyChanged += OnAxisPropertyChanged;
        }

        private void OnAxisPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AxisViewModel.EncoderPosition))
            {
                // Calculate and update torque asynchronously
                _ = UpdateTorqueAsync();
            }
        }

        /// <summary>
        /// Calculates torque based on encoder position relative to center.
        /// Torque increases as distance from center increases.
        /// Min torque at center (0), max torque at either limit.
        /// </summary>
        private async Task UpdateTorqueAsync()
        {
            if (_isDisposed || _torqueControl == null) return;

            try
            {
                await Task.Run(() =>
                {
                    var settings = _axisVm.Underlying.Settings;
                    var encoderPosition = _axisVm.EncoderPosition;

                    // Calculate offset from center (absolute encoder position)
                    var centerPosition = settings.CenterPosition;
                    var offsetFromCenter = encoderPosition - centerPosition;

                    // Calculate normalized distance based on direction
                    double normalizedDistance;

                    if (offsetFromCenter >= 0)
                    {
                        // Moving in positive direction
                        var maxPositive = Math.Max(1e-6, Math.Abs(settings.MaxPosition));
                        normalizedDistance = Math.Min(1.0, Math.Abs(offsetFromCenter) / maxPositive);
                    }
                    else
                    {
                        // Moving in negative direction
                        var maxNegative = Math.Max(1e-6, Math.Abs(settings.MinPosition));
                        normalizedDistance = Math.Min(1.0, Math.Abs(offsetFromCenter) / maxNegative);
                    }

                    // Get torque range from settings (display scale 0-100)
                    var minTorqueDisplay = settings.MinTorqueDisplay;
                    var maxTorqueDisplay = settings.MaxTorqueDisplay;

                    // Calculate display torque: min at center, max at limits (0-100 scale)
                    var targetTorqueDisplay = minTorqueDisplay + (normalizedDistance * (maxTorqueDisplay - minTorqueDisplay));

                    // Clamp to valid display range
                    targetTorqueDisplay = Math.Max(0, Math.Min(maxTorqueDisplay, targetTorqueDisplay));

                    // Update ViewModel with display value for UI
                    _axisVm.CurrentTorque = targetTorqueDisplay;

                    // Convert to actual motor scale (0-300) and send to motor
                    try
                    {
                        var targetTorqueActual = settings.ConvertTorqueDisplayToActual(targetTorqueDisplay);
                        var torqueInt = (int)Math.Round(targetTorqueActual);

                        // Send to appropriate register based on direction
                        if (offsetFromCenter <= 0)
                        {
                            // Positive offset: use forward register (register 8)
                            _torqueControl.SetTorqueForward(torqueInt);
                        }
                        else
                        {
                            // Negative offset: use backward register (register 9)
                            _torqueControl.SetTorqueBackward(torqueInt);
                        }
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });
            }
            catch
            {
                // Calculation failed, ignore
            }
        }

        /// <summary>
        /// TODO: Manage axis position control
        /// Will handle position commands and feedback in future implementation.
        /// </summary>
        public void SetTargetPosition(double position)
        {
            // TODO: Implement position control logic
            throw new NotImplementedException("Position control will be implemented in future update");
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _axisVm.PropertyChanged -= OnAxisPropertyChanged;
            }
            catch { }

            try
            {
                _torqueControl?.Dispose();
            }
            catch { }

            GC.SuppressFinalize(this);
        }
    }
}
