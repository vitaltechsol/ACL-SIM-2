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
    }
}
