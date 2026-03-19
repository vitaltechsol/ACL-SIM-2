using System;
using System.Diagnostics;
using System.Threading;
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

        /// <summary>
        /// Interval between autopilot tracking loop iterations (ms).
        /// </summary>
        private const int AUTOPILOT_LOOP_INTERVAL_MS = 100;
        private CancellationTokenSource? _autopilotLoopCts;
        private long _latestAutopilotTargetBits = BitConverter.DoubleToInt64Bits(double.NaN);

        private double LatestAutopilotTarget
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestAutopilotTargetBits));
            set => Interlocked.Exchange(ref _latestAutopilotTargetBits, BitConverter.DoubleToInt64Bits(value));
        }

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

                // Subscribe to ProSim autopilot command changes (Pitch and Roll only)
                if (string.Equals(_name, "Pitch", StringComparison.OrdinalIgnoreCase))
                {
                    _proSimManager.OnPitchCmdChanged += ProSimManager_OnAutopilotChanged;
                    _proSimManager.OnElevatorChanged += ProSimManager_OnFlightControlChanged;
                }
                else if (string.Equals(_name, "Roll", StringComparison.OrdinalIgnoreCase))
                {
                    _proSimManager.OnRollCmdChanged += ProSimManager_OnAutopilotChanged;
                    _proSimManager.OnAileronLeftChanged += ProSimManager_OnFlightControlChanged;
                }
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
        /// Handles changes to ProSim autopilot command state (B_PITCH_CMD / B_ROLL_CMD).
        /// Updates AutopilotOn for this axis.
        /// </summary>
        private void ProSimManager_OnAutopilotChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            var autopilotOn = e.Value != 0;

            if (!_axisVm.Underlying.CalibrationMode)
            {
                _axisVm.Underlying.AutopilotOn = autopilotOn;

                // MotorIsMoving follows autopilot state; actual movement is driven by
                // ProSimManager_OnFlightControlChanged (elevator / aileron events).
                _axisVm.Underlying.MotorIsMoving = autopilotOn;

                if (autopilotOn)
                {
                    StartAutopilotTrackingLoop();
                }
                else
                {
                    // Autopilot disengaged — stop tracking loop, revert to
                    // encoder-based dynamic torque, and quickly return to center.
                    StopAutopilotTrackingLoop();
                    try { _axisMovement.GoToPosition(0, rpmOverride: 90); } catch { }
                }

                // Notify the ViewModel to update UI bindings
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AutopilotOn));
                    _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
                });

                _ = UpdateTorqueAsync();
            }
        }

        /// <summary>
        /// Handles ProSim flight control value changes (elevator for Pitch, aileron for Roll).
        /// Stores the latest target for the background tracking loop to pick up.
        /// Raw ProSim flight-control values are approximately -1..+1; multiply by 100 to
        /// obtain the -100..+100 percentage expected by <see cref="AxisMovement.GoToPosition"/>.
        /// </summary>
        private void ProSimManager_OnFlightControlChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            if (_isDisposed || !_axisVm.Underlying.AutopilotOn) return;
            LatestAutopilotTarget = Math.Max(-100.0, Math.Min(100.0, e.Value * 100.0));
        }

        /// <summary>
        /// Starts a background loop that continuously commands the servo to follow
        /// the latest ProSim target while autopilot is engaged.
        /// </summary>
        private void StartAutopilotTrackingLoop()
        {
            StopAutopilotTrackingLoop();
            _autopilotLoopCts = new CancellationTokenSource();
            var token = _autopilotLoopCts.Token;

            Task.Run(async () =>
            {
                Debug.WriteLine($"[{_name}] Autopilot tracking loop started");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var target = LatestAutopilotTarget;
                        if (!double.IsNaN(target))
                        {
                            _axisMovement.GoToPosition(target);
                        }
                        await Task.Delay(AUTOPILOT_LOOP_INTERVAL_MS, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{_name}] Autopilot tracking error: {ex.Message}");
                        try { await Task.Delay(AUTOPILOT_LOOP_INTERVAL_MS, token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                Debug.WriteLine($"[{_name}] Autopilot tracking loop stopped");
            }, token);
        }

        /// <summary>
        /// Stops the autopilot tracking loop and clears the stored target.
        /// </summary>
        private void StopAutopilotTrackingLoop()
        {
            if (_autopilotLoopCts != null)
            {
                _autopilotLoopCts.Cancel();
                _autopilotLoopCts.Dispose();
                _autopilotLoopCts = null;
            }
            LatestAutopilotTarget = double.NaN;
        }

        /// <summary>
        /// Calculates torque based on encoder position relative to center.
        /// Torque increases as distance from center increases.
        /// Min torque at center (0), max torque at either limit.
        /// When MotorIsMoving (centering, position test, or autopilot), uses MovingTorqueDisplay for both directions.
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

            // If MotorIsMoving (centering, position test, or autopilot movement), use fixed MovingTorque for both directions
            if (_axisVm.Underlying.MotorIsMoving)
            {
                await Task.Run(() =>
                {
                    var settings = _axisVm.Underlying.Settings;

                    // Convert MovingTorquePercentage (0-100) to actual motor torque (0-300)
                    var torqueActual = settings.ConvertTorqueDisplayToActual(settings.MovingTorquePercentage);
                    var torqueInt = (int)Math.Round(torqueActual);

                    // Clamp to valid range (0-300)
                    torqueInt = Math.Max(0, Math.Min(300, torqueInt));

                    // Set same torque for both directions during motor movement
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

                    // Relative position: 0 at center, negative=left, positive=right
                    var relativePos = encoderPosition - _axisVm.Underlying.EncoderCenterOffset;

                    // Calculate normalized distance based on direction
                    double normalizedDistance;

                    if (relativePos >= 0)
                    {
                        normalizedDistance = Math.Min(1.0, relativePos / Math.Max(1e-6, settings.FullRightPosition));
                    }
                    else
                    {
                        normalizedDistance = Math.Min(1.0, Math.Abs(relativePos) / Math.Max(1e-6, Math.Abs(settings.FullLeftPosition)));
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
                        if (relativePos >= 0)
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
        /// <param name="rpmOverride">
        /// Optional RPM value that overrides <see cref="AxisSettings.MotorSpeedRpm"/>.
        /// When null, the configured MotorSpeedRpm is used.
        /// </param>
        public void GoToPosition(double targetPercent, int? rpmOverride = null)
        {
            _axisMovement.GoToPosition(targetPercent, rpmOverride);
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
        /// Uses <see cref="AxisMovement.MoveByUnits"/> to move in raw encoder units,
        /// avoiding any dependency on <c>EncoderCenterOffset</c> or the calibrated
        /// <c>FullLeft/FullRight</c> range (which are not yet known at centering time).
        /// After the first measured move, the encoder-to-ProSim gain is computed
        /// adaptively so subsequent moves converge quickly without overshooting.
        /// Once within tolerance the motor is stopped and a 2-second averaging window
        /// determines the final <c>EncoderCenterOffset</c>.
        /// </summary>
        /// <param name="getProSimValue">Function to get the current ProSim axis value (0-1024 range, 512 = center)</param>
        /// <param name="log">Action to log status messages</param>
        /// <returns>Task that completes when centering is done or fails</returns>
        public async Task CenterToProSimPositionAsync(Func<double> getProSimValue, Action<string> log)
        {
            const double TARGET_CENTER = 512.0;
            const double COARSE_TOLERANCE = 15.0;    // Enter fine-centering phase
            const double FINE_TOLERANCE = 2.0;       // Final accuracy target (±2 ProSim units, averaged)
            const int MAX_ITERATIONS = 60;
            const int SETTLE_MS = 400;
            const int MAX_FINE_ADJUSTMENTS = 10;     // Max correction cycles in fine phase

            // Per-read averaging to smooth ±10-unit ProSim noise
            const int SAMPLES_PER_READ = 5;
            const int SAMPLE_INTERVAL_MS = 40;

            // Final averaging constants
            const int AVERAGING_DURATION_MS = 2000;
            const int AVERAGING_SAMPLE_INTERVAL_MS = 50;

            // Movement limits (encoder units)
            const int MAX_MOVE_UNITS = 600;
            const int MIN_MOVE_UNITS = 15;
            const int PROBE_UNITS = 80;

            if (_isDisposed)
            {
                log($"[{_name}] ERROR: AxisManager is disposed");
                return;
            }

            log($"[{_name}] Centering started - EncoderCenterOffset = {_axisVm.Underlying.EncoderCenterOffset:F2}");

            // Helper: read averaged ProSim value to reduce noise impact
            async Task<double> ReadAveragedProSimAsync()
            {
                double sum = 0;
                for (int i = 0; i < SAMPLES_PER_READ; i++)
                {
                    sum += getProSimValue();
                    if (i < SAMPLES_PER_READ - 1)
                        await Task.Delay(SAMPLE_INTERVAL_MS);
                }
                return sum / SAMPLES_PER_READ;
            }

            // Helper: average ProSim and encoder over 2 seconds
            async Task<(double avgProSim, double avgEncoder)> AverageOverWindowAsync()
            {
                int sampleCount = AVERAGING_DURATION_MS / AVERAGING_SAMPLE_INTERVAL_MS;
                double proSimSum = 0;
                double encoderSum = 0;

                for (int s = 0; s < sampleCount; s++)
                {
                    proSimSum += getProSimValue();
                    encoderSum += _axisVm.EncoderPosition;
                    await Task.Delay(AVERAGING_SAMPLE_INTERVAL_MS);
                }

                return (proSimSum / sampleCount, encoderSum / sampleCount);
            }

            // Signed gain: encoder units to move per ProSim unit of desired change.
            var settings = _axisVm.Underlying.Settings;
            double totalEncoderRange = Math.Abs(settings.FullRightPosition) + Math.Abs(settings.FullLeftPosition);
            double gain = totalEncoderRange / 1024.0; // magnitude only — sign unknown
            bool gainSignKnown = false;

            log($"[{_name}] Initial gain magnitude: {gain:F2} enc/ProSim (sign TBD)");

            try
            {
                // Read initial state
                double currentProSim = await ReadAveragedProSimAsync();

                for (int iteration = 0; iteration < MAX_ITERATIONS; iteration++)
                {
                    double error = currentProSim - TARGET_CENTER;
                    double errorMag = Math.Abs(error);
                    double currentEncoder = _axisVm.EncoderPosition;

                    log($"[{_name}] Iter {iteration + 1}: ProSim={currentProSim:F1}, Error={error:F1}, Enc={currentEncoder:F0}");

                    // Within coarse tolerance → enter fine-centering phase
                    if (errorMag <= COARSE_TOLERANCE)
                    {
                        log($"[{_name}] Within coarse tolerance ({COARSE_TOLERANCE}), entering fine-centering phase...");

                        for (int fineIter = 0; fineIter < MAX_FINE_ADJUSTMENTS; fineIter++)
                        {
                            // Stop motor and let it settle
                            _axisMovement.Stop();
                            await Task.Delay(300);

                            // Average ProSim and encoder over 2 seconds
                            var (avgProSim, avgEncoder) = await AverageOverWindowAsync();
                            double avgError = avgProSim - TARGET_CENTER;

                            log($"[{_name}] Fine iter {fineIter + 1}: Avg ProSim={avgProSim:F1}, Avg Error={avgError:F1}, Avg Enc={avgEncoder:F2}");

                            // Check if averaged position is within fine tolerance
                            if (Math.Abs(avgError) <= FINE_TOLERANCE)
                            {
                                _axisVm.Underlying.EncoderCenterOffset = avgEncoder;
                                log($"[{_name}] Centered successfully (±{FINE_TOLERANCE}). Avg ProSim={avgProSim:F1}, Encoder center offset set to {avgEncoder:F2}");
                                return;
                            }

                            // Averaged error still too large — make a small correction
                            // Use calibrated gain; if gain isn't calibrated yet, use magnitude with sign guess
                            double correction = gain * (-avgError);
                            int correctionUnits = (int)Math.Round(correction * 0.5); // Conservative 50% of estimated
                            if (Math.Abs(correctionUnits) < 5)
                                correctionUnits = correctionUnits >= 0 ? 5 : -5;
                            correctionUnits = Math.Max(-200, Math.Min(200, correctionUnits));

                            log($"[{_name}] Fine correction: {correctionUnits} encoder units (avg error={avgError:F1})");

                            double preCorrEncoder = _axisVm.EncoderPosition;
                            _axisMovement.MoveByUnits(correctionUnits);
                            await Task.Delay(SETTLE_MS);

                            // Update gain from the correction move if possible
                            double postCorrProSim = await ReadAveragedProSimAsync();
                            double postCorrEncoder = _axisVm.EncoderPosition;
                            double corrEncDelta = postCorrEncoder - preCorrEncoder;
                            double corrProSimDelta = postCorrProSim - avgProSim;

                            if (Math.Abs(corrProSimDelta) > 1.0 && Math.Abs(corrEncDelta) > 3.0)
                            {
                                gain = corrEncDelta / corrProSimDelta;
                                gainSignKnown = true;
                            }
                        }

                        // Max fine adjustments exhausted — accept last average
                        log($"[{_name}] Fine-centering: max adjustments reached, accepting current position");
                        _axisMovement.Stop();
                        await Task.Delay(300);
                        var (finalProSim, finalEncoder) = await AverageOverWindowAsync();
                        _axisVm.Underlying.EncoderCenterOffset = finalEncoder;
                        log($"[{_name}] Centered (best effort). Avg ProSim={finalProSim:F1}, Encoder center offset set to {finalEncoder:F2}");
                        return;
                    }

                    // --- Coarse approach phase (unchanged) ---

                    double desiredProSimChange = -error;

                    int moveUnits;

                    if (!gainSignKnown)
                    {
                        moveUnits = PROBE_UNITS;
                        log($"[{_name}] Probe move: {moveUnits} encoder units (gain sign unknown)");
                    }
                    else
                    {
                        double estimatedMove = gain * desiredProSimChange;

                        double fraction;
                        if (errorMag > 200) fraction = 0.5;
                        else if (errorMag > 100) fraction = 0.4;
                        else if (errorMag > 50) fraction = 0.35;
                        else fraction = 0.3;

                        moveUnits = (int)Math.Round(estimatedMove * fraction);

                        if (Math.Abs(moveUnits) < MIN_MOVE_UNITS)
                            moveUnits = moveUnits >= 0 ? MIN_MOVE_UNITS : -MIN_MOVE_UNITS;
                        moveUnits = Math.Max(-MAX_MOVE_UNITS, Math.Min(MAX_MOVE_UNITS, moveUnits));
                    }

                    double preMoveEncoder = _axisVm.EncoderPosition;
                    double preMoveProSim = currentProSim;

                    log($"[{_name}] Moving {moveUnits} encoder units");
                    _axisMovement.MoveByUnits(moveUnits);
                    await Task.Delay(SETTLE_MS);

                    currentProSim = await ReadAveragedProSimAsync();
                    double postMoveEncoder = _axisVm.EncoderPosition;

                    double actualEncoderDelta = postMoveEncoder - preMoveEncoder;
                    double actualProSimDelta = currentProSim - preMoveProSim;

                    log($"[{_name}] Result: ΔEnc={actualEncoderDelta:F0}, ΔProSim={actualProSimDelta:F1}");

                    if (Math.Abs(actualProSimDelta) > 2.0 && Math.Abs(actualEncoderDelta) > 5.0)
                    {
                        gain = actualEncoderDelta / actualProSimDelta;
                        gainSignKnown = true;
                        log($"[{_name}] Gain updated: {gain:F2} enc/ProSim");
                    }
                    else if (!gainSignKnown && Math.Abs(actualProSimDelta) > 0.5)
                    {
                        if (actualProSimDelta < 0)
                            gain = -Math.Abs(gain);
                        gainSignKnown = true;
                        log($"[{_name}] Gain sign determined: {gain:F2} enc/ProSim");
                    }
                    else if (!gainSignKnown)
                    {
                        gain = -Math.Abs(gain);
                        log($"[{_name}] Probe inconclusive, flipping sign: {gain:F2}");
                    }
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

            try { StopAutopilotTrackingLoop(); } catch { }

            try
            {
                if (_proSimManager != null)
                {
                    _proSimManager.OnHydraulicsAvailableChanged -= ProSimManager_OnHydraulicsAvailableChanged;

                    if (string.Equals(_name, "Pitch", StringComparison.OrdinalIgnoreCase))
                    {
                        _proSimManager.OnPitchCmdChanged -= ProSimManager_OnAutopilotChanged;
                        _proSimManager.OnElevatorChanged -= ProSimManager_OnFlightControlChanged;
                    }
                    else if (string.Equals(_name, "Roll", StringComparison.OrdinalIgnoreCase))
                    {
                        _proSimManager.OnRollCmdChanged -= ProSimManager_OnAutopilotChanged;
                        _proSimManager.OnAileronLeftChanged -= ProSimManager_OnFlightControlChanged;
                    }
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
