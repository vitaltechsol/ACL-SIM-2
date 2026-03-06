using System.Windows;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2.Views
{
    public partial class AxisSetupWindow : Window
    {
        public AxisSetupWindow(AxisSetupViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.CloseAction = Close;
        }
    }
}
