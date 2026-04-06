using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ACL_SIM_2.Models;
using ACL_SIM_2.Services;

namespace ACL_SIM_2.ViewModels
{
    public class AxisSetupViewModel : INotifyPropertyChanged
    {
        private readonly AxisViewModel _axisVm;
        private readonly AxisManager? _axisManager;
        private readonly ProSimManager? _proSimManager;
        private readonly Func<double> _getProSimValue;
        private double _calibrationDisplayOffset;
        private double _testStartEncoder;
        private bool _isPositionTestEnabled;
        private bool _isHydraulicTestEnabled;
        private bool _isFullLeftSet;
        private bool _isFullRightSet;
        private bool _isCalibrationMode;
        private double _targetPosition = 0.0;
        private readonly string _originalRS485Ip;
        private readonly int _originalDriverId;
        private CancellationTokenSource? _verificationCts;
        private CancellationTokenSource? _trackingTestCts;
        private string _toastMessage = string.Empty;
        private bool _showToast;
        private CancellationTokenSource? _toastCts;
        private bool _calibrationPerformed;
        private bool _isCenterSet;
        private AxisSettings _savedSettingsSnapshot = new AxisSettings();
        private DispatcherTimer? _proSimTimer;
        private EventHandler? _proSimTimerTickHandler;
        private double _lastProSimRaw = double.NaN;
        private PropertyChangedEventHandler? _axisVmPropertyChangedHandler;

        public AxisSetupViewModel(AxisViewModel axisVm, AxisManager? axisManager = null, Func<double>? getProSimValue = null, ProSimManager? proSimManager = null)
        {
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            _axisManager = axisManager;
            _proSimManager = proSimManager;
            _getProSimValue = getProSimValue ?? (() => 512.0);
            AxisName = _axisVm.Name;

            // Start a timer to refresh the ProSimPosition display periodically
            try
            {
                _proSimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _proSimTimerTickHandler = (s, e) =>
                {
                    OnPropertyChanged(nameof(ProSimPosition));
                    OnPropertyChanged(nameof(ProSimPositionIsGood));
                };
                _proSimTimer.Tick += _proSimTimerTickHandler;
                _proSimTimer.Start();
            }
            catch
            {
                // Ignore if dispatcher not available in some test contexts
            }

            // Store original connection settings to detect changes
            _originalRS485Ip = _axisVm.Underlying.Settings.RS485Ip;
            _originalDriverId = _axisVm.Underlying.Settings.DriverId;

            // Subscribe to AxisViewModel PropertyChanged to update encoder position in real-time
            _axisVmPropertyChangedHandler = new PropertyChangedEventHandler((s, e) =>
            {
                if (e.PropertyName == nameof(AxisViewModel.EncoderPosition))
                {
                    OnPropertyChanged(nameof(EncoderPosition));
                }
                else if (e.PropertyName == nameof(AxisViewModel.IsCentering))
                {
                    OnPropertyChanged(nameof(IsCentering));
                }
            });
            _axisVm.PropertyChanged += _axisVmPropertyChangedHandler;

            // ProSim mapped position property (bindable) is implemented below

            // Initialize values from underlying settings
            LoadSettings();

            SetCenterCommand = new RelayCommand(_ => SetCenter());
            SetFullRightCommand = new RelayCommand(_ => SetFullRight(), _ => _isFullLeftSet);
            SetFullLeftCommand = new RelayCommand(_ => SetFullLeft(), _ => _isCenterSet);
            ToggleReversedCommand = new RelayCommand(_ => ToggleReversed());
            SaveCommand = new RelayCommand(_ => Save());
            TogglePositionTestCommand = new RelayCommand(_ => TogglePositionTest());
            ToggleHydraulicTestCommand = new RelayCommand(_ => ToggleHydraulicTest());
            ToggleCalibrateCommand = new RelayCommand(_ => ToggleCalibrate());
            CloseCommand = new RelayCommand(o => CloseAction?.Invoke());
        }

        public string AxisName { get; }

