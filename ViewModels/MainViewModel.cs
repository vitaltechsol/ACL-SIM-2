using System.Collections.Generic;
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
        private readonly Dictionary<string, Services.AxisManager?> _axisManagers = new Dictionary<string, Services.AxisManager?>();
        private readonly Dictionary<string, AxisViewModel> _axes = new Dictionary<string, AxisViewModel>();
        private readonly Dictionary<string, AxisSettings> _axisSettings = new Dictionary<string, AxisSettings>();
        private static readonly string[] AxisNames = { "Pitch", "Roll", "Rudder", "Tiller" };
        private static readonly Dictionary<string, int> DefaultDriverIds = new Dictionary<string, int>
        {
            { "Pitch", 1 },
            { "Roll", 2 },
            { "Rudder", 3 },
            { "Tiller", 4 }
        };

        // Public properties for XAML binding
        public AxisViewModel Pitch => _axes["Pitch"];
        public AxisViewModel Roll => _axes["Roll"];
        public AxisViewModel Rudder => _axes["Rudder"];
        public AxisViewModel Tiller => _axes["Tiller"];

        public ObservableCollection<string> ErrorLog { get; } = new ObservableCollection<string>();

        private string _errorLogText = string.Empty;
        public string ErrorLogText
        {
            get => _errorLogText;
            set
            {
                if (_errorLogText != value)
                {
                    _errorLogText = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorLogText)));
                }
            }
        }

        public ICommand SetupCommand { get; }
        public ICommand GlobalSettingsCommand { get; }
        public ICommand ClearErrorLogCommand { get; }

        public MainViewModel()
        {
            // Load settings and create ViewModels for all axes
            foreach (var axisName in AxisNames)
            {
                var settings = Services.SettingsService.LoadAxisSettings(axisName) 
                    ?? new AxisSettings { DriverId = DefaultDriverIds[axisName] };

                _axisSettings[axisName] = settings;
                var axisVm = new AxisViewModel(new Axis(axisName, settings));
                _axes[axisName] = axisVm;

                // Subscribe to Enabled property changes
                axisVm.PropertyChanged += (s, e) => HandleAxisEnabledChanged(axisName, axisVm, settings.RS485Ip, e);
            }

            // Register encoders with centralized manager
            try
            {
                _encoderManager = new Services.EncoderManager();
                foreach (var axisName in AxisNames)
                {
                    var axisVm = _axes[axisName];
                    var settings = _axisSettings[axisName];
                    if (axisVm.Enabled && !string.IsNullOrWhiteSpace(settings.RS485Ip))
                    {
                        _encoderManager.RegisterAxis(axisName, axisVm, settings.RS485Ip);
                    }
                }
            }
            catch
            {
                // ignore if Modbus client cannot be created at startup
            }

            // Initialize AxisManagers for torque control
            try
            {
                if (_encoderManager != null)
                {
                    foreach (var axisName in AxisNames)
                    {
                        var axisVm = _axes[axisName];
                        if (axisVm.Enabled)
                        {
                            var modbusClient = _encoderManager.GetModbusClient(axisName);
                            var modbusLock = _encoderManager.GetModbusLock(axisName);
                            if (modbusClient != null && modbusLock != null)
                            {
                                _axisManagers[axisName] = new Services.AxisManager(axisName, axisVm, modbusClient, modbusLock);
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore if AxisManager creation fails
            }

            // Populate with some initial demo values so UI appears active
            Pitch.EncoderPosition = 0;
            Roll.EncoderPosition = 0.1;
            Rudder.EncoderPosition = -0.05;
            Tiller.EncoderPosition = 0.02;

            SetupCommand = new RelayCommand(OnSetup);
            GlobalSettingsCommand = new RelayCommand(_ => OnGlobalSettings());
            ClearErrorLogCommand = new RelayCommand(_ => ClearErrorLog());

            // Subscribe to ErrorLog collection changes to update ErrorLogText
            ErrorLog.CollectionChanged += (s, e) => UpdateErrorLogText();
        }

        private void UpdateErrorLogText()
        {
            ErrorLogText = string.Join(System.Environment.NewLine, ErrorLog);
        }

        private void ClearErrorLog()
        {
            ErrorLog.Clear();
            ErrorLogText = string.Empty;
        }

        private void OnSetup(object? parameter)
        {
            var axisName = parameter?.ToString() ?? string.Empty;

            if (!_axes.TryGetValue(axisName, out var axisVm))
            {
                MessageBox.Show($"Unknown axis: {axisName}", "Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get the AxisManager for this axis (if available)
            _axisManagers.TryGetValue(axisName, out var axisManager);

            // Open the axis setup window with AxisManager for position testing
            var vm = new AxisSetupViewModel(axisVm, axisManager);

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
                        if (_axisManagers.TryGetValue(savedAxisName, out var oldManager))
                        {
                            oldManager?.Dispose();
                            _axisManagers[savedAxisName] = null;
                        }

                        // Re-register with new settings if IP is configured
                        if (!string.IsNullOrWhiteSpace(rs485Ip))
                        {
                            _encoderManager.RegisterAxis(savedAxisName, axisVm, rs485Ip);

                            // Recreate AxisManager with new ModbusClient
                            var modbusClient = _encoderManager.GetModbusClient(savedAxisName);
                            if (modbusClient != null)
                            {
                                _axisManagers[savedAxisName] = new Services.AxisManager(savedAxisName, axisVm, modbusClient);
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
                            _axisManagers[axisName] = new Services.AxisManager(axisName, vm, modbusClient);
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
                        if (_axisManagers.TryGetValue(axisName, out var manager))
                        {
                            manager?.Dispose();
                            _axisManagers[axisName] = null;
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
