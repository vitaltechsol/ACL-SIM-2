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
        private double _centerEncoder;
        private double _testStartEncoder;
        private bool _isPositionTestEnabled;
        private bool _isHydraulicTestEnabled;
        private bool _isCalibrationMode;
        private double _targetPosition = 0.0;
        private readonly string _originalRS485Ip;
        private readonly int _originalDriverId;
        private CancellationTokenSource? _verificationCts;

        public AxisSetupViewModel(AxisViewModel axisVm, AxisManager? axisManager = null)
        {
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            _axisManager = axisManager;
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

        public double EncoderPosition => _axisVm.EncoderPosition;

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

                // Set AutopilotOn flag to use fixed MovingTorqueDisplay during test mode
                _axisVm.Underlying.AutopilotOn = value;

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
                        MessageBox.Show(
                            $"Cannot test position control for {AxisName}.\n\n" +
                            "The axis must be ENABLED with a valid RS485 IP address.\n" +
                            "1. Enable the axis checkbox on the main window\n" +
                            "2. Configure RS485 IP in this setup window\n" +
                            "3. Save settings\n" +
                            "4. Restart the application",
                            "Position Test Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
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

            MessageBox.Show("Settings saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetCenter()
        {
            _centerEncoder = _axisVm.EncoderPosition;
            Settings.CenterPosition = _centerEncoder;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Center set to encoder {_centerEncoder}", "Center", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetFullRight()
        {
            var val = _axisVm.EncoderPosition - _centerEncoder;
            Settings.FullRightPosition = val;
            // If right is less than left, mark reversed
            if (Settings.FullRightPosition < Settings.FullLeftPosition) Settings.ReversedMotor = true;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Full right position saved: {Settings.FullRightPosition}", "Full Right", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetFullLeft()
        {
            var val = _axisVm.EncoderPosition - _centerEncoder;
            Settings.FullLeftPosition = val;
            if (Settings.FullRightPosition < Settings.FullLeftPosition) Settings.ReversedMotor = true;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Full left position saved: {Settings.FullLeftPosition}", "Full Left", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ToggleReversed()
        {
            Settings.ReversedMotor = !Settings.ReversedMotor;
            OnPropertyChanged(nameof(Reversed));
        }

        private void ToggleCalibrate()
        {
            IsCalibrationMode = !IsCalibrationMode;
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
                MessageBox.Show("Encoder did not change. The moving torque may be too low.", "Test Result", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            // Re-center the axis to default position (512.0)
            if (_axisManager != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[{AxisName}] Centering axis after settings window close...");

                    // Enable AutopilotOn to use Movement Torque during centering
                    _axisVm.Underlying.AutopilotOn = true;

                    await _axisManager.CenterToProSimPositionAsync(
                        getProSimValue: () => 512.0, // Center position
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
                    // Disable AutopilotOn after centering
                    _axisVm.Underlying.AutopilotOn = false;
                }
            }
        }
    }
}
