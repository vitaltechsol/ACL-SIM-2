using System;
using System.Windows;
using EasyModbus;

namespace ACL_SIM_2.Services
{
    /// <summary>
    /// Controls motor torque via Modbus TCP (RS485 gateway) using EasyModbus.
    /// When constructed with enabled==false the class is inert and will not attempt connections.
    /// Accepts an external shared ModbusClient to avoid multiple connections per axis.
    /// </summary>
    public class AxisTorqueControl : IDisposable
    {
        private readonly bool _enabled;
        private readonly int _driverId;
        private readonly ModbusClient _mbc; // Shared external client managed by EncoderManager
        private readonly object _modbusLock; // Shared lock for thread-safe Modbus access

        /// <summary>
        /// Constructor that accepts a shared external ModbusClient managed by EncoderManager.
        /// </summary>
        public AxisTorqueControl(bool enabled, int driverId, ModbusClient sharedClient, object modbusLock)
        {
            _enabled = enabled;
            _driverId = driverId;
            _mbc = sharedClient ?? throw new ArgumentNullException(nameof(sharedClient));
            _modbusLock = modbusLock ?? throw new ArgumentNullException(nameof(modbusLock));

            if (_enabled)
            {
                try
                {
                    if (Application.Current != null)
                        Application.Current.Exit += OnAppExit;
                }
                catch { }
            }
        }

        // Connection is managed by EncoderManager, no need for EnsureConnected()

        /// <summary>
        /// Set forward torque register for the configured driver.
        /// </summary>
        public void SetTorqueRight(int fwrdVal)
        {
            if (!_enabled) return;

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(8, fwrdVal);
            }
        }

        /// <summary>
        /// Set backward torque register for the configured driver (written negated).
        /// </summary>
        public void SetTorqueLeft(int backWrd)
        {
            if (!_enabled) return;

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return;
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

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(8, fwrdVal);
                _mbc.WriteSingleRegister(9, backWrd * -1);
            }
        }

        /// <summary>
        /// Set self-centering speed register for the configured driver.
        /// </summary>
        public void SetCenteringSpeed(int speed)
        {
            if (!_enabled) return;

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                // _mbc.WriteSingleRegister(51, speed);
                _mbc.WriteSingleRegister(51, speed);
            }
        }

        /// <summary>
        /// Set dampening register for the configured driver.
        /// </summary>
        public void SetDampening(int dampening)
        {
            if (!_enabled) return;

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return;
                _mbc.UnitIdentifier = (byte)_driverId;
                _mbc.WriteSingleRegister(192, dampening);
            }
        }

        /// <summary>
        /// Reads the average torque from the motor driver (Dn002, register 370).
        /// Returns the signed value in the range -100..+100, or 0 if disabled or disconnected.
        /// </summary>
        public int GetExternalTorque()
        {
            if (!_enabled) return 0;

            lock (_modbusLock)
            {
                if (!_mbc.Connected) return 0;
                _mbc.UnitIdentifier = (byte)_driverId;
                int rawTorqueRaw = _mbc.ReadHoldingRegisters(370, 1)[0];
                return unchecked((short)rawTorqueRaw);
            }
        }

        /// <summary>
        /// Disconnect is not called - shared ModbusClient is managed by EncoderManager.
        /// </summary>
        public void Disconnect()
        {
            // External client managed by EncoderManager - do nothing
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
