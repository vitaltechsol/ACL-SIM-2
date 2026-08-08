using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using ACL_SIM_2.Models;
using ACL_SIM_2.Services;

namespace ACL_SIM_2.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private Services.AxisModbusRegistry? _modbusRegistry;
        private readonly Dictionary<string, Services.AxisEncoder> _axisEncoders = new Dictionary<string, Services.AxisEncoder>();
        private readonly IAircraftManager? _proSimManager;
        private readonly IAppLogger _appLogger;
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

        private bool _hasCenteredControls = false;
        private bool _autoCenterOnStartup = false;

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

        private double _pitchTrim;
        public double PitchTrim
        {
            get => _pitchTrim;
            set
            {
                if (Math.Abs(_pitchTrim - value) > 0.001)
                {
                    _pitchTrim = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PitchTrim)));
                }
            }
        }

        private double _rollTrim;
        public double RollTrim
        {
            get => _rollTrim;
            set
            {
                if (Math.Abs(_rollTrim - value) > 0.001)
                {
                    _rollTrim = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RollTrim)));
                }
            }
        }

        private double _rudderTrim;
        public double RudderTrim
        {
            get => _rudderTrim;
            set
            {
                if (Math.Abs(_rudderTrim - value) > 0.001)
                {
                    _rudderTrim = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RudderTrim)));
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

        private ConnectionState _proSimConnectionState = ConnectionState.Disconnected;
        public ConnectionState ProSimConnectionState
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
        public ICommand CancelCenteringCommand { get; }

        private readonly Dictionary<string, CancellationTokenSource> _centeringCtsByAxis = new Dictionary<string, CancellationTokenSource>();

        private bool _simIsPaused = false;

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

        public MainViewModel(IAircraftManager aircraftManager, IAppLogger appLogger)
        {
            _proSimManager = aircraftManager ?? throw new ArgumentNullException(nameof(aircraftManager));
            _appLogger = appLogger ?? throw new ArgumentNullException(nameof(appLogger));

            // Configure logger to write to the error log
            if (_appLogger is AppLogger logger)
            {
                logger.SetLogAction(LogError);
            }

            // Load global settings for ProSim IP
            var globalSettings = Services.SettingsService.LoadGlobalSettings();
            if (globalSettings != null)
            {
                ProSimIp = globalSettings.ProsimIp ?? "127.0.0.1";
                _autoCenterOnStartup = globalSettings.AutoCenterOnStartup;
            }

            // Initialize ProSim Manager
            try
            {
                _proSimManager.OnConnectionStateChanged += ProSimManager_OnConnectionStateChanged;

                // Subscribe to ProSim DataRef changes
                _proSimManager.OnElevatorCptnChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => PitchProSimAxis = e.Value);
                _proSimManager.OnAileronCptnChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RollProSimAxis = e.Value);
                _proSimManager.OnRudderCaptChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RudderProSimAxis = e.Value);
                _proSimManager.OnTillerCaptChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => TillerProSimAxis = e.Value);
                _proSimManager.OnElevatorChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => PitchFlightControl = e.Value * 100);
                _proSimManager.OnAileronRightChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RollFlightControl = e.Value * 100);
                _proSimManager.OnTrimElevatorChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => PitchTrim = e.Value);
                _proSimManager.OnTrimAileronChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RollTrim = e.Value);
                _proSimManager.OnTrimRudderChanged += (s, e) => Application.Current?.Dispatcher.Invoke(() => RudderTrim = e.Value);
                _proSimManager.OnPauseChanged += (s, e) => _simIsPaused = e.Value != 0;

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
            CancelCenteringCommand = new RelayCommand(CancelCentering);

            // Subscribe to ErrorLog collection changes to update ErrorLogText
            ErrorLog.CollectionChanged += (s, e) => UpdateErrorLogText();

            // Defer all Modbus connections until after the UI has rendered
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(InitializeModbus));
        }

        // -----------------------------------------------------------------
        // Per-axis encoder helpers
        // -----------------------------------------------------------------

        private Services.AxisEncoder CreateEncoder(string axisName, AxisViewModel axisVm, EasyModbus.ModbusClient modbusClient, object modbusLock)
        {
            var encoder = new Services.AxisEncoder(modbusClient, axisName, () => axisVm.IsReversed, modbusLock, pollIntervalMs: 10);
            WireEncoderEvents(axisName, axisVm, encoder);
            _axisEncoders[axisName] = encoder;
            return encoder;
        }

        private void DisposeEncoder(string axisName)
        {
            if (_axisEncoders.TryGetValue(axisName, out var encoder))
            {
                try { encoder.Dispose(); } catch { }
                _axisEncoders.Remove(axisName);
            }
        }

        private void WireEncoderEvents(string axisName, AxisViewModel axisVm, Services.AxisEncoder encoder)
        {
            encoder.ConnectionChanged += (connected) =>
            {
                try
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        axisVm.ConnectionState = connected
                            ? AxisViewModel.EncoderConnectionState.Connected
                            : AxisViewModel.EncoderConnectionState.Failed;
                        if (connected)
                            LogError($"[{axisName}] RS485 TCP link established (waiting for Servo Driver response...)");
                    }));
                }
                catch { }
            };

            encoder.FirstReadSucceeded += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        LogError($"[{axisName}] RS485 Modbus ready - motor driver responding")));
                }
                catch { }
            };

            encoder.ErrorOccurred += (errorMsg) =>
            {
                try
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        LogError(errorMsg)));
                }
                catch { }
            };
        }

        private void InitializeModbus()
        {
            // Create the shared Modbus connection registry first
            _modbusRegistry = new Services.AxisModbusRegistry();
            foreach (var axisName in AxisNames)
            {
                var axisVm = _axes[axisName];
                var settings = _axisSettings[axisName];
                if (axisVm.Enabled && !string.IsNullOrWhiteSpace(settings.RS485Ip))
                    _modbusRegistry.Register(axisName, settings.RS485Ip, (byte)settings.DriverId);
            }

            // Create one AxisEncoder per axis, then one AxisManager wired to it
            try
            {
                foreach (var axisName in AxisNames)
                {
                    var axisVm = _axes[axisName];
                    var modbusClient = _modbusRegistry.GetModbusClient(axisName);
                    var modbusLock = _modbusRegistry.GetModbusLock(axisName);
                    if (!axisVm.Enabled || modbusClient == null || modbusLock == null) continue;

                    var encoder = CreateEncoder(axisName, axisVm, modbusClient, modbusLock);
                    _axisManagers[axisName] = new Services.AxisManager(axisName, axisVm, modbusClient, modbusLock, _proSimManager, _appLogger, encoder);
                }
            }
            catch
            {
                // ignore if axis setup fails at startup
            }

            // Send initial centering speed and dampening values to all enabled axes
            foreach (var axisName in AxisNames)
            {
                if (_axisManagers.TryGetValue(axisName, out var mgr) && _axisSettings.TryGetValue(axisName, out var s))
                {
                    mgr.SendCenteringSpeed(AxisSettings.ConvertCenteringSpeedToActual(s.SelfCenteringSpeed));
                    mgr.SendDampening(AxisSettings.ConvertDampeningToActual(s.Dampening));
                }
            }
        }

        private void ProSimManager_OnConnectionStateChanged(object? sender, Services.ConnectionStateEventArgs e)
        {
            // Update UI on main thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ProSimConnectionState = e.State;
                ProSimStatusMessage = e.Message;
                LogError($"[ProSim] {e.Message}");
                if (ProSimConnectionState == ConnectionState.Connected
                    && _autoCenterOnStartup
                    && !_hasCenteredControls
                    && CanCenterControls())
                {
                    CenterAllControls();
                }
            });
        }

        private async void ToggleProSimConnection()
        {
            if (_proSimManager == null) return;

            if (ProSimConnectionState == ConnectionState.Connected)
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
            var vm = new AxisSetupViewModel(axisVm, axisManager, () => GetProSimAxisValue(axisName), _proSimManager);

            // Subscribe to settings saved event to update encoder registration
            vm.OnSettingsSaved += (savedAxisName, rs485Ip, connectionSettingsChanged) =>
            {
                if (_modbusRegistry != null && axisVm.Enabled)
                {
                    try
                    {
                        if (axisVm.AutopilotOn)
                        {
                            LogError(connectionSettingsChanged
                                ? $"[{savedAxisName}] Settings saved while autopilot is active. Connection changes will apply after autopilot is off."
                                : $"[{savedAxisName}] Settings saved while autopilot is active. Skipped axis reset/re-center.");
                            return;
                        }

                        if (connectionSettingsChanged)
                        {
                            DisposeEncoder(savedAxisName);
                            _modbusRegistry.Unregister(savedAxisName);

                            if (_axisManagers.TryGetValue(savedAxisName, out var oldManager))
                            {
                                oldManager?.Dispose();
                                _axisManagers[savedAxisName] = null;
                            }

                            if (!string.IsNullOrWhiteSpace(rs485Ip))
                            {
                                var settings = _axisSettings[savedAxisName];
                                _modbusRegistry.Register(savedAxisName, rs485Ip, (byte)settings.DriverId);

                                var modbusClient = _modbusRegistry.GetModbusClient(savedAxisName);
                                var modbusLock = _modbusRegistry.GetModbusLock(savedAxisName);
                                if (modbusClient != null && modbusLock != null)
                                {
                                    var encoder = CreateEncoder(savedAxisName, axisVm, modbusClient, modbusLock);
                                    _axisManagers[savedAxisName] = new Services.AxisManager(savedAxisName, axisVm, modbusClient, modbusLock, _proSimManager, _appLogger, encoder);
                                }

                                LogError($"[{savedAxisName}] Connection settings changed - encoder and torque control reconnected");
                            }
                        }
                        else
                        {
                            LogError($"[{savedAxisName}] Settings saved");
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
            Debug.WriteLine(timestamped);
            // ObservableCollection must only be modified on the UI thread.
            // LogError can be called from background threads (e.g. Task.Run in UpdateTorqueAsync),
            // so marshal the collection mutation to the dispatcher.

            if (Application.Current?.Dispatcher is System.Windows.Threading.Dispatcher dispatcher
                && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() =>
                {
                    ErrorLog.Insert(0, timestamped);
                    while (ErrorLog.Count > 100)
                        ErrorLog.RemoveAt(ErrorLog.Count - 1);
                });
            }
            else
            {
                ErrorLog.Insert(0, timestamped);
                while (ErrorLog.Count > 100)
                    ErrorLog.RemoveAt(ErrorLog.Count - 1);
            }
        }

        private bool CanCenterControls()
        {
            return _proSimManager != null &&
                   ProSimConnectionState == ConnectionState.Connected &&
                   !IsCentering;
        }

        private void UpdateCenteringState()
        {
            IsCentering = _centeringCtsByAxis.Count > 0;
        }

        private async System.Threading.Tasks.Task CenterAxisAsync(string axisName, AxisViewModel axisVm, Services.AxisManager axisManager, CancellationToken cancellationToken)
        {
            try
            {
                LogError($"[Center] Starting {axisName}...");

                await axisManager.CenterToProSimPositionAsync(
                    getProSimValue: () => GetProSimAxisValue(axisName),
                    log: message => LogError($"[Center] {message}"),
                    cancellationToken: cancellationToken
                );
            }
            finally
            {
                if (_centeringCtsByAxis.TryGetValue(axisName, out var cts))
                {
                    cts.Dispose();
                    _centeringCtsByAxis.Remove(axisName);
                }

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    axisVm.Underlying.MotorIsMoving = false;
                    axisVm.IsCentering = false;
                    axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
                });

                LogError($"[Center] {axisName} - MotorIsMoving disabled (back to normal torque)");
                UpdateCenteringState();
            }
        }

        private async void CenterAllControls()
        {
            if (IsCentering || _proSimManager == null) return;

            LogError("[Center] Starting concurrent centering for all axes...");
            var simPaused = false;
            var wasAlreadyPaused = _simIsPaused;

            try
            {
                var centeringTasks = new List<System.Threading.Tasks.Task>();

                try
                {
                    _proSimManager.DisengageAP();
                    LogError("[Center] MCP AP disengage sent");
                }
                catch (System.Exception ex)
                {
                    LogError($"[Center] Failed to disengage MCP AP: {ex.Message}");
                }

                foreach (var axisName in AxisNames)
                {
                    if (!_axes.TryGetValue(axisName, out var axisVm) || !axisVm.Enabled)
                    {
                        // LogError($"[Center] Skipping {axisName} (not enabled)");
                        continue;
                    }

                    if (!_axisManagers.TryGetValue(axisName, out var axisManager) || axisManager == null)
                    {
                        // LogError($"[Center] Skipping {axisName} (no axis manager)");
                        continue;
                    }

                    if (_centeringCtsByAxis.ContainsKey(axisName))
                    {
                        continue;
                    }

                    var axisCts = new CancellationTokenSource();
                    _centeringCtsByAxis[axisName] = axisCts;

                    axisVm.Underlying.MotorIsMoving = true;
                    axisVm.IsCentering = true;
                    axisVm.NotifyPropertyChanged(nameof(AxisViewModel.MotorIsMoving));
                    //LogError($"[Center] {axisName} - MotorIsMoving enabled (using Movement Torque)");

                    // Pre-activate centering state so the hydraulics-off handler cannot set
                    // centering speed to 0 between now and when the async task begins executing.
                    axisManager.PrepareCentering();

                    if (!simPaused && !wasAlreadyPaused)
                    {
                        try
                        {
                            _proSimManager.PauseSim();
                            simPaused = true;
                            LogError("[Center] Simulator paused during centering");
                        }
                        catch (System.Exception ex)
                        {
                            LogError($"[Center] Failed to pause simulator: {ex.Message}");
                        }
                    }

                    centeringTasks.Add(CenterAxisAsync(axisName, axisVm, axisManager, axisCts.Token));
                }

                UpdateCenteringState();

                await System.Threading.Tasks.Task.WhenAll(centeringTasks);
                _hasCenteredControls = true;

                LogError("[Center] All axis centering tasks complete!");
            }
            catch (System.Exception ex)
            {
                LogError($"[Center] Error: {ex.Message}");
            }
            finally
            {
                foreach (var kvp in _centeringCtsByAxis)
                {
                    kvp.Value.Dispose();
                }

                _centeringCtsByAxis.Clear();
                UpdateCenteringState();

                if (simPaused && !wasAlreadyPaused)
                {
                    try
                    {
                        _proSimManager.UnpauseSim();
                        LogError("[Center] Simulator unpaused");
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"[Center] Failed to unpause simulator: {ex.Message}");
                    }
                }
            }
        }

        private void CancelCentering(object? parameter)
        {
            var axisName = parameter?.ToString();
            if (string.IsNullOrWhiteSpace(axisName))
            {
                return;
            }

            if (_axes.TryGetValue(axisName, out var axisVm))
            {
                axisVm.IsCentering = false;
            }

            if (_centeringCtsByAxis.TryGetValue(axisName, out var cts) && !cts.IsCancellationRequested)
            {
                LogError($"[Center] Cancelling centering for {axisName}...");
                cts.Cancel();
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
            if (e.PropertyName != nameof(AxisViewModel.Enabled) || _modbusRegistry == null) return;

            if (vm.Enabled)
            {
                if (string.IsNullOrWhiteSpace(rs485Ip))
                {
                    LogError($"[{axisName}] enabled");
                    LogError($"[{axisName}] RS485 IP is missing");
                    return;
                }
                try
                {
                    var settings = _axisSettings[axisName];
                    _modbusRegistry.Register(axisName, rs485Ip, (byte)settings.DriverId);

                    var modbusClient = _modbusRegistry.GetModbusClient(axisName);
                    var modbusLock = _modbusRegistry.GetModbusLock(axisName);
                    if (modbusClient != null && modbusLock != null)
                    {
                        var encoder = CreateEncoder(axisName, vm, modbusClient, modbusLock);
                        _axisManagers[axisName] = new Services.AxisManager(axisName, vm, modbusClient, modbusLock, _proSimManager, _appLogger, encoder);
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
                try
                {
                    DisposeEncoder(axisName);
                    _modbusRegistry.Unregister(axisName);

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

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
