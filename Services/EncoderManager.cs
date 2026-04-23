using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EasyModbus;
using ACL_SIM_2.ViewModels;
using System.Diagnostics;

namespace ACL_SIM_2.Services
{
    public class EncoderManager : IDisposable
    {
        private class Entry
        {
            public string Name { get; set; } = string.Empty;
            public AxisViewModel Vm { get; set; } = null!;
            public AxisEncoder Encoder { get; set; } = null!;
            public ModbusClient ModbusClient { get; set; } = null!; // Shared ModbusClient for this axis
            public object ModbusLock { get; set; } = new object(); // Shared lock for thread-safe Modbus access
            public int Address { get; set; }
            public int PollMs { get; set; }
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>
        /// Raised on the UI dispatcher thread whenever a new encoder value is read.
        /// Arguments are (axisName, encoderValue). <see cref="AxisManager"/> subscribes
        /// to this event to receive encoder data without any coupling to EncoderManager internals.
        /// </summary>
        public event Action<string, double>? EncoderValueUpdated;

        public Action<string>? OnError { get; set; }

        public EncoderManager()
        {
            // AxisEncoder instances manage their own loops; subscribe to app exit for cleanup.
            try { if (Application.Current != null) Application.Current.Exit += OnAppExit; } catch { }
        }

        public void RegisterAxis(string name, AxisViewModel vm, string rs485Ip, int pollMs = 10)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (string.IsNullOrWhiteSpace(rs485Ip)) return;

            try
            {
                // Create ONE shared ModbusClient per axis
                var mb = new ModbusClient(rs485Ip, 502);
                mb.UnitIdentifier = (byte)vm.Underlying.Settings.DriverId;

                // Create entry with shared ModbusLock
                var entry = new Entry { Name = name, Vm = vm, ModbusClient = mb, PollMs = Math.Max(10, pollMs) };

                // Pass shared client AND shared lock to AxisEncoder
                var encoder = new AxisEncoder(mb, name, () => vm.IsReversed, entry.ModbusLock, Math.Max(10, pollMs));
                entry.Encoder = encoder;

                // subscribe to encoder events and marshal updates to UI
                encoder.ValueUpdated += (val) =>
                {
                    try
                    {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { EncoderValueUpdated?.Invoke(name, val); } catch { }
                        }));
                    }
                    catch { }
                };

                encoder.ConnectionChanged += (connected) =>
                {
                    try
                    {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { entry.Vm.ConnectionState = connected ? AxisViewModel.EncoderConnectionState.Connected : AxisViewModel.EncoderConnectionState.Failed; } catch { }
                        }));
                    }
                    catch { }
                };

                encoder.ErrorOccurred += (errorMsg) =>
                {
                    try
                    {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try 
                            { 
                                if (Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel mainVm)
                                {
                                    mainVm.LogError(errorMsg);
                                }
                            } 
                            catch { }
                        }));
                    }
                    catch { }
                };

                lock (_entries) { _entries.Add(entry); }
            }
            catch
            {
                // ignore
            }
        }

        public void UnregisterAxis(string name)
        {
            Entry? toRemove = null;
            lock (_entries)
            {
                toRemove = _entries.FirstOrDefault(x => x.Name == name);
                if (toRemove != null) _entries.Remove(toRemove);
            }

            if (toRemove != null)
            {
                try { toRemove.Encoder.Dispose(); } catch { }
                try { if (toRemove.ModbusClient?.Connected == true) toRemove.ModbusClient?.Disconnect(); } catch { }
            }
        }

        /// <summary>
        /// Gets the shared ModbusClient for an axis. Returns null if axis is not registered.
        /// This allows other services (like AxisTorqueControl) to share the same connection.
        /// </summary>
        public ModbusClient? GetModbusClient(string name)
        {
            lock (_entries)
            {
                return _entries.FirstOrDefault(x => x.Name == name)?.ModbusClient;
            }
        }

        /// <summary>
        /// Gets the shared ModbusLock for an axis. Returns null if axis is not registered.
        /// This allows other services to synchronize their Modbus operations.
        /// </summary>
        public object? GetModbusLock(string name)
        {
            lock (_entries)
            {
                return _entries.FirstOrDefault(x => x.Name == name)?.ModbusLock;
            }
        }

        /// <summary>
        /// Gets the <see cref="AxisEncoder"/> for an axis. Returns null if axis is not registered.
        /// </summary>
        public AxisEncoder? GetEncoder(string name)
        {
            lock (_entries)
            {
                return _entries.FirstOrDefault(x => x.Name == name)?.Encoder;
            }
        }

        // Per-axis polling is handled by AxisEncoder instances.

        private void OnAppExit(object? sender, ExitEventArgs e) => Dispose();

        public void Dispose()
        {
            try { if (Application.Current != null) Application.Current.Exit -= OnAppExit; } catch { }

            lock (_entries)
            {
                foreach (var e in _entries)
                {
                    try { e.Encoder.Dispose(); } catch { }
                    try { if (e.ModbusClient?.Connected == true) e.ModbusClient?.Disconnect(); } catch { }
                }
                _entries.Clear();
            }

            GC.SuppressFinalize(this);
        }
    }
}

