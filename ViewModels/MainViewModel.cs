using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly Services.EncoderManager? _encoderManager;
        public AxisViewModel Pitch { get; }
        public AxisViewModel Roll { get; }
        public AxisViewModel Rudder { get; }
        public AxisViewModel Tiller { get; }

        public ObservableCollection<string> ErrorLog { get; } = new ObservableCollection<string>();

        public ICommand SetupCommand { get; }
        public ICommand GlobalSettingsCommand { get; }

        public MainViewModel()
        {
            // Load per-axis persisted settings if available, otherwise use defaults.
            var pitchSettings = Services.SettingsService.LoadAxisSettings("Pitch");
            if (pitchSettings == null) pitchSettings = new AxisSettings { DriverId = 1 };

            var rollSettings = Services.SettingsService.LoadAxisSettings("Roll");
            if (rollSettings == null) rollSettings = new AxisSettings { DriverId = 2 };

            var rudderSettings = Services.SettingsService.LoadAxisSettings("Rudder");
            if (rudderSettings == null) rudderSettings = new AxisSettings { DriverId = 3 };

            var tillerSettings = Services.SettingsService.LoadAxisSettings("Tiller");
            if (tillerSettings == null) tillerSettings = new AxisSettings { DriverId = 4 };

            Pitch = new AxisViewModel(new Axis("Pitch", pitchSettings));
            Roll = new AxisViewModel(new Axis("Roll", rollSettings));
            Rudder = new AxisViewModel(new Axis("Rudder", rudderSettings));
            Tiller = new AxisViewModel(new Axis("Tiller", tillerSettings));

            // Register encoders with a centralized manager so polling logic lives in one place.
            try
            {
                _encoderManager = new Services.EncoderManager();
                if (!string.IsNullOrWhiteSpace(pitchSettings.RS485Ip)) _encoderManager.RegisterAxis("Pitch", Pitch, pitchSettings.RS485Ip);
                if (!string.IsNullOrWhiteSpace(rollSettings.RS485Ip)) _encoderManager.RegisterAxis("Roll", Roll, rollSettings.RS485Ip);
                if (!string.IsNullOrWhiteSpace(rudderSettings.RS485Ip)) _encoderManager.RegisterAxis("Rudder", Rudder, rudderSettings.RS485Ip);
                if (!string.IsNullOrWhiteSpace(tillerSettings.RS485Ip)) _encoderManager.RegisterAxis("Tiller", Tiller, tillerSettings.RS485Ip);
            }
            catch
            {
                // ignore if Modbus client cannot be created at startup
            }

            // Populate with some initial demo values so UI appears active.
            Pitch.EncoderPosition = 0;
            Roll.EncoderPosition = 0.1;
            Rudder.EncoderPosition = -0.05;
            Tiller.EncoderPosition = 0.02;

            SetupCommand = new RelayCommand(OnSetup);
            GlobalSettingsCommand = new RelayCommand(_ => OnGlobalSettings());
        }

        private void OnSetup(object? parameter)
        {
            var axisName = parameter?.ToString() ?? string.Empty;
            AxisViewModel? axisVm = axisName switch
            {
                "Pitch" => Pitch,
                "Roll" => Roll,
                "Rudder" => Rudder,
                "Tiller" => Tiller,
                _ => null
            };

            if (axisVm == null)
            {
                MessageBox.Show($"Unknown axis: {axisName}", "Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Open the axis setup window
            var vm = new AxisSetupViewModel(axisVm);
            var win = new Views.AxisSetupWindow(vm);
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        }

        private void OnGlobalSettings()
        {
            var vm = new GlobalSettingsViewModel();
            var win = new Views.GlobalSettingsWindow(vm);
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        }

        public void LogError(string message)
        {
            var timestamped = $"{DateTime.Now:HH:mm:ss} - {message}";
            ErrorLog.Insert(0, timestamped);
            // Keep only last 100 entries
            while (ErrorLog.Count > 100)
            {
                ErrorLog.RemoveAt(ErrorLog.Count - 1);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
