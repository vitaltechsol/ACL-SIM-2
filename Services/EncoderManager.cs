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
            public int Address { get; set; }
            public int PollMs { get; set; }
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public EncoderManager()
        {
            // AxisEncoder instances manage their own loops; subscribe to app exit for cleanup.
            try { if (Application.Current != null) Application.Current.Exit += OnAppExit; } catch { }
        }

        public void RegisterAxis(string name, AxisViewModel vm, string rs485Ip, int encoderAddress = 387, int pollMs = 10)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (string.IsNullOrWhiteSpace(rs485Ip)) return;

            try
            {
                var mb = new ModbusClient(rs485Ip, 502);
                mb.UnitIdentifier = (byte)vm.Underlying.Settings.DriverId;
                var encoder = new AxisEncoder(mb, name, encoderAddress, Math.Max(10, pollMs));
                var entry = new Entry { Name = name, Vm = vm, Encoder = encoder, Address = encoderAddress, PollMs = Math.Max(10, pollMs) };

                // subscribe to encoder events and marshal updates to UI
                encoder.ValueUpdated += (val) =>
                {
                    try
                    {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { entry.Vm.EncoderPosition = val; } catch { }
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
                }
                _entries.Clear();
            }

            GC.SuppressFinalize(this);
        }
    }
}

