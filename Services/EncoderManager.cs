using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EasyModbus;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2.Services
{
    public class EncoderManager : IDisposable
    {
        private class Entry
        {
            public string Name { get; set; } = string.Empty;
            public AxisViewModel Vm { get; set; } = null!;
            public ModbusClient Mbc { get; set; } = null!;
            public int Address { get; set; }
            public int PollMs { get; set; }
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _loopTask;

        public EncoderManager()
        {
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            try { if (Application.Current != null) Application.Current.Exit += OnAppExit; } catch { }
        }

        public void RegisterAxis(string name, AxisViewModel vm, string rs485Ip, int encoderAddress = 387, int pollMs = 200)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (string.IsNullOrWhiteSpace(rs485Ip)) return;

            try
            {
                var mb = new ModbusClient(rs485Ip, 502);
                var entry = new Entry { Name = name, Vm = vm, Mbc = mb, Address = encoderAddress, PollMs = Math.Max(10, pollMs) };
                lock (_entries) { _entries.Add(entry); }
            }
            catch
            {
                // ignore
            }
        }

        public void UnregisterAxis(string name)
        {
            lock (_entries)
            {
                var e = _entries.FirstOrDefault(x => x.Name == name);
                if (e != null)
                {
                    try { e.Mbc.Disconnect(); } catch { }
                    _entries.Remove(e);
                }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Entry[] entries;
                lock (_entries) { entries = _entries.ToArray(); }

                foreach (var e in entries)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (!e.Mbc.Connected)
                        {
                            try { e.Mbc.Connect(); } catch { }
                        }

                        if (e.Mbc.Connected)
                        {
                            try
                            {
                                var regs = e.Mbc.ReadHoldingRegisters(e.Address, 1);
                                if (regs != null && regs.Length > 0)
                                {
                                    var val = regs[0];
                                    // marshal to UI
                                    try
                                    {
                                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            try { e.Vm.EncoderPosition = val; } catch { }
                                        }));
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try { await Task.Delay(e.PollMs, ct).ConfigureAwait(false); } catch { break; }
                }

                // small delay between full cycles
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
            }
        }

        private void OnAppExit(object? sender, ExitEventArgs e) => Dispose();

        public void Dispose()
        {
            try { if (Application.Current != null) Application.Current.Exit -= OnAppExit; } catch { }
            try { _cts.Cancel(); } catch { }
            try { _loopTask?.Wait(500); } catch { }

            lock (_entries)
            {
                foreach (var e in _entries)
                {
                    try { e.Mbc.Disconnect(); } catch { }
                }
                _entries.Clear();
            }

            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
