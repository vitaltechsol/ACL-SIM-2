using System;
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
        private readonly Services.ProSimManager? _proSimManager;
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

        // ProSim properties
        private string _proSimIp = "127.0.0.1";
        public string ProSimIp
        {
            get => _proSimIp;
            set
            {
                if (_proSimIp != value)
                {
                    _proSimIp = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProSimIp)));
                }
            }
        }

        private Services.ProSimManager.ConnectionState _proSimConnectionState = Services.ProSimManager.ConnectionState.Disconnected;
        public Services.ProSimManager.ConnectionState ProSimConnectionState
        {
            get => _proSimConnectionState;
            set
            {
                if (_proSimConnectionState != value)
                {
                    _proSimConnectionState = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProSimConnectionState)));
                }
            }
        }

        private string _proSimStatusMessage = "Not connected";
        public string ProSimStatusMessage
        {
            get => _proSimStatusMessage;
            set
            {
                if (_proSimStatusMessage != value)
                {
                    _proSimStatusMessage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProSimStatusMessage)));
                }
            }
        }

        // ProSim Axis Values
        private double _pitchProSimAxis;
        public double PitchProSimAxis
        {
            get => _pitchProSimAxis;
            set
            {
                if (Math.Abs(_pitchProSimAxis - value) > 0.001)
                {
                    _pitchProSimAxis = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PitchProSimAxis)));
                }
            }
        }

        private double _rollProSimAxis;
        public double RollProSimAxis
        {
            get => _rollProSimAxis;
            set
            {
                if (Math.Abs(_rollProSimAxis - value) > 0.001)
                {
                    _rollProSimAxis = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RollProSimAxis)));
                }
            }
        }

        private double _rudderProSimAxis;
        public double RudderProSimAxis
        {
            get => _rudderProSimAxis;
            set
            {
                if (Math.Abs(_rudderProSimAxis - value) > 0.001)
                {
                    _rudderProSimAxis = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RudderProSimAxis)));
                }
            }
        }

        private double _tillerProSimAxis;
        public double TillerProSimAxis
        {
            get => _tillerProSimAxis;
            set
            {
                if (Math.Abs(_tillerProSimAxis - value) > 0.001)
                {
                    _tillerProSimAxis = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TillerProSimAxis)));
                }
            }
        }

        // ProSim Flight Control Values (Pitch and Roll only)
        private double _pitchFlightControl;
        public double PitchFlightControl
        {
            get => _pitchFlightControl;
            set
            {
                if (Math.Abs(_pitchFlightControl - value) > 0.001)
                {
                    _pitchFlightControl = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PitchFlightControl)));
                }
            }
        }

        private double _rollFlightControl;
        public double RollFlightControl
        {
            get => _rollFlightControl;
            set
            {
                if (Math.Abs(_rollFlightControl - value) > 0.001)
                {
                    _rollFlightControl = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RollFlightControl)));
                }
            }
        }

        public ICommand SetupCommand { get; }
        public ICommand GlobalSettingsCommand { get; }
        public ICommand ClearErrorLogCommand { get; }
        public ICommand ConnectProSimCommand { get; }
        public ICommand CenterControlsCommand { get; }

        private bool _isCentering;
        public bool IsCentering
        {
            get => _isCentering;
            set
            {
                if (_isCentering != value)
                {
                    _isCentering = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCentering)));
                }
            }
        }

        public MainViewModel()
        {
            // Load global settings for ProSim IP
            var globalSettings = Services.SettingsService.LoadGlobalSettings();
            if (globalSettings != null)
            {
                ProSimIp = globalSettings.ProsimIp ?? "127.0.0.1";
            }

            // Initialize ProSim Manager
            try
            {
                _proSimManager = new Services.ProSimManager();
                _proSimManager.OnConnectionStateChanged += ProSimManager_OnConnectionStateChanged;

                // Subscribe to ProSim DataRef changes
                _proSimManager.OnElevatorCptnChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => PitchProSimAxis = e.Value);
                _proSimManager.OnAileronCptnChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RollProSimAxis = e.Value);
                _proSimManager.OnRudderCaptChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RudderProSimAxis = e.Value);
                _proSimManager.OnTillerCaptChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => TillerProSimAxis = e.Value);
                _proSimManager.OnElevatorChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => PitchFlightControl = e.Value);
                _proSimManager.OnAileronLeftChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RollFlightControl = e.Value);

                // Auto-connect if enabled in settings
                if (globalSettings?.AutoConnectProsim == true)
                {
                    _ = ConnectProSimAsync();
                }
            }
            catch (System.Exception ex)
            {
                LogError($"Failed to initialize ProSim manager: {ex.Message}");
            }

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
                                _axisManagers[axisName] = new Services.AxisManager(axisName, axisVm, modbusClient, modbusLock, _proSimManager);
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore if AxisManager creation fails
            }

            // Populate with some initial value
            Pitch.EncoderPosition = 0;
            Roll.EncoderPosition = 0;
            Rudder.EncoderPosition = 0;
            Tiller.EncoderPosition = 0;

            SetupCommand = new RelayCommand(OnSetup);
            GlobalSettingsCommand = new RelayCommand(_ => OnGlobalSettings());
            ClearErrorLogCommand = new RelayCommand(_ => ClearErrorLog());
            ConnectProSimCommand = new RelayCommand(_ => ToggleProSimConnection());
            CenterControlsCommand = new RelayCommand(_ => CenterAllControls(), _ => CanCenterControls());

            // Subscribe to ErrorLog collection changes to update ErrorLogText
            ErrorLog.CollectionChanged += (s, e) => UpdateErrorLogText();
        }

        private void ProSimManager_OnConnectionStateChanged(object? sender, Services.ConnectionStateEventArgs e)
        {
            // Update UI on main thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ProSimConnectionState = e.State;
                ProSimStatusMessage = e.Message;
                LogError($"[ProSim] {e.Message}");
            });
        }

        private async void ToggleProSimConnection()
        {
            if (_proSimManager == null) return;

            if (ProSimConnectionState == Services.ProSimManager.ConnectionState.Connected)
            {
                _proSimManager.Disconnect();
            }
            else
            {
                await ConnectProSimAsync();
            }
        }

        private async System.Threading.Tasks.Task ConnectProSimAsync()
        {
            if (_proSimManager == null) return;

            try
            {
                await _proSimManager.ConnectAsync(ProSimIp);
            }
            catch (System.Exception ex)
            {
                LogError($"[ProSim] Connection error: {ex.Message}");
            }
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
            vm.OnSettingsSaved += (savedAxisName, rs485Ip, connectionSettingsChanged) =>
            {
                if (_encoderManager != null && axisVm.Enabled)
                {
                    try
                    {
                        // Only do full reconnection if connection settings (RS485 IP or Driver ID) changed
                        if (connectionSettingsChanged)
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

                                // Recreate AxisManager with new ModbusClient and shared lock
                                var modbusClient = _encoderManager.GetModbusClient(savedAxisName);
                                var modbusLock = _encoderManager.GetModbusLock(savedAxisName);
                                if (modbusClient != null && modbusLock != null)
                                {
                                    _axisManagers[savedAxisName] = new Services.AxisManager(savedAxisName, axisVm, modbusClient, modbusLock);
                                }

                                LogError($"[{savedAxisName}] Connection settings changed - encoder and torque control reconnected");
                            }
                        }
                        else
                        {
                            // Connection settings didn't change - just recreate AxisManager with existing ModbusClient
                            // This allows settings like RPM, torque, etc. to be updated without reconnecting
                            if (_axisManagers.TryGetValue(savedAxisName, out var oldManager))
                            {
                                oldManager?.Dispose();
                                _axisManagers[savedAxisName] = null;
                            }

                            var modbusClient = _encoderManager.GetModbusClient(savedAxisName);
                            var modbusLock = _encoderManager.GetModbusLock(savedAxisName);
                            if (modbusClient != null && modbusLock != null)
                            {
                                _axisManagers[savedAxisName] = new Services.AxisManager(savedAxisName, axisVm, modbusClient, modbusLock);
                                LogError($"[{savedAxisName}] Settings updated (no reconnection needed)");
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"[{savedAxisName}] Failed to update settings: {ex.Message}");
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

            // Reload ProSim IP after settings window closes
            var globalSettings = Services.SettingsService.LoadGlobalSettings();
            if (globalSettings != null)
            {
                var newIp = globalSettings.ProsimIp ?? "127.0.0.1";
                if (ProSimIp != newIp)
                {
                    ProSimIp = newIp;
                    LogError($"[ProSim] IP updated to {newIp}");
                }
            }
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

        private bool CanCenterControls()
        {
            return _proSimManager != null && 
                   ProSimConnectionState == Services.ProSimManager.ConnectionState.Connected &&
                   !IsCentering;
        }

        private async void CenterAllControls()
        {
            if (IsCentering || _proSimManager == null) return;

            IsCentering = true;
            LogError("[Center] Starting concurrent centering for all axes...");

            // Track which axes were enabled and had AutopilotOn set
            var axesToCenter = new List<(string name, AxisViewModel vm, Services.AxisManager manager)>();

            try
            {
                // First pass: Collect enabled axes and set AutopilotOn
                foreach (var axisName in AxisNames)
                {
                    if (!_axes.TryGetValue(axisName, out var axisVm) || !axisVm.Enabled)
                    {
                        LogError($"[Center] Skipping {axisName} (not enabled)");
                        continue;
                    }

                    if (!_axisManagers.TryGetValue(axisName, out var axisManager) || axisManager == null)
                    {
                        LogError($"[Center] Skipping {axisName} (no axis manager)");
                        continue;
                    }

                    axesToCenter.Add((axisName, axisVm, axisManager));

                    // Enable AutopilotOn to use Movement Torque during centering
                    axisVm.Underlying.AutopilotOn = true;
                    LogError($"[Center] {axisName} - AutopilotOn enabled (using Movement Torque)");
                }

                // Second pass: Start centering all axes concurrently (don't await individually)
                var centeringTasks = new List<System.Threading.Tasks.Task>();

                foreach (var (axisName, axisVm, axisManager) in axesToCenter)
                {
                    LogError($"[Center] Starting {axisName}...");

                    // Start centering task without awaiting (runs concurrently)
                    var task = axisManager.CenterToProSimPositionAsync(
                        getProSimValue: () => GetProSimAxisValue(axisName),
                        log: message => LogError($"[Center] {message}")
                    );

                    centeringTasks.Add(task);
                }

                // Wait for all axes to finish centering concurrently
                await System.Threading.Tasks.Task.WhenAll(centeringTasks);

                LogError("[Center] All axes centering complete!");
            }
            catch (System.Exception ex)
            {
                LogError($"[Center] Error: {ex.Message}");
            }
            finally
            {
                // Third pass: Disable AutopilotOn for all axes that were centered
                foreach (var (axisName, axisVm, _) in axesToCenter)
                {
                    axisVm.Underlying.AutopilotOn = false;
                    LogError($"[Center] {axisName} - AutopilotOn disabled (back to normal torque)");
                }

                IsCentering = false;
            }
        }

        private double GetProSimAxisValue(string axisName)
        {
            return axisName switch
            {
                "Pitch" => PitchProSimAxis,
                "Roll" => RollProSimAxis,
                "Rudder" => RudderProSimAxis,
                "Tiller" => TillerProSimAxis,
                _ => 512.0 // Default to center if unknown
            };
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
                        var modbusLock = _encoderManager.GetModbusLock(axisName);
                        if (modbusClient != null && modbusLock != null)
                        {
                            _axisManagers[axisName] = new Services.AxisManager(axisName, vm, modbusClient, modbusLock, _proSimManager);
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
