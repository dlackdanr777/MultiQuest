using System.Windows;

namespace MultiQuest_Management
{
    using MessageBox = System.Windows.MessageBox;

    public partial class AddDeviceWindow : Window
    {
        public string DeviceName { get; private set; }
        public string DeviceIp { get; private set; }
        public int DevicePort { get; private set; }

        private MainWindow _mainWindow;
        public AddDeviceWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            DeviceName = NameBox.Text.Trim();
            DeviceIp = IpBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(DeviceName) || string.IsNullOrWhiteSpace(DeviceIp))
            {
                MessageBox.Show("Name과 IP를 모두 입력하세요.");
                return;
            }

            _mainWindow.AddDevice(DeviceName, DeviceIp);
            this.Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.Focus();
            this.Close();
        }
    }
}