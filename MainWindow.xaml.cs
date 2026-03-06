using System.Windows;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Set the DataContext to the main view model.
            DataContext = new MainViewModel();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }

        private void AxisEnable_Checked(object sender, RoutedEventArgs e)
        {
            SaveAxisEnabledFromSender(sender);
        }

        private void AxisEnable_Unchecked(object sender, RoutedEventArgs e)
        {
            SaveAxisEnabledFromSender(sender);
        }

        private void SaveAxisEnabledFromSender(object sender)
        {
            if (sender is FrameworkElement fe && fe.Tag is string axisName)
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    ViewModels.AxisViewModel? axisVm = axisName switch
                    {
                        "Pitch" => vm.Pitch,
                        "Roll" => vm.Roll,
                        "Rudder" => vm.Rudder,
                        "Tiller" => vm.Tiller,
                        _ => null
                    };

                    if (axisVm != null)
                    {
                        try
                        {
                            ACL_SIM_2.Services.SettingsService.SaveAxisSettings(axisName, axisVm.Underlying.Settings);
                        }
                        catch
                        {
                            // ignore save errors
                        }
                    }
                }
            }
        }
    }
}
