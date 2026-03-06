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
            // Create default settings once and reuse for each axis (could be loaded from persistent storage)
            var defaultSettings = new AxisSettings();

            Pitch = new AxisViewModel(new Axis("Pitch", defaultSettings));
            Roll = new AxisViewModel(new Axis("Roll", defaultSettings));
            Rudder = new AxisViewModel(new Axis("Rudder", defaultSettings));
            Tiller = new AxisViewModel(new Axis("Tiller", defaultSettings));

            // Populate with some initial demo values so UI appears active.
            Pitch.EncoderPosition = 0;
            Roll.EncoderPosition = 0.1;
            Rudder.EncoderPosition = -0.05;
            Tiller.EncoderPosition = 0.02;

            SetupCommand = new RelayCommand(OnSetup);
        }

        private void OnSetup(object? parameter)
        {
            var axisName = parameter?.ToString() ?? "";
            // TODO: Open setup dialog for the selected axis. For now, show a simple message box.
            MessageBox.Show($"Open setup for {axisName}", "Setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
