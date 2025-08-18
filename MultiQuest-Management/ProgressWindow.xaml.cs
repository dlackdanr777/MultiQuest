using System.Windows;


namespace MultiQuest_Management
{
    public partial class ProgressWindow : Window
    {
        private MainWindow _mainWindow;


        public ProgressWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
        }

        public void SetProgress(int current, int total)
        {
            // 반드시 Dispatcher에서 호출(스레드 충돌 방지)
            Dispatcher.Invoke(() =>
            {
                LoadingBar.Maximum = total;
                LoadingBar.Value = current;
                ProgressText.Text = $"{current} / {total}";
                StatusText.Text = $"Meta Quest 기기 탐색 중... ({current}/{total})";
            });
        }
    }
}
