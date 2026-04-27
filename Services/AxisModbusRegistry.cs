using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EasyModbus;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Owns and manages one shared <see cref="ModbusClient"/> and thread-safety lock per axis.
    /// Other services (<see cref="EncoderManager"/>, <see cref="AxisManager"/>,
    /// <see cref="AxisTorqueControl"/>) retrieve the shared client and lock from here rather
    /// than creating their own connections.
    /// </summary>
    public class AxisModbusRegistry : IDisposable
    {
        private class Entry
        {
            public string Name { get; set; } = string.Empty;
            public ModbusClient ModbusClient { get; set; } = null!;
            public object ModbusLock { get; set; } = new object();
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public AxisModbusRegistry()
        {
            try { if (Application.Current != null) Application.Current.Exit += OnAppExit; } catch { }
        }

        /// <summary>
        /// Creates and registers a shared <see cref="ModbusClient"/> for the specified axis.
        /// If the axis is already registered the call is a no-op.
        /// </summary>
        public void Register(string name, string rs485Ip, byte unitId)
        {
            if (string.IsNullOrWhiteSpace(rs485Ip)) return;

            lock (_entries)
            {
                if (_entries.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return;

                try
                {
                    var mb = new ModbusClient(rs485Ip, 502) { UnitIdentifier = unitId };
                    _entries.Add(new Entry { Name = name, ModbusClient = mb });
                }
                catch { }
            }
        }

        /// <summary>
        /// Disconnects and removes the connection entry for the specified axis.
        /// </summary>
        public void Unregister(string name)
        {
            Entry? entry;
            lock (_entries)
            {
                entry = _entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry != null) _entries.Remove(entry);
            }

            if (entry != null)
                try { if (entry.ModbusClient?.Connected == true) entry.ModbusClient.Disconnect(); } catch { }
        }

        /// <summary>
        /// Gets the shared <see cref="ModbusClient"/> for the specified axis, or <c>null</c> if not registered.
        /// </summary>
        public ModbusClient? GetModbusClient(string name)
        {
            lock (_entries)
                return _entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.ModbusClient;
        }

        /// <summary>
        /// Gets the shared Modbus lock for the specified axis, or <c>null</c> if not registered.
        /// </summary>
        public object? GetModbusLock(string name)
        {
            lock (_entries)
                return _entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.ModbusLock;
        }

        private void OnAppExit(object? sender, ExitEventArgs e) => Dispose();

        public void Dispose()
        {
            try { if (Application.Current != null) Application.Current.Exit -= OnAppExit; } catch { }

            lock (_entries)
            {
                foreach (var e in _entries)
                    try { if (e.ModbusClient?.Connected == true) e.ModbusClient.Disconnect(); } catch { }

                _entries.Clear();
            }

            GC.SuppressFinalize(this);
        }
    }
}
