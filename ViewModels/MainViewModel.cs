using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public AxisViewModel Pitch { get; }
        public AxisViewModel Roll { get; }
        public AxisViewModel Rudder { get; }
        public AxisViewModel Tiller { get; }

        public ICommand SetupCommand { get; }

        public MainViewModel()
        {
            // Load per-axis persisted settings if available, otherwise use defaults.
            var pitchSettings = Services.SettingsService.LoadAxisSettings("Pitch") ?? new AxisSettings();
            var rollSettings = Services.SettingsService.LoadAxisSettings("Roll") ?? new AxisSettings();
            var rudderSettings = Services.SettingsService.LoadAxisSettings("Rudder") ?? new AxisSettings();
            var tillerSettings = Services.SettingsService.LoadAxisSettings("Tiller") ?? new AxisSettings();

            Pitch = new AxisViewModel(new Axis("Pitch", pitchSettings));
            Roll = new AxisViewModel(new Axis("Roll", rollSettings));
            Rudder = new AxisViewModel(new Axis("Rudder", rudderSettings));
            Tiller = new AxisViewModel(new Axis("Tiller", tillerSettings));

            // Populate with some initial demo values so UI appears active.
            Pitch.EncoderPosition = 0;
            Roll.EncoderPosition = 0.1;
            Rudder.EncoderPosition = -0.05;
            Tiller.EncoderPosition = 0.02;

            SetupCommand = new RelayCommand(OnSetup);
        }

        private void OnSetup(object? parameter)
        {
            var axisName = parameter?.ToString() ?? string.Empty;
            AxisViewModel? axisVm = axisName switch
            {
                "Pitch" => Pitch,
                "Roll" => Roll,
                "Rudder" => Rudder,
                "Tiller" => Tiller,
                _ => null
            };

            if (axisVm == null)
            {
                MessageBox.Show($"Unknown axis: {axisName}", "Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Open the axis setup window
            var vm = new AxisSetupViewModel(axisVm);
            var win = new Views.AxisSetupWindow(vm);
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
