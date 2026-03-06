using System.Windows;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2.Views
{
    public partial class GlobalSettingsWindow : Window
    {
        public GlobalSettingsWindow(GlobalSettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.CloseAction = () => this.Close();
        }
    }
}
