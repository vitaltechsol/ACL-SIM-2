using System;
using System.ComponentModel;
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
        private double _centerEncoder;
        private double _testStartEncoder;
        private bool _isTesting;

        public AxisSetupViewModel(AxisViewModel axisVm)
        {
            _axisVm = axisVm ?? throw new ArgumentNullException(nameof(axisVm));
            AxisName = _axisVm.Name;

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
            SetMaxCommand = new RelayCommand(_ => SetMax());
            SetMinCommand = new RelayCommand(_ => SetMin());
            ToggleReversedCommand = new RelayCommand(_ => ToggleReversed());
            SaveCommand = new RelayCommand(_ => Save());
            StartMovementTestCommand = new RelayCommand(_ => StartMovementTest());
            VerifyMovementTestCommand = new RelayCommand(_ => VerifyMovementTest());
            CloseCommand = new RelayCommand(o => CloseAction?.Invoke());
        }

        public string AxisName { get; }

        public double EncoderPosition => _axisVm.EncoderPosition;

        public AxisSettings Settings => _axisVm.Underlying.Settings;

        // Bindable proxies to common settings (UI sliders expect 0..100 ranges)
        public double MinTorqueDisplay
        {
            get => Settings.MinTorqueDisplay;
            set
            {
                if (Settings.MinTorqueDisplay == value) return;
                Settings.MinTorqueDisplay = value;
                OnPropertyChanged(nameof(MinTorqueDisplay));
                PreviewTorque();
            }
        }

        public double MaxTorqueDisplay
        {
            get => Settings.MaxTorqueDisplay;
            set
            {
                if (Settings.MaxTorqueDisplay == value) return;
                Settings.MaxTorqueDisplay = value;
                OnPropertyChanged(nameof(MaxTorqueDisplay));
                PreviewTorque();
            }
        }

        public double MovingTorqueDisplay
        {
            get => Settings.MovingTorqueDisplay;
            set
            {
                if (Settings.MovingTorqueDisplay == value) return;
                Settings.MovingTorqueDisplay = value;
                OnPropertyChanged(nameof(MovingTorqueDisplay));
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

        public double HydraulicOffTorqueDisplay
        {
            get => Settings.HydraulicOffTorqueDisplay;
            set
            {
                if (Settings.HydraulicOffTorqueDisplay == value) return;
                Settings.HydraulicOffTorqueDisplay = value;
                OnPropertyChanged(nameof(HydraulicOffTorqueDisplay));
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

        public ICommand SetCenterCommand { get; }
        public ICommand SetMaxCommand { get; }
        public ICommand SetMinCommand { get; }
        public ICommand ToggleReversedCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand StartMovementTestCommand { get; }
        public ICommand VerifyMovementTestCommand { get; }
        public ICommand CloseCommand { get; }

        // An action the Window can set to close itself when VM requests
        public Action? CloseAction { get; set; }

        public event Action<string, string>? OnSettingsSaved; // axis name, RS485 IP

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void LoadSettings()
        {
            var loaded = SettingsService.LoadAxisSettings(AxisName);
            if (loaded != null)
            {
                // copy loaded values into existing settings instance
                Settings.MinPosition = loaded.MinPosition;
                Settings.MaxPosition = loaded.MaxPosition;
                Settings.MinTorqueDisplay = loaded.MinTorqueDisplay;
                Settings.MaxTorqueDisplay = loaded.MaxTorqueDisplay;
                Settings.ReversedMotor = loaded.ReversedMotor;
                Settings.MovingTorqueDisplay = loaded.MovingTorqueDisplay;
                Settings.SelfCenteringSpeed = loaded.SelfCenteringSpeed;
                Settings.Dampening = loaded.Dampening;
                Settings.HydraulicOffTorqueDisplay = loaded.HydraulicOffTorqueDisplay;
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

            // Notify that settings have changed (especially RS485 IP or DriverId)
            OnSettingsSaved?.Invoke(AxisName, Settings.RS485Ip);

            MessageBox.Show("Settings saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetCenter()
        {
            _centerEncoder = _axisVm.EncoderPosition;
            Settings.CenterPosition = _centerEncoder;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Center set to encoder {_centerEncoder}", "Center", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetMax()
        {
            var val = _axisVm.EncoderPosition - _centerEncoder;
            Settings.MaxPosition = val;
            // If max is less than min, mark reversed
            if (Settings.MaxPosition < Settings.MinPosition) Settings.ReversedMotor = true;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Max position saved: {Settings.MaxPosition}", "Max", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetMin()
        {
            var val = _axisVm.EncoderPosition - _centerEncoder;
            Settings.MinPosition = val;
            if (Settings.MaxPosition < Settings.MinPosition) Settings.ReversedMotor = true;
            OnPropertyChanged(nameof(Settings));
            MessageBox.Show($"Min position saved: {Settings.MinPosition}", "Min", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ToggleReversed()
        {
            Settings.ReversedMotor = !Settings.ReversedMotor;
            OnPropertyChanged(nameof(Reversed));
        }

        private void PreviewTorque()
        {
            // Recalculate torque for current encoder and updated settings for preview
            _axisVm.Underlying.RecalculateTorqueTarget();
            OnPropertyChanged(nameof(EncoderPosition));
        }

        private void StartMovementTest()
        {
            _testStartEncoder = _axisVm.EncoderPosition;
            _isTesting = true;
            MessageBox.Show("Movement test started. Use the movement controls or push the axis and then press Verify.", "Test", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void VerifyMovementTest()
        {
            if (!_isTesting)
            {
                MessageBox.Show("Start test before verifying.", "Test", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var delta = Math.Abs(_axisVm.EncoderPosition - _testStartEncoder);
            _isTesting = false;
            if (delta < 1e-3)
            {
                MessageBox.Show("Encoder did not change. The moving torque may be too low.", "Test Result", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"Encoder changed by {delta}. Movement detected.", "Test Result", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
