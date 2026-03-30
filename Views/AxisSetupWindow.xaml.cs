using System.Windows;
using ACL_SIM_2.ViewModels;

namespace ACL_SIM_2.Views
{
    public partial class AxisSetupWindow : Window
    {
        private readonly AxisSetupViewModel _viewModel;

        public AxisSetupWindow(AxisSetupViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            _viewModel = vm;
            vm.CloseAction = Close;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);

            // Clean up any active test functions and re-center the axis
            _ = _viewModel.CleanupAndCenterAsync();

            // Stop ProSim timer in viewmodel if running
            _viewModel.DisposeTimerIfNeeded();
        }
    }
}