        public double ProSimPosition
        {
            get
            {
                try
                {
                    var raw = _getProSimValue();
                    // Map 0..1024 to -100..100 where 0 -> -100, 512 -> 0, 1024 -> 100
                    var mapped = (raw - 512.0) / 512.0 * 100.0;
                    return mapped;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        public double EncoderPosition => IsCalibrationMode
            ? _axisVm.EncoderPosition + _calibrationDisplayOffset
            : _axisVm.EncoderPosition;

        public AxisSettings Settings => _axisVm.Underlying.Settings;

        // Bindable proxies to common settings (UI sliders expect 0..100 ranges)
        public double MinTorquePercent
        {
            get => Settings.MinTorquePercent;
            set
            {
                if (Settings.MinTorquePercent == value) return;
                Settings.MinTorquePercent = value;
                OnPropertyChanged(nameof(MinTorquePercent));
                PreviewTorque();
            }
        }

        public double MaxTorquePercent
        {
            get => Settings.MaxTorquePercent;
            set
            {
                if (Settings.MaxTorquePercent == value) return;
                Settings.MaxTorquePercent = value;
                OnPropertyChanged(nameof(MaxTorquePercent));
                PreviewTorque();
            }
        }

        public double MovingTorquePercentage
        {
            get => Settings.MovingTorquePercentage;
            set
            {
                if (Settings.MovingTorquePercentage == value) return;
                Settings.MovingTorquePercentage = value;
                OnPropertyChanged(nameof(MovingTorquePercentage));
                ApplyMotionTuningPreview();
            }
        }

        public int MotorSpeedRpm
        {
            get => Settings.MotorSpeedRpm;
            set
            {
                var clamped = Math.Max(1, Math.Min(50, value));
                if (Settings.MotorSpeedRpm == clamped) return;

                Settings.MotorSpeedRpm = clamped;
                OnPropertyChanged(nameof(MotorSpeedRpm));
                ApplyMotionTuningPreview();
            }
        }

        public double MotionSmoothingPercent
        {
            get => (1.0 - Settings.TargetFilterAlpha) * 100.0;
            set
            {
                var clamped = Math.Max(0.0, Math.Min(100.0, value));
                var alpha = 1.0 - (clamped / 100.0);
                if (Math.Abs(Settings.TargetFilterAlpha - alpha) < 0.0001) return;

                Settings.TargetFilterAlpha = alpha;
                OnPropertyChanged(nameof(MotionSmoothingPercent));
                ApplyMotionTuningPreview();
            }
        }

        public int MinMotorCommandIntervalMs
        {
            get => Settings.MinMotorCommandIntervalMs;
            set
            {
                var clamped = Math.Max(0, Math.Min(1000, value));
                if (Settings.MinMotorCommandIntervalMs == clamped) return;

                Settings.MinMotorCommandIntervalMs = clamped;
                OnPropertyChanged(nameof(MinMotorCommandIntervalMs));
                ApplyMotionTuningPreview();
            }
        }

        public int MotorAccelParam1Ms
        {
            get => Settings.MotorAccelParam1Ms;
            set
            {
                var max = 3000;
                var clamped = Math.Max(50, Math.Min(max, value));
                if (Settings.MotorAccelParam1Ms == clamped) return;

                Settings.MotorAccelParam1Ms = clamped;
                OnPropertyChanged(nameof(MotorAccelParam1Ms));
                ApplyMotionTuningPreview();
            }
        }

        public int MotorAccelParam2Ms
        {
            get => Settings.MotorAccelParam2Ms;
            set
            {
                var clamped = Math.Max(0, Math.Min(1500, value));
                if (Settings.MotorAccelParam2Ms == clamped) return;

                Settings.MotorAccelParam2Ms = clamped;
                OnPropertyChanged(nameof(MotorAccelParam2Ms));
                ApplyMotionTuningPreview();
            }
        }

        public double SelfCenteringSpeed
        {
            get => Settings.SelfCenteringSpeed;
            set
            {
                if (Settings.SelfCenteringSpeed == value) return;
                Settings.SelfCenteringSpeed = value;
                OnPropertyChanged(nameof(SelfCenteringSpeed));

                _axisManager?.SendCenteringSpeed(AxisSettings.ConvertCenteringSpeedToActual(value));
            }
        }

        public double Dampening
        {
            get => Settings.Dampening;
            set
            {
                if (Settings.Dampening == value) return;
                Settings.Dampening = value;
                OnPropertyChanged(nameof(Dampening));

                _axisManager?.SendDampening(AxisSettings.ConvertDampeningToActual(value));
            }
        }

        public double HydraulicOffTorquePercent
        {
            get => Settings.HydraulicOffTorquePercent;
            set
            {
                if (Settings.HydraulicOffTorquePercent == value) return;
                Settings.HydraulicOffTorquePercent = value;
                OnPropertyChanged(nameof(HydraulicOffTorquePercent));
            }
        }

        public double AutopilotOverridePercent
        {
            get => Settings.AutopilotOverridePercent;
            set
            {
                if (Settings.AutopilotOverridePercent == value) return;
                Settings.AutopilotOverridePercent = value;
                OnPropertyChanged(nameof(AutopilotOverridePercent));
            }
        }

        public double AirspeedAdditionalTorquePercent
        {
            get => Settings.AirspeedAdditionalTorquePercent;
            set
            {
                var clamped = Math.Max(1.0, Math.Min(50.0, value));
                if (Math.Abs(Settings.AirspeedAdditionalTorquePercent - clamped) < 0.0001) return;

                Settings.AirspeedAdditionalTorquePercent = clamped;
                OnPropertyChanged(nameof(AirspeedAdditionalTorquePercent));
            }
        }

        // Communication / driver settings
        public string RS485Ip
        {
            get => Settings.RS485Ip;
            set
            {
                if (Settings.RS485Ip == value) return;
                Settings.RS485Ip = value;
                OnPropertyChanged(nameof(RS485Ip));
            }
        }

        public int DriverId
        {
            get => Settings.DriverId;
            set
            {
                if (Settings.DriverId == value) return;
                Settings.DriverId = value;
                OnPropertyChanged(nameof(DriverId));
            }
        }

        public bool Reversed
        {
            get => Settings.ReversedMotor;
            set
            {
                if (Settings.ReversedMotor == value) return;
                Settings.ReversedMotor = value;
                OnPropertyChanged(nameof(Reversed));
            }
        }

        // Position test mode properties
        public bool IsPositionTestEnabled
        {
            get => _isPositionTestEnabled;
            set
            {
                if (_isPositionTestEnabled == value) return;
                _isPositionTestEnabled = value;
                OnPropertyChanged(nameof(IsPositionTestEnabled));

                // Set MotorIsMoving flag to use fixed MovingTorqueDisplay during test mode
                _axisVm.Underlying.MotorIsMoving = value;

                _axisVm.Underlying.RecalculateTorqueTarget();
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));

                if (value)
                {
                    // Test mode ENABLED: suspend the ProSim AP loop so it cannot fight the test slider,
                    // restore configured centering speed so the motor can move even if hydraulics are off.
                    if (_axisManager != null)
                    {
                        _axisManager.SuspendForPositionTest();

                        var configuredSpeed = AxisSettings.ConvertCenteringSpeedToActual(Settings.SelfCenteringSpeed);
                        _axisManager.SendCenteringSpeed(configuredSpeed);

                        StartTrackingTestLoop();

                        // If target position is not 0, start automatic verification
                        if (Math.Abs(_targetPosition) > 0.01)
                        {
                            StartAutomaticVerification();
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[{AxisName}] ERROR: AxisManager is null! Cannot test position control.");
                        System.Diagnostics.Debug.WriteLine($"[{AxisName}] Make sure the axis is ENABLED and has a valid RS485 IP configured.");
                        ShowToastNotification("⚠ Cannot test position control. Axis must be enabled with valid RS485 IP.");
                    }
                }
                else
                {
                    // Test mode DISABLED: Stop tracking loop and movement
                    StopTrackingTestLoop();

                    if (_axisManager != null)
                    {
                        _axisManager.Movement.Stop();
                        _axisManager.ResumeAfterPositionTest();

                        // Restore the hydraulics-correct centering speed now that test mode is done.
                        var hydraulicsOn = _axisVm.Underlying.HydraulicsOn;
                        var restoredSpeed = hydraulicsOn
                            ? AxisSettings.ConvertCenteringSpeedToActual(Settings.SelfCenteringSpeed)
                            : 0;
                        _axisManager.SendCenteringSpeed(restoredSpeed);
                    }

                    // Cancel any ongoing verification
                    _verificationCts?.Cancel();

                    // Reset target to 0 for next time
                    TargetPosition = 0.0;
                }
            }
        }

        // Hydraulic test mode properties
        public bool IsHydraulicTestEnabled
        {
            get => _isHydraulicTestEnabled;
            set
            {
                if (_isHydraulicTestEnabled == value) return;
                _isHydraulicTestEnabled = value;
                OnPropertyChanged(nameof(IsHydraulicTestEnabled));

                // Set HydraulicsOn flag to false during test mode to apply hydraulic off torque
                _axisVm.Underlying.HydraulicsOn = !value;

                // Recalculate torque to apply the new hydraulics state
                _axisVm.Underlying.RecalculateTorqueTarget();

                // Notify UI of hydraulics state change
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.HydraulicsOn));
            }
        }

