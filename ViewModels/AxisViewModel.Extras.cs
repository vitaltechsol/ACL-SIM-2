using System.ComponentModel;
using ACL_SIM_2.Services;

namespace ACL_SIM_2.ViewModels
{
    public partial class AxisViewModel
    {
        private bool _enabled = true;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                try
                {
                    _axis.Settings.Enabled = value;
                    SettingsService.SaveAxisSettings(_axis.Name, _axis.Settings);
                }
                catch { }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }
    }
}
