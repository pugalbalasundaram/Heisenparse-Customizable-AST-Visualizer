using System.Windows;
using HeisenParserWPF.Pages;

namespace HeisenParserWPF
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            MainFrame.Navigate(new LoginPage(MainFrame));
        }
    }
}