        public double TargetPosition
        {
            get => _targetPosition;
            set
            {
                if (Math.Abs(_targetPosition - value) < 0.01) return;
                _targetPosition = value;
                OnPropertyChanged(nameof(TargetPosition));

                // Target will be picked up by the tracking loop on next iteration
                // No need to manually call anything here - the loop handles it

                // If moving to non-zero position and test is enabled, start automatic verification
                if (IsPositionTestEnabled && Math.Abs(_targetPosition) > 0.01)
                {
                    StartAutomaticVerification();
                }
            }
        }

        public bool IsCalibrationMode
        {
            get => _isCalibrationMode;
            set
            {
                if (_isCalibrationMode == value) return;
                _isCalibrationMode = value;
                OnPropertyChanged(nameof(IsCalibrationMode));
                OnPropertyChanged(nameof(ShowSetCenterButton));
                OnPropertyChanged(nameof(ShowSetFullLeftButton));
                OnPropertyChanged(nameof(ShowSetFullRightButton));
                OnPropertyChanged(nameof(ShowDoneLabel));
                OnPropertyChanged(nameof(ProSimPositionIsGood));

                // Set CalibrationMode flag on the underlying Axis to set torque to 0
                _axisVm.Underlying.CalibrationMode = value;

                // Recalculate torque to apply the new calibration mode state (torque = 0 when active)
                _axisVm.Underlying.RecalculateTorqueTarget();

                // Force AxisManager to send updated torque to motor immediately.
                // Without this, UpdateTorqueAsync only fires on encoder changes, but the
                // motor may be holding position (torque != 0) preventing encoder movement.
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));
            }
        }

        public string ToastMessage
        {
            get => _toastMessage;
            set
            {
                if (_toastMessage == value) return;
                _toastMessage = value;
                OnPropertyChanged(nameof(ToastMessage));
            }
        }

        public bool ShowToast
        {
            get => _showToast;
            set
            {
                if (_showToast == value) return;
                _showToast = value;
                OnPropertyChanged(nameof(ShowToast));
            }
        }

        public bool IsCentering => _axisVm.IsCentering;

        public bool IsCenterSet
        {
            get => _isCenterSet;
            private set
            {
                if (_isCenterSet == value) return;
                _isCenterSet = value;
                OnPropertyChanged(nameof(IsCenterSet));
                OnPropertyChanged(nameof(ShowSetCenterButton));
                OnPropertyChanged(nameof(ShowSetFullLeftButton));
                OnPropertyChanged(nameof(ShowSetFullRightButton));
                OnPropertyChanged(nameof(ShowDoneLabel));
                OnPropertyChanged(nameof(ProSimPositionIsGood));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsFullLeftSet
        {
            get => _isFullLeftSet;
            private set
            {
                if (_isFullLeftSet == value) return;
                _isFullLeftSet = value;
                OnPropertyChanged(nameof(IsFullLeftSet));
                OnPropertyChanged(nameof(ShowSetCenterButton));
                OnPropertyChanged(nameof(ShowSetFullLeftButton));
                OnPropertyChanged(nameof(ShowSetFullRightButton));
                OnPropertyChanged(nameof(ShowDoneLabel));
                OnPropertyChanged(nameof(ProSimPositionIsGood));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool ShowSetCenterButton => IsCalibrationMode && !IsCenterSet;
        public bool ShowSetFullLeftButton => IsCalibrationMode && IsCenterSet && !IsFullLeftSet;
        public bool ShowSetFullRightButton => IsCalibrationMode && IsCenterSet && IsFullLeftSet && !_isFullRightSet;
        public bool ShowDoneLabel => IsCalibrationMode && _isFullRightSet;

        public bool ProSimPositionIsGood
        {
            get
            {
                if (!IsCalibrationMode) return true;
                var pos = ProSimPosition;
                if (!IsCenterSet) return Math.Abs(pos) <= 0.8;
                if (!IsFullLeftSet) return Math.Abs(pos + 100.0) <= 2.0;
                return Math.Abs(pos - 100.0) <= 2.0;
            }
        }

        public ICommand SetCenterCommand { get; }
        public ICommand SetFullRightCommand { get; }
        public ICommand SetFullLeftCommand { get; }
        public ICommand ToggleReversedCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand TogglePositionTestCommand { get; }
        public ICommand ToggleHydraulicTestCommand { get; }
        public ICommand ToggleCalibrateCommand { get; }
        public ICommand CloseCommand { get; }

        // An action the Window can set to close itself when VM requests
        public Action? CloseAction { get; set; }

        public event Action<string, string, bool>? OnSettingsSaved; // axis name, RS485 IP, connectionSettingsChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void DisposeTimerIfNeeded()
        {
            try
            {
                if (_proSimTimer != null && _proSimTimerTickHandler != null)
                {
                    _proSimTimer.Tick -= _proSimTimerTickHandler;
                    _proSimTimer.Stop();
                    _proSimTimer = null;
                    _proSimTimerTickHandler = null;
                }

                if (_axisVm != null && _axisVmPropertyChangedHandler != null)
                {
                    _axisVm.PropertyChanged -= _axisVmPropertyChangedHandler;
                    _axisVmPropertyChangedHandler = null;
                }
            }
            catch
            {
                // ignore cleanup exceptions
            }
        }

        private void LoadSettings()
        {
            var loaded = SettingsService.LoadAxisSettings(AxisName);
            if (loaded != null)
            {
                // copy loaded values into existing settings instance
                Settings.CenterPosition = loaded.CenterPosition;
                Settings.FullLeftPosition = loaded.FullLeftPosition;
                Settings.FullRightPosition = loaded.FullRightPosition;
                Settings.MinTorquePercent = loaded.MinTorquePercent;
                Settings.MaxTorquePercent = loaded.MaxTorquePercent;
                Settings.ReversedMotor = loaded.ReversedMotor;
                Settings.MovingTorquePercentage = loaded.MovingTorquePercentage;
                Settings.SelfCenteringSpeed = loaded.SelfCenteringSpeed;
                Settings.Dampening = loaded.Dampening;
                Settings.HydraulicOffTorquePercent = loaded.HydraulicOffTorquePercent;
                Settings.AutopilotOverridePercent = loaded.AutopilotOverridePercent;
                Settings.OutputIntervalMs = loaded.OutputIntervalMs;
                Settings.SpeedRateLimitPercent = loaded.SpeedRateLimitPercent;
                Settings.InputIntervalMs = loaded.InputIntervalMs;
                Settings.TargetFilterAlpha = loaded.TargetFilterAlpha;
                Settings.MinMotorCommandIntervalMs = loaded.MinMotorCommandIntervalMs;
                Settings.MotorSpeedRpm = loaded.MotorSpeedRpm;
                Settings.MotorAccelMode = loaded.MotorAccelMode;
                Settings.MotorAccelParam1Ms = loaded.MotorAccelParam1Ms;
                Settings.MotorAccelParam2Ms = loaded.MotorAccelParam2Ms;
                Settings.Enabled = loaded.Enabled;
                Settings.RS485Ip = loaded.RS485Ip;
                Settings.DriverId = loaded.DriverId;
                Settings.AirspeedAdditionalTorquePercent = Math.Max(1.0, Math.Min(50.0, loaded.AirspeedAdditionalTorquePercent));
                Settings.StallAdditionalTorquePercent = loaded.StallAdditionalTorquePercent;

                OnPropertyChanged(string.Empty);
            }

            _savedSettingsSnapshot = Settings.Clone();
        }

        private void Save()
        {
            Settings.AirspeedAdditionalTorquePercent = Math.Max(1.0, Math.Min(50.0, Settings.AirspeedAdditionalTorquePercent));
            SettingsService.SaveAxisSettings(AxisName, Settings);
            _savedSettingsSnapshot = Settings.Clone();

            // Check if connection-related settings changed
            var connectionSettingsChanged = Settings.RS485Ip != _originalRS485Ip || Settings.DriverId != _originalDriverId;

            // Notify that settings have changed
            OnSettingsSaved?.Invoke(AxisName, Settings.RS485Ip, connectionSettingsChanged);

            // Notify AxisViewModel to recalculate EncoderPercentage in case calibration settings changed
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));

            ShowToastNotification("✓ Settings saved successfully");
        }

        private void SetCenter()
        {
            var rawEncoder = _axisVm.EncoderPosition;

            // Temporary display offset so encoder reads 0 at center during calibration
            _calibrationDisplayOffset = -rawEncoder;

            // CenterPosition is always 0 (raw encoder at center is not persisted)
            Settings.CenterPosition = 0;

            // Set offset to current raw encoder so all position calculations
            // (EncoderPercentage, torque, PercentToEncoderPosition) remain correct.
            _axisVm.Underlying.EncoderCenterOffset = rawEncoder;

            IsCenterSet = true;
            IsFullLeftSet = false;
            _isFullRightSet = false;
            OnPropertyChanged(nameof(ShowDoneLabel));

            OnPropertyChanged(nameof(EncoderPosition));
            OnPropertyChanged(nameof(Settings));

            // Notify AxisViewModel to recalculate EncoderPercentage
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));

            ShowToastNotification($"✓ Center set encoder centered position)");
        }

        private void SetFullRight()
        {
            // Store relative distance from center (positive value)
            var relativePosition = _axisVm.EncoderPosition + _calibrationDisplayOffset;
            Settings.FullRightPosition = relativePosition;
            _isFullRightSet = true;
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(ShowSetFullRightButton));
            OnPropertyChanged(nameof(ShowDoneLabel));
            CommandManager.InvalidateRequerySuggested();

            // Notify AxisViewModel to recalculate EncoderPercentage
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));

            ShowToastNotification($"✓ Full right position saved: {relativePosition:F2}");
        }

        private void SetFullLeft()
        {
            // Store relative distance from center (negative value)
            var relativePosition = _axisVm.EncoderPosition + _calibrationDisplayOffset;
            Settings.FullLeftPosition = relativePosition;
            IsFullLeftSet = true;
            _isFullRightSet = false;
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(ShowDoneLabel));

            // Notify AxisViewModel to recalculate EncoderPercentage
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));

            ShowToastNotification($"✓ Full left position saved: {relativePosition:F2}");
        }

        private void ToggleReversed()
        {
            Settings.ReversedMotor = !Settings.ReversedMotor;
            OnPropertyChanged(nameof(Reversed));
        }

        private void ToggleCalibrate()
        {
            if (IsCalibrationMode)
            {
                // Exiting calibration mode (Done): only mark reversed when both endpoints were
                // captured this session and full left is physically greater than full right.
                if (_isFullRightSet && Settings.FullLeftPosition > Settings.FullRightPosition)
                {
                    Settings.FullLeftPosition *= -1;
                    Settings.FullRightPosition *= -1;
                    Reversed = true;
                    OnPropertyChanged(nameof(Reversed));
                    OnPropertyChanged(nameof(Settings));
                }

                // Restore EncoderCenterOffset to the raw encoder value at calibration center
                // so GoToPosition(0) targets the correct physical position.
                // _calibrationDisplayOffset was set to -rawEncoder in SetCenter().
                _axisVm.Underlying.EncoderCenterOffset = -_calibrationDisplayOffset;

                // Clear temporary calibration display offset
                _calibrationDisplayOffset = 0;

                // Exit calibration mode (restores torque via the setter)
                IsCalibrationMode = false;

                _calibrationPerformed = true;

                // Auto-save calibration results so the snapshot is up to date
                // and CleanupAndCenterAsync won't restore stale values on close.
                SettingsService.SaveAxisSettings(AxisName, Settings);
                _savedSettingsSnapshot = Settings.Clone();

                // Center the axis via AxisManager so EncoderCenterOffset is set from the actual encoder
                if (_axisManager != null)
                {
                    ShowToastNotification("↻ Centering axis…");
                    _ = CenterAxisViaManagerAsync();
                }
                else
                {
                    ShowToastNotification("⚠ Cannot center – axis not connected");
                }
            }
            else
            {
                // Entering calibration mode: reset display offset and all calibration step flags
                _calibrationDisplayOffset = 0;
                IsCenterSet = false;
                IsFullLeftSet = false;
                _isFullRightSet = false;
                IsCalibrationMode = true;
            }
        }

        /// <summary>
        /// Delegates centering to AxisManager.CenterToProSimPositionAsync and shows toast notifications.
        /// Handles AutopilotOn/Off around the centering call.
        /// </summary>
        private async Task CenterAxisViaManagerAsync()
        {
            var simWasAlreadyPaused = false;
            try
            {
                TryDisengageMcpAutopilot();

                // Capture whether the sim was already paused before we touch it
                simWasAlreadyPaused = _proSimManager?.Pause ?? false;

                // Show centering overlay and enable movement torque
                _axisVm.Underlying.MotorIsMoving = true;
                _axisVm.IsCentering = true;
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
                OnPropertyChanged(nameof(IsCentering));

                // Pause the sim only if it wasn't already paused
                if (!simWasAlreadyPaused)
                {
                    try { _proSimManager?.PauseSim(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{AxisName}] Failed to pause sim: {ex.Message}"); }
                }

                // Enable AutopilotOn so AxisManager sends movement torque to the motor
                _axisVm.Underlying.AutopilotOn = true;
                _axisVm.Underlying.RecalculateTorqueTarget();
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));

                await _axisManager!.CenterToProSimPositionAsync(
                    getProSimValue: _getProSimValue,
                    log: message => System.Diagnostics.Debug.WriteLine($"[{AxisName}] {message}")
                );

                ShowToastNotification("✓ Calibration complete – axis centered");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{AxisName}] Post-calibration centering failed: {ex.Message}");
                ShowToastNotification($"⚠ Centering failed: {ex.Message}");
            }
            finally
            {
                _axisVm.Underlying.AutopilotOn = false;
                _axisVm.Underlying.RecalculateTorqueTarget();
                _axisVm.Underlying.MotorIsMoving = false;
                _axisVm.IsCentering = false;
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));
                OnPropertyChanged(nameof(IsCentering));

                // Only unpause if we were the ones who paused it
                if (!simWasAlreadyPaused)
                {
                    try { _proSimManager?.UnpauseSim(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{AxisName}] Failed to unpause sim: {ex.Message}"); }
                }

                // Clear flag so closing the window doesn't trigger a second centering pass
                _calibrationPerformed = false;
            }
        }

        private void PreviewTorque()
        {
            // Recalculate torque for current encoder and updated settings for preview
            _axisVm.Underlying.RecalculateTorqueTarget();
            OnPropertyChanged(nameof(EncoderPosition));
        }

        private void ApplyMotionTuningPreview()
        {
            _axisManager?.ApplyMotionTuningPreview();
        }

        private void TryDisengageMcpAutopilot()
        {
            try
            {
                _proSimManager?.DisengageAP();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{AxisName}] Failed to disengage MCP AP before centering: {ex.Message}");
            }
        }

        private void RestoreSavedSettingsSnapshot()
        {
            Settings.CopyFrom(_savedSettingsSnapshot);

            OnPropertyChanged(string.Empty);

            _axisVm.Underlying.RecalculateTorqueTarget();
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPercentage));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderNormalized));
            _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));

            ApplyMotionTuningPreview();
        }

        private async void StartAutomaticVerification()
        {
            // Cancel any existing verification
            _verificationCts?.Cancel();
            _verificationCts = new CancellationTokenSource();
            var token = _verificationCts.Token;

            // Record starting encoder position
            _testStartEncoder = _axisVm.EncoderPosition;

            try
            {
                // Wait 3 seconds for motor to respond
                await Task.Delay(3000, token);

                // Only verify if still in test mode and not cancelled
                if (IsPositionTestEnabled && !token.IsCancellationRequested)
                {
                    VerifyMovementTest();
                }
            }
            catch (TaskCanceledException)
            {
                // Verification was cancelled, this is expected when slider moves
            }
        }

        private void VerifyMovementTest()
        {
            var delta = Math.Abs(_axisVm.EncoderPosition - _testStartEncoder);

            if (delta < 1e-3)
            {
                ShowToastNotification("⚠ Encoder did not change. The moving torque may be too low.");
            }
        }

        /// <summary>
        /// Starts a tracking loop that moves the motor toward <see cref="TargetPosition"/>.
        /// When the target changes, the previous servo command is cancelled and the RPM
        /// ramp restarts from 1 RPM. The loop calls <see cref="AxisMovement.MoveToward"/>
        /// every 100 ms to update pulses and RPM.
        /// </summary>
        private void StartTrackingTestLoop()
        {
            StopTrackingTestLoop();
            _trackingTestCts = new CancellationTokenSource();
            var token = _trackingTestCts.Token;

            System.Diagnostics.Debug.WriteLine($"[{AxisName}] Starting tracking test loop");

            Task.Run(async () =>
            {
                const int TRACKING_INTERVAL_MS = 100;

                try
                {
                    var lastTarget = double.NaN;
                    var arrived = true;

                    while (!token.IsCancellationRequested && _axisManager != null)
                    {
                        var target = _targetPosition;

                        // When the target changes, cancel the current move and restart the ramp
                        if (double.IsNaN(lastTarget) || Math.Abs(target - lastTarget) > 0.01)
                        {
                            if (arrived)
                                _axisManager.Movement.BeginMove();
                            else
                                _axisManager.Movement.RefreshMoveTimeout();
                            lastTarget = target;
                            arrived = false;

                            System.Diagnostics.Debug.WriteLine($"[{AxisName}] New target: {target:F2}%");
                        }

                        if (!arrived)
                        {
                            arrived = _axisManager.Movement.MoveToward(target);

                            if (arrived)
                            {
                                _axisManager.Movement.Stop();
                                System.Diagnostics.Debug.WriteLine($"[{AxisName}] Target {target:F2}% reached");
                            }
                        }

                        await Task.Delay(TRACKING_INTERVAL_MS, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[{AxisName}] Tracking test loop cancelled");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{AxisName}] Tracking test loop error: {ex.Message}");
                }
            }, token);
        }

        /// <summary>
        /// Stops the tracking test loop.
        /// </summary>
        private void StopTrackingTestLoop()
        {
            if (_trackingTestCts != null)
            {
                _trackingTestCts.Cancel();
                _trackingTestCts.Dispose();
                _trackingTestCts = null;
                System.Diagnostics.Debug.WriteLine($"[{AxisName}] Tracking test loop stopped");
            }
        }

        private async void ShowToastNotification(string message)
        {
            // Cancel any existing toast
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            ToastMessage = message;
            ShowToast = true;

            try
            {
                // Show toast for 3 seconds
                await Task.Delay(3000, token);
                ShowToast = false;
            }
            catch (TaskCanceledException)
            {
                // Toast was cancelled, hide it immediately
                ShowToast = false;
            }
        }

        private void TogglePositionTest()
        {
            IsPositionTestEnabled = !IsPositionTestEnabled;
        }

        private void ToggleHydraulicTest()
        {
            IsHydraulicTestEnabled = !IsHydraulicTestEnabled;
        }

        public async Task CleanupAndCenterAsync()
        {
            try
            {
                // Stop any active test functions
                if (IsPositionTestEnabled)
                {
                    IsPositionTestEnabled = false;
                }

                if (IsHydraulicTestEnabled)
                {
                    IsHydraulicTestEnabled = false;
                }

                // Turn off calibration mode
                if (IsCalibrationMode)
                {
                    IsCalibrationMode = false;
                }

                // Only re-center if calibration was performed during this session
                if (_calibrationPerformed && _axisManager != null)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[{AxisName}] Centering axis after settings window close...");
                        TryDisengageMcpAutopilot();

                        // Enable AutopilotOn to use Movement Torque during centering
                        _axisVm.Underlying.AutopilotOn = true;
                        _axisVm.Underlying.RecalculateTorqueTarget();
                        _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));

                        await _axisManager.CenterToProSimPositionAsync(
                            getProSimValue: _getProSimValue,
                            log: message => System.Diagnostics.Debug.WriteLine($"[{AxisName}] {message}")
                        );

                        System.Diagnostics.Debug.WriteLine($"[{AxisName}] Centering complete");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[{AxisName}] Centering failed: {ex.Message}");
                    }
                    finally
                    {
                        _axisVm.Underlying.AutopilotOn = false;
                        _axisVm.Underlying.RecalculateTorqueTarget();
                        _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));
                    }
                }
            }
            finally
            {
                RestoreSavedSettingsSnapshot();
            }
        }
    }
}
