using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ACL_SIM_2.Models;
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
        private readonly IAppLogger? _logger;
        private readonly string _name;
        private bool _isDisposed;

        /// <summary>
        /// Interval between autopilot tracking loop iterations (ms).
        /// </summary>
        /// <summary>
        /// Delay between autopilot tracking loop iterations.
        /// Lower values increase responsiveness; higher values reduce command churn and can smooth motion.
        /// </summary>
        private const int AUTOPILOT_LOOP_INTERVAL_MS = 100; //100
        private const int AIRSPEED_TORQUE_THROTTLE_MS = 50;
        private CancellationTokenSource? _autopilotLoopCts;
        private Task? _autopilotLoopTask;
        private long _latestAutopilotTargetBits = BitConverter.DoubleToInt64Bits(double.NaN);
        private readonly object _airspeedUpdateLock = new object();
        private readonly object _trimCommandLock = new object();
        private long _lastAirspeedTorqueUpdateTick;
        private bool _airspeedUpdateScheduled;
        private double _pendingTrimPercent;
        private long _lastTrimUpdateTick;
        private bool _trimMoveLoopRunning;
        private double _trimBaseCenterOffset;
        private double _lastAppliedTrimPercent;
        private volatile bool _centeringPerformed;
        private volatile bool _isCenteringActive;

        private const int TRIM_FILTER_MS = 100;
        private const int STALL_TORQUE_DURATION_MS = 2000;

        private long _stallTorqueActiveUntil = 0;

        private volatile bool _autopilotOverrideActive;
        private volatile bool _isPositionTestActive;

        private double LatestAutopilotTarget
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestAutopilotTargetBits));
            set => Interlocked.Exchange(ref _latestAutopilotTargetBits, BitConverter.DoubleToInt64Bits(value));
        }

        public AxisManager(string name, AxisViewModel axisVm, ModbusClient modbusClient, object? modbusLock, ProSimManager proSimManager, IAppLogger logger)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            _proSimManager = proSimManager;
            _logger = logger;
            _trimBaseCenterOffset = _axisVm.Underlying.EncoderCenterOffset;

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
            _axisMovement = new AxisMovement(axisVm.Underlying, modbusClient, _torqueControl, modbusLock, _logger);

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
                    _proSimManager.OnSpeedIasChanged += ProSimManager_OnSpeedIasChanged;
                    _proSimManager.OnIsStallingChanged += ProSimManager_OnIsStallingChanged;
                    _axisVm.Underlying.AirspeedIas = _proSimManager.SpeedIas;
                }
                else if (string.Equals(_name, "Roll", StringComparison.OrdinalIgnoreCase))
                {
                    _proSimManager.OnRollCmdChanged += ProSimManager_OnAutopilotChanged;
                    _proSimManager.OnAileronRightChanged += ProSimManager_OnFlightControlChanged;
                    _proSimManager.OnTrimAileronChanged += ProSimManager_OnTrimChanged;
                }
                else if (string.Equals(_name, "Rudder", StringComparison.OrdinalIgnoreCase))
                {
                    _proSimManager.OnTrimRudderChanged += ProSimManager_OnTrimChanged;
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

                // For Pitch axis: set centering speed to 0 when hydraulics are off, restore when on
                if (string.Equals(_name, "Pitch", StringComparison.OrdinalIgnoreCase) && !_isCenteringActive)
                {
                    var centeringSpeed = hydraulicsAvailable
                        ? AxisSettings.ConvertCenteringSpeedToActual(_axisVm.Underlying.Settings.SelfCenteringSpeed)
                        : 0;
                    SendCenteringSpeed(centeringSpeed);
                }

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
                    if (!_isPositionTestActive)
                        StartAutopilotTrackingLoop();
                }
                else
                {
                    // Normal AP disengage -- stop loop and return to center.
                    // Awaiting the loop ensures its final MoveToward call cannot
                    // race with and override the GoToPosition(0) center command.
                    _ = StopAndReturnToCenterAsync();
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
            var newTarget = Math.Max(-100.0, Math.Min(100.0, e.Value * 100.0));
            var prev = LatestAutopilotTarget;
            LatestAutopilotTarget = newTarget;
            if (!double.IsNaN(prev) && Math.Abs(newTarget - prev) >= 1.0)
            {
                Debug.WriteLine($"[{_name}] AP target changed: {prev:F1}% -> {newTarget:F1}% (raw={e.Value:F4})");
            }
        }

        private void ProSimManager_OnSpeedIasChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            if (_isDisposed) return;

            _axisVm.Underlying.AirspeedIas = e.Value;
            RequestAirspeedTorqueUpdate();
        }

        private void ProSimManager_OnIsStallingChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            if (_isDisposed) return;

            var isStalling = e.Value != 0;
            if (!isStalling) return;

            Interlocked.Exchange(ref _stallTorqueActiveUntil, Environment.TickCount64 + STALL_TORQUE_DURATION_MS);
            _ = UpdateTorqueAsync();
            _ = Task.Delay(STALL_TORQUE_DURATION_MS).ContinueWith(_ => UpdateTorqueAsync());
        }

        private void ProSimManager_OnTrimChanged(object? sender, DataRefValueChangedEventArgs e)
        {
            if (_isDisposed || _axisVm.Underlying.CalibrationMode || _axisVm.Underlying.AutopilotOn || !_centeringPerformed)
            {
                return;
            }

            ScheduleTrimMove(ScaleTrimToPercent(e.Value));
        }


        private double ScaleTrimToPercent(double trimValue)
        {
            var sign = Math.Sign(trimValue);
            var magnitude = Math.Abs(trimValue);

            if (string.Equals(_name, "Roll", StringComparison.OrdinalIgnoreCase))
            {
                return sign * Math.Min(52.0, (magnitude / 10.0) * 52.0);
            }

            if (string.Equals(_name, "Rudder", StringComparison.OrdinalIgnoreCase))
            {
                return sign * Math.Min(100.0, (magnitude / 15.0) * 100.0);
            }

            return trimValue;
        }

        private void ScheduleTrimMove(double trimPercent)
        {
            var shouldStartLoop = false;

            lock (_trimCommandLock)
            {
                _pendingTrimPercent = trimPercent;
                _lastTrimUpdateTick = Environment.TickCount64;

                if (!_trimMoveLoopRunning)
                {
                    _trimMoveLoopRunning = true;
                    shouldStartLoop = true;
                }
            }

            if (shouldStartLoop)
            {
                _ = Task.Run(RunTrimMoveLoopAsync);
            }
        }

        private async Task RunTrimMoveLoopAsync()
        {
            const double MAX_TRIM_STEP = 300.0;
            const double TRIM_POSITION_TOLERANCE = 10.0;

            // Enter trim state BEFORE resetting ECO so any torque recalculation
            // triggered by the offset change sees IsTrimming==true and uses fixed
            // MovingTorquePercentage instead of dynamic encoder-based torque
            // (which would spike because the relative position jumps by the
            // entire previous trim offset).
            _axisVm.Underlying.IsTrimming = true;
            NotifyTrimStateChanged();
            ApplyRuntimeCenterOffset(_trimBaseCenterOffset);
            _ = UpdateTorqueAsync();

            try
            {
                while (!_isDisposed)
                {
                    double trimPercent;

                    lock (_trimCommandLock)
                    {
                        trimPercent = _pendingTrimPercent;
                    }

                    if (_axisVm.Underlying.CalibrationMode || _axisVm.Underlying.AutopilotOn)
                    {
                        return;
                    }

                    // Calculate target in encoder units and cap per-command delta
                    // to prevent servo overshoot from oversized pulse commands
                    var trimTarget = _trimBaseCenterOffset + ConvertTrimPercentToEncoderOffset(trimPercent);
                    var currentRaw = (double)_axisVm.EncoderPosition;
                    var delta = trimTarget - currentRaw;

                    if (Math.Abs(delta) > TRIM_POSITION_TOLERANCE)
                    {
                        if (Math.Abs(delta) <= MAX_TRIM_STEP)
                        {
                            _axisMovement.GoToPosition(trimPercent);
                        }
                        else
                        {
                            // Cap the move to MAX_TRIM_STEP encoder units
                            var cappedTarget = currentRaw + Math.Sign(delta) * MAX_TRIM_STEP;
                            var relTarget = cappedTarget - _trimBaseCenterOffset;
                            var settings = _axisVm.Underlying.Settings;
                            double fullRange = relTarget >= 0
                                ? Math.Max(1.0, settings.FullRightPosition)
                                : Math.Max(1.0, Math.Abs(settings.FullLeftPosition));
                            var cappedPercent = Math.Clamp((relTarget / fullRange) * 100.0, -100.0, 100.0);
                            _axisMovement.GoToPosition(cappedPercent);
                        }
                    }

                    _lastAppliedTrimPercent = trimPercent;

                    await Task.Delay(TRIM_FILTER_MS);

                    // Re-read the latest tick AFTER the delay to check for new events
                    long latestTick;
                    lock (_trimCommandLock)
                    {
                        latestTick = _lastTrimUpdateTick;
                    }

                    if (Environment.TickCount64 - latestTick >= TRIM_FILTER_MS)
                    {
                        // No new trim events -- check if motor still needs to catch up
                        var remaining = (_trimBaseCenterOffset + ConvertTrimPercentToEncoderOffset(_lastAppliedTrimPercent))
                            - (double)_axisVm.EncoderPosition;
                        if (Math.Abs(remaining) <= MAX_TRIM_STEP)
                        {
                            // Close enough -- send final command if needed and exit
                            if (Math.Abs(remaining) > TRIM_POSITION_TOLERANCE)
                            {
                                _axisMovement.GoToPosition(_lastAppliedTrimPercent);
                            }
                            return;
                        }
                        // Still far from target -- continue loop for catch-up
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_name}] Trim move failed: {ex.Message}");
            }
            finally
            {
                var shouldRestartLoop = false;

                lock (_trimCommandLock)
                {
                    _trimMoveLoopRunning = false;

                    if (!_isDisposed &&
                        !_axisVm.Underlying.CalibrationMode &&
                        !_axisVm.Underlying.AutopilotOn &&
                        Environment.TickCount64 - _lastTrimUpdateTick < TRIM_FILTER_MS)
                    {
                        _trimMoveLoopRunning = true;
                        shouldRestartLoop = true;
                    }
                }

                if (shouldRestartLoop)
                {
                    _ = Task.Run(RunTrimMoveLoopAsync);
                }
                else
                {
                    // Only apply trim offset and exit trim state when truly finished
                    ApplyTrimOffsetToCenter();
                    _axisVm.Underlying.IsTrimming = false;
                    NotifyTrimStateChanged();
                    _ = UpdateTorqueAsync();
                }
            }
        }

        private void NotifyTrimStateChanged()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.IsTrimming));
            });
        }

        private void ApplyTrimOffsetToCenter()
        {
            ApplyRuntimeCenterOffset(_trimBaseCenterOffset + ConvertTrimPercentToEncoderOffset(_lastAppliedTrimPercent));
        }

        private void ApplyRuntimeCenterOffset(double encoderCenterOffset)
        {
            _axisVm.Underlying.EncoderCenterOffset = encoderCenterOffset;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.Torque));
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.TorqueNormalized));
            });
        }

        private double ConvertTrimPercentToEncoderOffset(double trimPercent)
        {
            if (trimPercent >= 0)
            {
                return (trimPercent / 100.0) * _axisVm.Underlying.Settings.FullRightPosition;
            }

            return (trimPercent / 100.0) * Math.Abs(_axisVm.Underlying.Settings.FullLeftPosition);
        }

        private void RequestAirspeedTorqueUpdate()
        {
            int delayMs = 0;

            lock (_airspeedUpdateLock)
            {
                var now = Environment.TickCount64;
                var elapsedMs = _lastAirspeedTorqueUpdateTick == 0
                    ? AIRSPEED_TORQUE_THROTTLE_MS
                    : now - _lastAirspeedTorqueUpdateTick;

                if (elapsedMs >= AIRSPEED_TORQUE_THROTTLE_MS)
                {
                    _lastAirspeedTorqueUpdateTick = now;
                }
                else
                {
                    if (_airspeedUpdateScheduled)
                    {
                        return;
                    }

                    _airspeedUpdateScheduled = true;
                    delayMs = (int)Math.Max(1, AIRSPEED_TORQUE_THROTTLE_MS - elapsedMs);
                }
            }

            if (delayMs == 0)
            {
                _ = UpdateTorqueAsync();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);

                    lock (_airspeedUpdateLock)
                    {
                        _airspeedUpdateScheduled = false;
                        _lastAirspeedTorqueUpdateTick = Environment.TickCount64;
                    }

                    await UpdateTorqueAsync();
                }
                catch
                {
                    lock (_airspeedUpdateLock)
                    {
                        _airspeedUpdateScheduled = false;
                    }
                }
            });
        }

        /// <summary>
        /// Starts a background loop that continuously commands the servo to follow
        /// the latest ProSim target while autopilot is engaged.
        /// </summary>
        private void StartAutopilotTrackingLoop()
        {
            StopAutopilotTrackingLoop();
            _autopilotOverrideActive = false;
            _axisMovement.Reset();
            _autopilotLoopCts = new CancellationTokenSource();
            var token = _autopilotLoopCts.Token;

         _autopilotLoopTask = Task.Run(async () =>
            {
                Debug.WriteLine($"[{_name}] Autopilot tracking loop started");
                var lastTarget = double.NaN;
                var smoothedTarget = double.NaN;
                var arrived = true;
                var lastMotorCommandTick = 0L;
                while (!token.IsCancellationRequested)
                {
                    try { 
                        var raw = LatestAutopilotTarget;
                        if (double.IsNaN(raw))
                        {
                            await Task.Delay(AUTOPILOT_LOOP_INTERVAL_MS, token);
                            continue;
                        }

                        // Apply low-pass smoothing (TargetFilterAlpha: 1.0=no smoothing, 0.0=max smoothing)
                        var alpha = _axisVm.Underlying.Settings.TargetFilterAlpha;
                        smoothedTarget = double.IsNaN(smoothedTarget)
                            ? raw
                            : alpha * raw + (1.0 - alpha) * smoothedTarget;
                        var target = smoothedTarget;

                        if (double.IsNaN(lastTarget) || Math.Abs(target - lastTarget) > 0.01)
                        {
                            if (arrived)
                                _axisMovement.BeginMove();
                            else
                                _axisMovement.RefreshMoveTimeout();
                            lastTarget = target;
                            arrived = false;
                        }

                        if (!arrived)
                        {
                            var now = Environment.TickCount64;
                            var minIntervalMs = _axisVm.Underlying.Settings.MinMotorCommandIntervalMs;
                            if (now - lastMotorCommandTick >= minIntervalMs)
                            {
                                arrived = _axisMovement.MoveToward(target);
                                if (arrived)
                                    _axisMovement.Stop();
                                lastMotorCommandTick = now;
                            }
                        }

                        CheckAutopilotManualOverride();
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
        /// Checks whether the pilot has manually overridden the autopilot by measuring the
        /// external (physical) torque reported by the motor driver (register 370, Dn002).
        /// When the absolute external torque percentage exceeds MovingTorquePercentage by
        /// more than AutopilotOverridePercent, autopilot is disengaged and the axis returns
        /// to center.
        /// </summary>
        private void CheckAutopilotManualOverride()
        {
            if (_autopilotOverrideActive || _torqueControl == null) return;

            int rawTorque;
            try
            {
                // Register 370 (Dn002) reports average torque already in percentage units (0-100),
                rawTorque = _torqueControl.GetExternalTorque();
              //  Debug.WriteLine($"[{_name}] rawTorque: {rawTorque}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_name}] GetExternalTorque failed: {ex.Message}");
                return;
            }

            var settings = _axisVm.Underlying.Settings;
            var threshold = settings.MovingTorquePercentage + settings.AutopilotOverridePercent;
            if (rawTorque <= threshold) return;

            // _autopilotOverrideActive = true;

            _logger?.Log(
                $"[{_name}] MANUAL OVERRIDE TRIGGERED " +
                $"externalTorque={rawTorque:F1}% threshold={threshold:F1}% " +
                $"(movingTorque={settings.MovingTorquePercentage:F1}% + override={settings.AutopilotOverridePercent:F1}%)"                );

            // Cancel the tracking loop from within the running task
            //_autopilotLoopCts?.Cancel();

            //// Update local state immediately
            //_axisVm.Underlying.AutopilotOn = false;
            //_axisVm.Underlying.MotorIsMoving = false;

            // Signal ProSim to disengage autopilot
            if (_proSimManager != null)
            {
                try
                {
                    _proSimManager.DisengageAP();
                    _ = Task.Delay(2000).ContinueWith(_ => _ = StopAndReturnToCenterAsync());
                    //_ = Task.Delay(4000).ContinueWith(_ => _axisMovement.GoToPosition(0, rpmOverride: 100));
                }
                catch (Exception ex) { Debug.WriteLine($"[{_name}] DisengageAP failed: {ex.Message}"); }
            }

            //System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            //{
            //    _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AutopilotOn));
            //    _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
            //});

            //_ = UpdateTorqueAsync();
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
            _autopilotLoopTask = null;
            LatestAutopilotTarget = double.NaN;
        }

        /// <summary>
        /// Cancels and awaits the autopilot tracking loop, then issues a center command.
        /// Awaiting the loop before calling GoToPosition(0) prevents the loop's final
        /// MoveToward call from racing with and overriding the center command.
        /// The GoToPosition(0) is skipped if <see cref="_autopilotOverrideActive"/> is true,
        /// meaning a manual override already issued the center command while we were awaiting.
        /// </summary>
        private async Task StopAndReturnToCenterAsync()
        {
            var loopTask = _autopilotLoopTask;
            StopAutopilotTrackingLoop();

            if (loopTask != null)
            {
                try
                {
                    await loopTask.WaitAsync(TimeSpan.FromMilliseconds(AUTOPILOT_LOOP_INTERVAL_MS * 3 + 100));
                }
                catch { }
            }

            // Re-check override flag: if manual override fired while we were awaiting the loop,
            // it already issued GoToPosition(0). Issuing a second one would Stop() the motor
            // mid-return and recalculate from a stale encoder, causing the axis to miss center.
            if (!_isDisposed && _centeringPerformed)
            {
                _logger.Log($"[{_name}] Forced returning to center");
                try { _axisMovement.GoToPosition(0, rpmOverride: 90); } catch { }
            }
           
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
                        _axisVm.Underlying.AirspeedAdditionalTorqueAppliedPercent = 0.0;
                        _axisVm.CurrentTorque = 0.0;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AirspeedAdditionalTorqueAppliedPercent));
                        });
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });

                return;
            }

            // If MotorIsMoving or IsTrimming, use fixed MovingTorque for both directions
            if (_axisVm.Underlying.MotorIsMoving || _axisVm.Underlying.IsTrimming)
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
                        _axisVm.Underlying.AirspeedAdditionalTorqueAppliedPercent = 0.0;
                        _axisVm.CurrentTorque = settings.MovingTorquePercentage;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AirspeedAdditionalTorqueAppliedPercent));
                        });
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
                        _axisVm.Underlying.AirspeedAdditionalTorqueAppliedPercent = 0.0;
                        _axisVm.CurrentTorque = settings.HydraulicOffTorquePercent;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AirspeedAdditionalTorqueAppliedPercent));
                        });
                    }
                    catch
                    {
                        // Motor write failed, continue
                    }
                });

                return;
            }

            // Normal operation: calculate torque based on encoder position and settings
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
                    var baseTorqueDisplay = minTorqueDisplay + (normalizedDistance * (maxTorqueDisplay - minTorqueDisplay));
                    var airspeedAdditionalTorqueDisplay = _axisVm.Underlying.CalculateAirspeedAdditionalTorqueDisplayPercent();
                    var stallAdditionalTorqueDisplay = Interlocked.Read(ref _stallTorqueActiveUntil) > Environment.TickCount64
                        ? baseTorqueDisplay * (settings.StallAdditionalTorquePercent / 100.0)
                        : 0.0;
                    var targetTorqueDisplay = Math.Max(0.0, Math.Min(100.0, baseTorqueDisplay + airspeedAdditionalTorqueDisplay + stallAdditionalTorqueDisplay));

                    // Update ViewModel with display value for UI
                    _axisVm.Underlying.AirspeedAdditionalTorqueAppliedPercent = airspeedAdditionalTorqueDisplay;
                    _axisVm.Underlying.TorqueTarget = settings.ConvertTorqueDisplayToActual(targetTorqueDisplay);
                    _axisVm.CurrentTorque = targetTorqueDisplay;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.AirspeedAdditionalTorqueAppliedPercent));
                        _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.Torque));
                        _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.TorqueNormalized));
                    });

                    // Convert to actual motor scale (0-300) and send to motor
                    try
                    {
                        var targetTorqueActual = _axisVm.Underlying.TorqueTarget;
                        var torqueInt = (int)Math.Round(targetTorqueActual);

                        // Both registers must reflect the positional torque at all times.
                        // The motor's self-centering force is applied in the direction OPPOSITE
                        // to displacement: when at right, the motor pushes left (register 9);
                        // when at left, the motor pushes right (register 8).  Using SetTorqueBoth ensures the full position-based
                        // resistance is always active in both directions
                        _torqueControl.SetTorqueBoth(torqueInt, torqueInt);
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

        public void ApplyMotionTuningPreview()
        {
            if (_isDisposed) return;

            try
            {
                if (_axisVm.Underlying.MotorIsMoving)
                {
                    _axisMovement.ReapplyCurrentTarget();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_name}] Motion tuning preview failed: {ex.Message}");
            }

            _ = UpdateTorqueAsync();
        }

        /// <summary>
        /// Suspends the ProSim autopilot tracking loop for the duration of a position test.
        /// Call ResumeAfterPositionTest when the test ends.
        /// </summary>
        public void SuspendForPositionTest()
        {
            _isPositionTestActive = true;
            StopAutopilotTrackingLoop();
        }

        /// <summary>
        /// Lifts the position-test suspension so the autopilot loop can restart
        /// normally on the next ProSim autopilot-on event.
        /// </summary>
        public void ResumeAfterPositionTest()
        {
            _isPositionTestActive = false;
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
        /// <param name="cancellationToken">Token to cancel centering early</param>
        /// <returns>Task that completes when centering is done, cancelled, or fails</returns>
        public async Task CenterToProSimPositionAsync(Func<double> getProSimValue, Action<string> log, CancellationToken cancellationToken = default)
        {
            const double TARGET_CENTER = 512.0;      // Center target in {roSim units (0-1024 scale). Coarse tolerance gets us into the right neighborhood for gain calculation, fine tolerance is the final target for stopping and averaging.
            const double COARSE_TOLERANCE = 15.0;    // Enter fine-centering phase
            const double FINE_TOLERANCE = 2.0;       // Final accuracy target (ProSim units, averaged)
            const int MAX_ITERATIONS = 60;
            const int SETTLE_MS = 100;
            const int MAX_FINE_ADJUSTMENTS = 10;     // Max correction cycles in fine phase

            // Per-read averaging to smooth 
            const int SAMPLES_PER_READ = 3;
            const int SAMPLE_INTERVAL_MS = 25;

            // Final averaging constants
            const int AVERAGING_DURATION_MS = 1000; // How long to check final position stability before settling on center offset
            const int AVERAGING_SAMPLE_INTERVAL_MS = 50; // how often to sample ProSim and encoder during the final averaging window

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
                    cancellationToken.ThrowIfCancellationRequested();
                    sum += getProSimValue();
                    if (i < SAMPLES_PER_READ - 1)
                        await Task.Delay(SAMPLE_INTERVAL_MS, cancellationToken);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    proSimSum += getProSimValue();
                    encoderSum += _axisVm.EncoderPosition;
                    await Task.Delay(AVERAGING_SAMPLE_INTERVAL_MS, cancellationToken);
                }

                return (proSimSum / sampleCount, encoderSum / sampleCount);
            }

            // Signed gain: encoder units to move per ProSim unit of desired change.
            var settings = _axisVm.Underlying.Settings;
            double totalEncoderRange = Math.Abs(settings.FullRightPosition) + Math.Abs(settings.FullLeftPosition);
            double gain = totalEncoderRange / 1024.0; // magnitude only sign unknown
            bool gainSignKnown = false;
            int probeDirection = 1;
            int noMotionCount = 0;
            /// <summary>
            /// Minimum observed encoder movement required to treat a commanded centering move as real motion.
            /// Smaller changes are considered noise or insufficient movement.
            /// </summary>
            const double MIN_VALID_ENCODER_DELTA = 5.0;

            log($"[{_name}] Initial gain magnitude: {gain:F2} enc/ProSim (sign TBD)");

            var isPitch = string.Equals(_name, "Pitch", StringComparison.OrdinalIgnoreCase);
            var configuredCenteringSpeed = AxisSettings.ConvertCenteringSpeedToActual(_axisVm.Underlying.Settings.SelfCenteringSpeed);
            _isCenteringActive = true;
            if (isPitch)
            {
                SendCenteringSpeed(configuredCenteringSpeed);
            }

            try
            {
                // Read initial state
                double currentProSim = await ReadAveragedProSimAsync();

                for (int iteration = 0; iteration < MAX_ITERATIONS; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double error = currentProSim - TARGET_CENTER;
                    double errorMag = Math.Abs(error);
                    double currentEncoder = _axisVm.EncoderPosition;

                    log($"[{_name}] Iter {iteration + 1}: ProSim={currentProSim:F1}, Error={error:F1}, Enc={currentEncoder:F0}");

                    // Within coarse tolerance ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ enter fine-centering phase
                    if (errorMag <= COARSE_TOLERANCE)
                    {
                        log($"[{_name}] Within coarse tolerance ({COARSE_TOLERANCE}), entering fine-centering phase...");

                        for (int fineIter = 0; fineIter < MAX_FINE_ADJUSTMENTS; fineIter++)
                        {
                            // Stop motor and let it settle
                            _axisMovement.Stop();
                            await Task.Delay(SETTLE_MS, cancellationToken);

                            // Average ProSim and encoder over 2 seconds
                            var (avgProSim, avgEncoder) = await AverageOverWindowAsync();
                            double avgError = avgProSim - TARGET_CENTER;

                            log($"[{_name}] Fine iter {fineIter + 1}: Avg ProSim={avgProSim:F1}, Avg Error={avgError:F1}, Avg Enc={avgEncoder:F2}");

                            // Check if averaged position is within fine tolerance
                            if (Math.Abs(avgError) <= FINE_TOLERANCE)
                            {
                                _trimBaseCenterOffset = avgEncoder;
                                _centeringPerformed = true;
                                ApplyRuntimeCenterOffset(avgEncoder);
                                log($"[{_name}] Centered successfully (\u00b1{FINE_TOLERANCE}). Avg ProSim={avgProSim:F1}, Encoder center offset set to {avgEncoder:F2}");
                                return;
                            }

                            // Averaged error still too large ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â make a small correction
                            // Use calibrated gain; if gain isn't calibrated yet, use magnitude with sign guess
                            double correction = gain * (-avgError);
                            int correctionUnits = (int)Math.Round(correction * 0.5); // Conservative 50% of estimated
                            if (Math.Abs(correctionUnits) < 5)
                                correctionUnits = correctionUnits >= 0 ? 5 : -5;
                            correctionUnits = Math.Max(-200, Math.Min(200, correctionUnits));

                            log($"[{_name}] Fine correction: {correctionUnits} encoder units (avg error={avgError:F1})");

                            double preCorrEncoder = _axisVm.EncoderPosition;
                            _axisMovement.MoveByUnits(correctionUnits);
                            await Task.Delay(SETTLE_MS, cancellationToken);

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

                        // Max fine adjustments exhausted ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â accept last average
                        log($"[{_name}] Fine-centering: max adjustments reached, accepting current position");
                        _axisMovement.Stop();
                        await Task.Delay(300, cancellationToken);
                        var (finalProSim, finalEncoder) = await AverageOverWindowAsync();
                        _trimBaseCenterOffset = finalEncoder;
                        _centeringPerformed = true;
                        ApplyRuntimeCenterOffset(finalEncoder);
                        log($"[{_name}] Centered (best effort). Avg ProSim={finalProSim:F1}, Encoder center offset set to {finalEncoder:F2}");
                        return;
                    }

                    // --- Coarse approach phase (unchanged) ---

                    double desiredProSimChange = -error;

                    int moveUnits;

                    if (!gainSignKnown)
                    {
                        int probeMagnitude = Math.Min(MAX_MOVE_UNITS, PROBE_UNITS * Math.Max(1, noMotionCount + 1));
                        moveUnits = probeDirection * probeMagnitude;
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
                    await Task.Delay(SETTLE_MS, cancellationToken);

                    currentProSim = await ReadAveragedProSimAsync();
                    double postMoveEncoder = _axisVm.EncoderPosition;

                    double actualEncoderDelta = postMoveEncoder - preMoveEncoder;
                    double actualProSimDelta = currentProSim - preMoveProSim;

                    log($"[{_name}] Result Enc={actualEncoderDelta:F0}, ProSim={actualProSimDelta:F1}");

                    if (Math.Abs(actualEncoderDelta) <= MIN_VALID_ENCODER_DELTA)
                    {
                        noMotionCount++;
                        _axisMovement.Stop();

                        if (gainSignKnown)
                        {
                            log($"[{_name}] No encoder movement detected; discarding learned gain and retrying probe");
                            gainSignKnown = false;
                            probeDirection = gain < 0 ? -1 : 1;
                        }
                        else
                        {
                            probeDirection = -probeDirection;
                            gain = probeDirection < 0 ? -Math.Abs(gain) : Math.Abs(gain);
                            log($"[{_name}] Probe inconclusive (no encoder movement), retrying with opposite sign: {gain:F2}");
                        }

                        continue;
                    }

                    noMotionCount = 0;

                    if (Math.Abs(actualProSimDelta) > 2.0 && Math.Abs(actualEncoderDelta) > MIN_VALID_ENCODER_DELTA)
                    {
                        gain = actualEncoderDelta / actualProSimDelta;
                        gainSignKnown = true;
                        probeDirection = gain < 0 ? -1 : 1;
                        log($"[{_name}] Gain updated: {gain:F2} enc/ProSim");
                    }
                    else if (!gainSignKnown && Math.Abs(actualProSimDelta) > 0.5)
                    {
                        if (actualProSimDelta < 0)
                            gain = -Math.Abs(gain);
                        gainSignKnown = true;
                        probeDirection = gain < 0 ? -1 : 1;
                        log($"[{_name}] Gain sign determined: {gain:F2} enc/ProSim");
                    }
                    else if (!gainSignKnown)
                    {
                        gain = -Math.Abs(gain);
                        probeDirection = gain < 0 ? -1 : 1;
                        log($"[{_name}] Probe inconclusive, flipping sign: {gain:F2}");
                    }
                }

                log($"[{_name}] WARNING: Max iterations reached. Final position: {getProSimValue():F1}");
            }
            catch (OperationCanceledException)
            {
                _axisMovement.Stop();
                log($"[{_name}] Centering cancelled");
            }
            catch (Exception ex)
            {
                log($"[{_name}] Error: {ex.Message}");
            }
            finally
            {
                _isCenteringActive = false;
                if (isPitch)
                {
                    var restoredSpeed = _axisVm.Underlying.HydraulicsOn ? configuredCenteringSpeed : 0;
                    SendCenteringSpeed(restoredSpeed);
                }
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
                        _proSimManager.OnSpeedIasChanged -= ProSimManager_OnSpeedIasChanged;
                        _proSimManager.OnIsStallingChanged -= ProSimManager_OnIsStallingChanged;
                    }
                    else if (string.Equals(_name, "Roll", StringComparison.OrdinalIgnoreCase))
                    {
                        _proSimManager.OnRollCmdChanged -= ProSimManager_OnAutopilotChanged;
                        _proSimManager.OnAileronRightChanged -= ProSimManager_OnFlightControlChanged;
                        _proSimManager.OnTrimAileronChanged -= ProSimManager_OnTrimChanged;
                    }
                    else if (string.Equals(_name, "Rudder", StringComparison.OrdinalIgnoreCase))
                    {
                        _proSimManager.OnTrimRudderChanged -= ProSimManager_OnTrimChanged;
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
