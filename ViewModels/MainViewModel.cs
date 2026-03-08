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
        private Services.AxisManager? _pitchManager;
        private Services.AxisManager? _rollManager;
        private Services.AxisManager? _rudderManager;
        private Services.AxisManager? _tillerManager;

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

            // Subscribe to Enabled property changes to dynamically register/unregister encoders
            Pitch.PropertyChanged += (s, e) => HandleAxisEnabledChanged("Pitch", Pitch, pitchSettings.RS485Ip, e);
            Roll.PropertyChanged += (s, e) => HandleAxisEnabledChanged("Roll", Roll, rollSettings.RS485Ip, e);
            Rudder.PropertyChanged += (s, e) => HandleAxisEnabledChanged("Rudder", Rudder, rudderSettings.RS485Ip, e);
            Tiller.PropertyChanged += (s, e) => HandleAxisEnabledChanged("Tiller", Tiller, tillerSettings.RS485Ip, e);

            // Register encoders with a centralized manager so polling logic lives in one place.
            // Only register axes that are enabled.
            try
            {
                _encoderManager = new Services.EncoderManager();
                if (Pitch.Enabled && !string.IsNullOrWhiteSpace(pitchSettings.RS485Ip)) 
                    _encoderManager.RegisterAxis("Pitch", Pitch, pitchSettings.RS485Ip);
                if (Roll.Enabled && !string.IsNullOrWhiteSpace(rollSettings.RS485Ip)) 
                    _encoderManager.RegisterAxis("Roll", Roll, rollSettings.RS485Ip);
                if (Rudder.Enabled && !string.IsNullOrWhiteSpace(rudderSettings.RS485Ip)) 
                    _encoderManager.RegisterAxis("Rudder", Rudder, rudderSettings.RS485Ip);
                if (Tiller.Enabled && !string.IsNullOrWhiteSpace(tillerSettings.RS485Ip)) 
                    _encoderManager.RegisterAxis("Tiller", Tiller, tillerSettings.RS485Ip);
            }
            catch
            {
                // ignore if Modbus client cannot be created at startup
            }

            // Initialize AxisManagers for torque control
            try
            {
                if (Pitch.Enabled && _encoderManager != null)
                {
                    var modbusClient = _encoderManager.GetModbusClient("Pitch");
                    if (modbusClient != null)
                        _pitchManager = new Services.AxisManager("Pitch", Pitch, modbusClient);
                }

                if (Roll.Enabled && _encoderManager != null)
                {
                    var modbusClient = _encoderManager.GetModbusClient("Roll");
                    if (modbusClient != null)
                        _rollManager = new Services.AxisManager("Roll", Roll, modbusClient);
                }

                if (Rudder.Enabled && _encoderManager != null)
                {
                    var modbusClient = _encoderManager.GetModbusClient("Rudder");
                    if (modbusClient != null)
                        _rudderManager = new Services.AxisManager("Rudder", Rudder, modbusClient);
                }

                if (Tiller.Enabled && _encoderManager != null)
                {
                    var modbusClient = _encoderManager.GetModbusClient("Tiller");
                    if (modbusClient != null)
                        _tillerManager = new Services.AxisManager("Tiller", Tiller, modbusClient);
                }
            }
            catch
            {
                // ignore if AxisManager creation fails
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

            // Subscribe to settings saved event to update encoder registration
            vm.OnSettingsSaved += (savedAxisName, rs485Ip) =>
            {
                if (_encoderManager != null && axisVm.Enabled)
                {
                    try
                    {
                        // Unregister old encoder
                        _encoderManager.UnregisterAxis(savedAxisName);

                        // Dispose old AxisManager
                        switch (savedAxisName)
                        {
                            case "Pitch": _pitchManager?.Dispose(); break;
                            case "Roll": _rollManager?.Dispose(); break;
                            case "Rudder": _rudderManager?.Dispose(); break;
                            case "Tiller": _tillerManager?.Dispose(); break;
                        }

                        // Re-register with new settings if IP is configured
                        if (!string.IsNullOrWhiteSpace(rs485Ip))
                        {
                            _encoderManager.RegisterAxis(savedAxisName, axisVm, rs485Ip);

                            // Recreate AxisManager with new ModbusClient
                            var modbusClient = _encoderManager.GetModbusClient(savedAxisName);
                            if (modbusClient != null)
                            {
                                var newManager = new Services.AxisManager(savedAxisName, axisVm, modbusClient);
                                switch (savedAxisName)
                                {
                                    case "Pitch": _pitchManager = newManager; break;
                                    case "Roll": _rollManager = newManager; break;
                                    case "Rudder": _rudderManager = newManager; break;
                                    case "Tiller": _tillerManager = newManager; break;
                                }
                            }

                            LogError($"[{savedAxisName}] Encoder and torque control re-registered with new settings");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"[{savedAxisName}] Failed to update encoder: {ex.Message}");
                    }
                }
            };

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
            var timestamped = $"{System.DateTime.Now:HH:mm:ss} - {message}";
            ErrorLog.Insert(0, timestamped);
            // Keep only last 100 entries
            while (ErrorLog.Count > 100)
            {
                ErrorLog.RemoveAt(ErrorLog.Count - 1);
            }
        }

        private void HandleAxisEnabledChanged(string axisName, AxisViewModel vm, string rs485Ip, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AxisViewModel.Enabled) && _encoderManager != null)
            {
                if (vm.Enabled && !string.IsNullOrWhiteSpace(rs485Ip))
                {
                    // Re-register encoder when enabled
                    try
                    {
                        _encoderManager.RegisterAxis(axisName, vm, rs485Ip);

                        // Create AxisManager for torque control
                        var modbusClient = _encoderManager.GetModbusClient(axisName);
                        if (modbusClient != null)
                        {
                            var newManager = new Services.AxisManager(axisName, vm, modbusClient);
                            switch (axisName)
                            {
                                case "Pitch": _pitchManager = newManager; break;
                                case "Roll": _rollManager = newManager; break;
                                case "Rudder": _rudderManager = newManager; break;
                                case "Tiller": _tillerManager = newManager; break;
                            }
                        }

                        LogError($"[{axisName}] Encoder and torque control enabled");
                    }
                    catch (Exception ex)
                    {
                        LogError($"[{axisName}] Failed to enable encoder: {ex.Message}");
                    }
                }
                else
                {
                    // Unregister encoder and dispose AxisManager when disabled
                    try
                    {
                        _encoderManager.UnregisterAxis(axisName);

                        // Dispose AxisManager
                        switch (axisName)
                        {
                            case "Pitch": _pitchManager?.Dispose(); _pitchManager = null; break;
                            case "Roll": _rollManager?.Dispose(); _rollManager = null; break;
                            case "Rudder": _rudderManager?.Dispose(); _rudderManager = null; break;
                            case "Tiller": _tillerManager?.Dispose(); _tillerManager = null; break;
                        }

                        LogError($"[{axisName}] Encoder and torque control disabled");
                    }
                    catch (Exception ex)
                    {
                        LogError($"[{axisName}] Failed to disable encoder: {ex.Message}");
                    }
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
