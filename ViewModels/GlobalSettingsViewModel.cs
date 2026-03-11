using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ACL_SIM_2.Models;
using ACL_SIM_2.Services;

namespace ACL_SIM_2.ViewModels
{
    public class GlobalSettingsViewModel : INotifyPropertyChanged
    {
        public GlobalSettingsViewModel()
        {
            // load existing or default
            var loaded = SettingsService.LoadGlobalSettings();
            Settings = loaded ?? new GlobalSettings();

            SaveCommand = new RelayCommand(_ => Save());
            CloseCommand = new RelayCommand(_ => CloseAction?.Invoke());
        }

        public GlobalSettings Settings { get; private set; }

        public string ProsimIp
        {
            get => Settings.ProsimIp;
            set
            {
                if (Settings.ProsimIp == value) return;
                Settings.ProsimIp = value;
                OnPropertyChanged(nameof(ProsimIp));
            }
        }

        public bool AutoConnectProsim
        {
            get => Settings.AutoConnectProsim;
            set
            {
                if (Settings.AutoConnectProsim == value) return;
                Settings.AutoConnectProsim = value;
                OnPropertyChanged(nameof(AutoConnectProsim));
            }
        }

        public bool AutoCenterOnStartup
        {
            get => Settings.AutoCenterOnStartup;
            set
            {
                if (Settings.AutoCenterOnStartup == value) return;
                Settings.AutoCenterOnStartup = value;
                OnPropertyChanged(nameof(AutoCenterOnStartup));
            }
        }

        public ICommand SaveCommand { get; }
        public ICommand CloseCommand { get; }

        public Action? CloseAction { get; set; }

        private void Save()
        {
            SettingsService.SaveGlobalSettings(Settings);
            MessageBox.Show("Global settings saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
