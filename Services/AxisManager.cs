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
        private readonly AxisMovement _axisMovement;
        private readonly ProSimManager? _proSimManager;
        private readonly string _name;
        private bool _isDisposed;

        public AxisManager(string name, AxisViewModel axisVm, ModbusClient? modbusClient = null, object? modbusLock = null, ProSimManager? proSimManager = null)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            _proSimManager = proSimManager;

            // Create torque control if ModbusClient is provided
            if (modbusClient != null && !string.IsNullOrWhiteSpace(axisVm.Underlying.Settings.RS485Ip))
            {
                try
                {
                    if (modbusLock != null)
                    {
                        _torqueControl = new AxisTorqueControl(
                            enabled: axisVm.Enabled,
                            driverId: axisVm.Underlying.Settings.DriverId,
                            sharedClient: modbusClient,
                            modbusLock: modbusLock
                        );
                    }
                }
                catch
                {
                    // Torque control creation failed, will operate without it
                }
            }

            // Create axis movement controller with ModbusClient, TorqueControl and shared lock for servo control
            _axisMovement = new AxisMovement(axisVm.Underlying, modbusClient, _torqueControl, modbusLock);

            // Subscribe to encoder position changes
            _axisVm.PropertyChanged += OnAxisPropertyChanged;

            // Subscribe to ProSim hydraulics availability changes
            if (_proSimManager != null)
            {
                _proSimManager.OnHydraulicsAvailableChanged += ProSimManager_OnHydraulicsAvailableChanged;
            }
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
        /// Handles changes to ProSim hydraulics availability.
        /// Updates HydraulicsOn state for this axis and recalculates torque immediately.
        /// </summary>
        private void ProSimManager_OnHydraulicsAvailableChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            var hydraulicsAvailable = e.Value != 0;

            // Don't change HydraulicsOn if we're in calibration mode
            if (!_axisVm.Underlying.CalibrationMode)
            {
                // Update the underlying axis model
                _axisVm.Underlying.HydraulicsOn = hydraulicsAvailable;

                // Notify the ViewModel to update UI bindings
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.HydraulicsOn));
                });

                // Trigger immediate torque recalculation with new hydraulics state
                _ = UpdateTorqueAsync();
            }
        }

        /// <summary>
        /// Calculates torque based on encoder position relative to center.
        /// Torque increases as distance from center increases.
        /// Min torque at center (0), max torque at either limit.
        /// When AutopilotOn (test mode), uses MovingTorqueDisplay for both directions.
        /// When HydraulicsOn is false, uses HydraulicOffTorquePercent for both directions.
        /// </summary>
        private async Task UpdateTorqueAsync()
        {
            if (_isDisposed || _torqueControl == null) return;

            // If CalibrationMode is active, set torque to 0 for both directions
            if (_axisVm.Underlying.CalibrationMode)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        _torqueControl.SetTorqueBoth(0, 0);
                        _axisVm.CurrentTorque = 0.0;
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });

                return;
            }

            // If AutopilotOn (position test mode), use fixed MovingTorque for both directions
            if (_axisVm.Underlying.AutopilotOn)
            {
                await Task.Run(() =>
                {
                    var settings = _axisVm.Underlying.Settings;

                    // Convert MovingTorquePercentage (0-100) to actual motor torque (0-300)
                    var torqueActual = settings.ConvertTorqueDisplayToActual(settings.MovingTorquePercentage);
                    var torqueInt = (int)Math.Round(torqueActual);

                    // Clamp to valid range (0-300)
                    torqueInt = Math.Max(0, Math.Min(300, torqueInt));

                    // Set same torque for both directions during test mode
                    try
                    {
                        _torqueControl.SetTorqueBoth(torqueInt, torqueInt);

                        // Update ViewModel display value
                        _axisVm.CurrentTorque = settings.MovingTorquePercentage;
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });

                return;
            }

            // If HydraulicsOn is false, use fixed HydraulicOffTorquePercent for both directions
            if (!_axisVm.Underlying.HydraulicsOn)
            {
                await Task.Run(() =>
                {
                    var settings = _axisVm.Underlying.Settings;

                    // Convert HydraulicOffTorquePercent (0-100) to actual motor torque (0-300)
                    var torqueActual = settings.ConvertTorqueDisplayToActual(settings.HydraulicOffTorquePercent);
                    var torqueInt = (int)Math.Round(torqueActual);

                    // Clamp to valid range (0-300)
                    torqueInt = Math.Max(0, Math.Min(300, torqueInt));

                    // Set same torque for both directions when hydraulics are off
                    try
                    {
                        _torqueControl.SetTorqueBoth(torqueInt, torqueInt);

                        // Update ViewModel display value
                        _axisVm.CurrentTorque = settings.HydraulicOffTorquePercent;
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });

                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    var settings = _axisVm.Underlying.Settings;
                    var encoderPosition = _axisVm.EncoderPosition;

                    // Normalize encoder reading by subtracting the offset
                    var normalizedEncoder = encoderPosition - _axisVm.Underlying.EncoderCenterOffset;
                    var centerPosition = settings.CenterPosition;
                    var offsetFromCenter = normalizedEncoder - centerPosition;

                    // Calculate normalized distance based on direction
                    double normalizedDistance;

                    if (offsetFromCenter >= 0)
                    {
                        // Moving in positive (right) direction: distance from center to full right
                        var maxPositive = Math.Max(1e-6, Math.Abs(settings.FullRightPosition - settings.CenterPosition));
                        normalizedDistance = Math.Min(1.0, Math.Abs(offsetFromCenter) / maxPositive);
                    }
                    else
                    {
                        // Moving in negative (left) direction: distance from center to full left
                        var maxNegative = Math.Max(1e-6, Math.Abs(settings.CenterPosition - settings.FullLeftPosition));
                        normalizedDistance = Math.Min(1.0, Math.Abs(offsetFromCenter) / maxNegative);
                    }

                    // Get torque range from settings (display scale 0-100)
                    var minTorqueDisplay = settings.MinTorquePercent;
                    var maxTorqueDisplay = settings.MaxTorquePercent;

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
                        if (offsetFromCenter >= 0)
                        {
                            // Positive offset (right direction): use right register (register 8)
                            _torqueControl.SetTorqueRight(torqueInt);
                        }
                        else
                        {
                            // Negative offset (left direction): use left register (register 9)
                            _torqueControl.SetTorqueLeft(torqueInt);
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
        /// Move axis to a target position using advanced motion control.
        /// </summary>
        /// <param name="targetPercent">
        /// Target position as percentage:
        /// -100 = Full left position
        /// 0 = Center position
        /// +100 = Full right position
        /// </param>
        public void GoToPosition(double targetPercent)
        {
            _axisMovement.GoToPosition(targetPercent);
        }

        /// <summary>
        /// Gets the axis movement controller for direct access to advanced features.
        /// </summary>
        public AxisMovement Movement => _axisMovement;

        /// <summary>
        /// Sends a centering speed value to the motor via Modbus register 51.
        /// </summary>
        /// <param name="speed">Speed value (0-3000)</param>
        public void SendCenteringSpeed(int speed)
        {
            if (_isDisposed || _torqueControl == null) return;
            try { _torqueControl.SetCenteringSpeed(speed); }
            catch { /* Motor write failed, continue */ }
        }

        /// <summary>
        /// Sends a dampening value to the motor via Modbus register 192.
        /// </summary>
        /// <param name="dampening">Dampening value (5-2000)</param>
        public void SendDampening(int dampening)
        {
            if (_isDisposed || _torqueControl == null) return;
            try { _torqueControl.SetDampening(dampening); }
            catch { /* Motor write failed, continue */ }
        }

        /// <summary>
        /// Centers the axis to ProSim position 512 using closed-loop feedback.
        /// Uses ProSim axis value as feedback to incrementally move the servo motor until centered.
        /// </summary>
        /// <param name="getProSimValue">Function to get the current ProSim axis value (0-1024 range, 512 = center)</param>
        /// <param name="log">Action to log status messages</param>
        /// <returns>Task that completes when centering is done or fails</returns>
        public async Task CenterToProSimPositionAsync(Func<double> getProSimValue, Action<string> log)
        {
            const double TARGET_CENTER = 512.0;
            const double TOLERANCE = 2.0;
            const int MAX_ITERATIONS = 50;
            const int DELAY_MS = 200;

            if (_isDisposed)
            {
                log($"[{_name}] ERROR: AxisManager is disposed");
                return;
            }

            // Reset the encoder center offset to 0 at the start of centering
            // This ensures we calculate position based on the configured center only
            // The new offset will be set after successful centering
            double originalOffset = _axisVm.Underlying.EncoderCenterOffset;
            _axisVm.Underlying.EncoderCenterOffset = 0.0;
            log($"[{_name}] Centering started - encoder offset reset to 0 (was {originalOffset:F2})");

            // Account for reversed motor setting - if motor is reversed, we need to invert the direction
            double motorDirectionMultiplier = _axisVm.Underlying.Settings.ReversedMotor ? -1.0 : 1.0;
            log($"[{_name}] Motor direction multiplier: {motorDirectionMultiplier} (ReversedMotor: {_axisVm.Underlying.Settings.ReversedMotor})");

            double? previousError = null;
            int errorIncreasingCount = 0; // Track consecutive error increases

            try
            {
                for (int iteration = 0; iteration < MAX_ITERATIONS; iteration++)
                {
                    // Get current ProSim axis value
                    double currentProSimValue = getProSimValue();

                    // Calculate error from center
                    double error = currentProSimValue - TARGET_CENTER;
                    double errorMagnitude = Math.Abs(error);

                    log($"[{_name}] Iteration {iteration + 1}: ProSim={currentProSimValue:F1}, Error={error:F1}");

                    // Check if we're within tolerance
                    if (errorMagnitude <= TOLERANCE)
                    {
                        log($"[{_name}] Centered successfully at {currentProSimValue:F1}");

                        // Set the encoder center offset to normalize future calculations
                        // The current encoder position becomes the new reference center
                        double centeredEncoderPos = _axisVm.EncoderPosition;
                        double configuredCenter = _axisVm.Underlying.Settings.CenterPosition;
                        _axisVm.Underlying.EncoderCenterOffset = centeredEncoderPos - configuredCenter;

                        log($"[{_name}] Encoder center offset set to {_axisVm.Underlying.EncoderCenterOffset:F2} (Encoder: {centeredEncoderPos:F1}, Configured: {configuredCenter:F1})");

                        return;
                    }

                    // Detect if we're moving away from center (error is increasing)
                    if (previousError.HasValue && iteration > 0)
                    {
                        double previousErrorMagnitude = Math.Abs(previousError.Value);

                        // If error magnitude increased, we're moving in the wrong direction
                        if (errorMagnitude > previousErrorMagnitude + 3.0) // +3 threshold to account for noise
                        {
                            errorIncreasingCount++;
                            log($"[{_name}] ERROR INCREASING! Count: {errorIncreasingCount}, Error: {errorMagnitude:F1} (was {previousErrorMagnitude:F1})");

                            // If error keeps increasing for 2 consecutive iterations, reverse the motor direction assumption
                            if (errorIncreasingCount >= 2)
                            {
                                motorDirectionMultiplier *= -1.0;
                                errorIncreasingCount = 0; // Reset counter
                                log($"[{_name}] Direction reversed! New multiplier: {motorDirectionMultiplier}");
                            }
                        }
                        else
                        {
                            // Error is decreasing (good), reset counter
                            errorIncreasingCount = 0;
                        }
                    }

                    // Calculate movement speed based on error magnitude
                    // Larger error = faster movement, smaller error = slower movement
                    double speedPercent;

                    if (errorMagnitude > 200)
                        speedPercent = 30.0; // Fast movement for large errors
                    else if (errorMagnitude > 100)
                        speedPercent = 20.0; // Medium speed
                    else if (errorMagnitude > 50)
                        speedPercent = 10.0; // Slow speed
                    else if (errorMagnitude > 20)
                        speedPercent = 5.0; // Very slow
                    else
                        speedPercent = 2.0; // Crawl speed near center

                    // Determine direction: if error is positive, we're right of center (need to move left)
                    // if error is negative, we're left of center (need to move right)
                    // Apply motor direction multiplier to account for ReversedMotor setting
                    double desiredDirection = (error > 0 ? -1.0 : 1.0);
                    double movePercent = desiredDirection * motorDirectionMultiplier * speedPercent;

                    log($"[{_name}] Moving {movePercent:F1}% (speed={speedPercent:F1}%)");

                    // Get current encoder position
                    // Note: EncoderCenterOffset is reset to 0 at start of centering, so we use configured center only
                    double currentEncoderPos = _axisVm.EncoderPosition;
                    double centerEncoderPos = _axisVm.Underlying.Settings.CenterPosition;
                    double currentPercentFromCenter = ((currentEncoderPos - centerEncoderPos) / Math.Max(1, Math.Abs(_axisVm.Underlying.Settings.FullRightPosition - centerEncoderPos))) * 100.0;

                    // Calculate target percent
                    double targetPercent = currentPercentFromCenter + movePercent;
                    targetPercent = Math.Max(-100, Math.Min(100, targetPercent)); // Clamp to valid range

                    // Move the axis
                    _axisMovement.GoToPosition(targetPercent);

                    // Store current error for next iteration
                    previousError = error;

                    // Wait for movement to complete
                    await Task.Delay(DELAY_MS);

                    // Give encoder time to update
                    await Task.Delay(DELAY_MS);
                }

                log($"[{_name}] WARNING: Max iterations reached. Final position: {getProSimValue():F1}");
            }
            catch (Exception ex)
            {
                log($"[{_name}] Error: {ex.Message}");
            }
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
                if (_proSimManager != null)
                {
                    _proSimManager.OnHydraulicsAvailableChanged -= ProSimManager_OnHydraulicsAvailableChanged;
                }
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
