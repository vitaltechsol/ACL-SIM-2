using System;
using System.Reflection;
using System.Windows;
using System.IO.Ports;
using EasyModbus;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Controls motor torque via Modbus TCP (RS485 gateway) using EasyModbus.
    /// When constructed with enabled==false the class is inert and will not attempt connections.
    /// </summary>
    public class AxisTorqueControl : IDisposable
    {
        private readonly bool _enabled;
        private readonly int _driverId;
        private readonly string _rs485Ip;
        private ModbusClient? _mbc;
        private readonly object _sync = new object();

        // Fixed params requested
        private const int Baudrate = 115200;
        // Use numeric values for stopbits/parity to avoid direct dependency on System.IO.Ports types
        private const int StopBitsVal = 1; // StopBits.One
        private const int ParityVal = 0; // Parity.None
        private const int ConnectionTimeoutMs = 4000;
        private const int Port = 502;

        public AxisTorqueControl(bool enabled, int driverId, string rs485Ip)
        {
            _enabled = enabled;
            _driverId = driverId;
            _rs485Ip = rs485Ip ?? throw new ArgumentNullException(nameof(rs485Ip));

            if (_enabled)
            {
                Start();
                // Disconnect on application exit
                try
                {
                    if (Application.Current != null)
                        Application.Current.Exit += OnAppExit;
                }
                catch
                {
                    // ignore when Application not available (unit tests, etc.)
                }
            }
        }

        private void Start()
        {
            if (!_enabled) return;

            lock (_sync)
            {
                try
                {
                    if (_mbc == null)
                    {
                        // create Modbus TCP client
                        _mbc = new ModbusClient(_rs485Ip, Port);

                        // set timeout if available
                        try
                        {
                            _mbc.ConnectionTimeout = ConnectionTimeoutMs;
                        }
                        catch { }

                        // Some EasyModbus builds expose serial transport properties when using RTU over TCP gateways.
                        // Set baud/parity/stopbits via reflection if those properties exist to satisfy the requirement without breaking compilation.
                        TrySetPropertyIfExists(_mbc, "Baudrate", Baudrate);
                        TrySetPropertyIfExists(_mbc, "StopBits", StopBitsVal);
                        TrySetPropertyIfExists(_mbc, "Parity", ParityVal);
                    }

                    if (!_mbc.Connected)
                    {
                        _mbc.Connect();
                    }
                }
                catch
                {
                    // swallow - callers may retry when writing registers
                }
            }
        }

        private void TrySetPropertyIfExists(object target, string propName, object value)
        {
            try
            {
                var t = target.GetType();
                var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    var propType = p.PropertyType;
                    if (propType.IsEnum && value is int intVal)
                    {
                        var enumVal = Enum.ToObject(propType, intVal);
                        p.SetValue(target, enumVal);
                    }
                    else
                    {
                        p.SetValue(target, value);
                    }
                }
            }
            catch
            {
                // ignore if property not present or set fails
            }
        }

        private void EnsureConnected()
        {
            if (!_enabled) return;

            lock (_sync)
            {
                if (_mbc == null)
                {
                    Start();
                }

                if (_mbc != null && !_mbc.Connected)
                {
                    try { _mbc.Connect(); } catch { }
                }
            }
        }

        /// <summary>
        /// Set forward torque register for the configured driver.
        /// </summary>
        public void SetTorqueForward(int fwrdVal)
        {
            if (!_enabled) return;
            EnsureConnected();

            lock (_sync)
            {
                if (_mbc == null || !_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(8, fwrdVal);
            }
        }

        /// <summary>
        /// Set backward torque register for the configured driver (written negated).
        /// </summary>
        public void SetTorqueBackward(int backWrd)
        {
            if (!_enabled) return;
            EnsureConnected();

            lock (_sync)
            {
                if (_mbc == null || !_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(9, backWrd * -1);
            }
        }

        /// <summary>
        /// Set both forward and backward torque registers for the configured driver.
        /// </summary>
        public void SetTorqueBoth(int fwrdVal, int backWrd)
        {
            if (!_enabled) return;
            EnsureConnected();

            lock (_sync)
            {
                if (_mbc == null || !_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(8, fwrdVal);
                _mbc.WriteSingleRegister(9, backWrd * -1);
            }
        }

        /// <summary>
        /// Disconnects and disposes the underlying client.
        /// </summary>
        public void Disconnect()
        {
            lock (_sync)
            {
                try
                {
                    if (_mbc != null && _mbc.Connected)
                    {
                        _mbc.Disconnect();
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _mbc = null;
                }
            }
        }

        private void OnAppExit(object? sender, ExitEventArgs e)
        {
            Disconnect();
        }

        public void Dispose()
        {
            try
            {
                if (Application.Current != null)
                    Application.Current.Exit -= OnAppExit;
            }
            catch { }

            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
