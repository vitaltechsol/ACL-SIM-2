using System;
using System.ComponentModel;
using System.Threading;
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
        private readonly Func<double> _getProSimValue;
        private double _calibrationDisplayOffset;
        private double _testStartEncoder;
        private bool _isPositionTestEnabled;
        private bool _isHydraulicTestEnabled;
        private bool _isCalibrationMode;
        private double _targetPosition = 0.0;
        private readonly string _originalRS485Ip;
        private readonly int _originalDriverId;
        private CancellationTokenSource? _verificationCts;
        private string _toastMessage = string.Empty;
        private bool _showToast;
        private CancellationTokenSource? _toastCts;
        private bool _calibrationPerformed;

        public AxisSetupViewModel(AxisViewModel axisVm, AxisManager? axisManager = null, Func<double>? getProSimValue = null)
        {
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            _axisManager = axisManager;
            _getProSimValue = getProSimValue ?? (() => 512.0);
            AxisName = _axisVm.Name;

            // Store original connection settings to detect changes
            _originalRS485Ip = _axisVm.Underlying.Settings.RS485Ip;
            _originalDriverId = _axisVm.Underlying.Settings.DriverId;

            // Subscribe to AxisViewModel PropertyChanged to update encoder position in real-time
            _axisVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AxisViewModel.EncoderPosition))
                {
                    OnPropertyChanged(nameof(EncoderPosition));
                }
            };

            // Initialize values from underlying settings
            LoadSettings();

            SetCenterCommand = new RelayCommand(_ => SetCenter());
            SetFullRightCommand = new RelayCommand(_ => SetFullRight());
            SetFullLeftCommand = new RelayCommand(_ => SetFullLeft());
            ToggleReversedCommand = new RelayCommand(_ => ToggleReversed());
            SaveCommand = new RelayCommand(_ => Save());
            TogglePositionTestCommand = new RelayCommand(_ => TogglePositionTest());
            ToggleHydraulicTestCommand = new RelayCommand(_ => ToggleHydraulicTest());
            ToggleCalibrateCommand = new RelayCommand(_ => ToggleCalibrate());
            CloseCommand = new RelayCommand(o => CloseAction?.Invoke());
        }

        public string AxisName { get; }

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
            }
        }

        public int MotorSpeedRpm
        {
            get => Settings.MotorSpeedRpm;
            set
            {
                if (Settings.MotorSpeedRpm == value) return;
                Settings.MotorSpeedRpm = value;
                OnPropertyChanged(nameof(MotorSpeedRpm));
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

                if (value)
                {
                    // Test mode ENABLED: Start movement to current target position
                    // GoToPosition now handles the continuous control loop internally
                    if (_axisManager != null)
                    {
                        _axisManager.GoToPosition(_targetPosition);

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
                    // Test mode DISABLED: Stop movement by sending 0% target
                    // This cancels any ongoing movement
                    if (_axisManager != null)
                    {
                        _axisManager.Movement.Stop();
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

                // If test mode is enabled, send new target immediately
                // GoToPosition will cancel previous movement and start new one
                if (IsPositionTestEnabled && _axisManager != null)
                {
                    _axisManager.GoToPosition(_targetPosition);

                    // If moving to non-zero position, start automatic verification
                    if (Math.Abs(_targetPosition) > 0.01)
                    {
                        StartAutomaticVerification();
                    }
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
                Settings.Enabled = loaded.Enabled;
                Settings.RS485Ip = loaded.RS485Ip;
                Settings.DriverId = loaded.DriverId;
                Settings.AirspeedAdditionalTorquePercent = loaded.AirspeedAdditionalTorquePercent;
                Settings.StallAdditionalTorquePercent = loaded.StallAdditionalTorquePercent;

                OnPropertyChanged(string.Empty);
            }
        }

        private void Save()
        {
            SettingsService.SaveAxisSettings(AxisName, Settings);

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
            OnPropertyChanged(nameof(Settings));

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
            OnPropertyChanged(nameof(Settings));

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
                // Exiting calibration mode (Done): auto-detect motor direction
                Reversed = Settings.FullLeftPosition > Settings.FullRightPosition;
                OnPropertyChanged(nameof(Reversed));

                // Restore EncoderCenterOffset to the raw encoder value at calibration center
                // so GoToPosition(0) targets the correct physical position.
                // _calibrationDisplayOffset was set to -rawEncoder in SetCenter().
                _axisVm.Underlying.EncoderCenterOffset = -_calibrationDisplayOffset;

                // Clear temporary calibration display offset
                _calibrationDisplayOffset = 0;

                // Exit calibration mode (restores torque via the setter)
                IsCalibrationMode = false;

                _calibrationPerformed = true;

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
                // Entering calibration mode: reset display offset
                _calibrationDisplayOffset = 0;
                IsCalibrationMode = true;
            }
        }

        /// <summary>
        /// Delegates centering to AxisManager.CenterToProSimPositionAsync and shows toast notifications.
        /// Handles AutopilotOn/Off around the centering call.
        /// </summary>
        private async Task CenterAxisViaManagerAsync()
        {
            try
            {
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
                System.Diagnostics.Debug.WriteLine(
                    $"[{AxisName}] Post-calibration centering failed: {ex.Message}");
                ShowToastNotification($"⚠ Centering failed: {ex.Message}");
            }
            finally
            {
                _axisVm.Underlying.AutopilotOn = false;
                _axisVm.Underlying.RecalculateTorqueTarget();
                _axisVm.NotifyPropertyChanged(nameof(AxisViewModel.EncoderPosition));
            }
        }

        private void PreviewTorque()
        {
            // Recalculate torque for current encoder and updated settings for preview
            _axisVm.Underlying.RecalculateTorqueTarget();
            OnPropertyChanged(nameof(EncoderPosition));
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
    }
}
