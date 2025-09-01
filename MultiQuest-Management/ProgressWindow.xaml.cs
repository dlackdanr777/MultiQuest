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

        /// <summary>
        /// 진행 상태를 업데이트합니다.
        /// </summary>
        /// <param name="current">현재 진행 횟수</param>
        /// <param name="max">최대 진행 횟수</param>
        public void SetProgress(int current, int max)
        {
            if (max <= 0) return; // 최대값이 0 이하인 경우 방어 코드
            Dispatcher.Invoke(() =>
            {
                LoadingBar.Maximum = max;
                LoadingBar.Value = current;
                //StatusText.Text = $"기기 탐색 중... ({current}/{max})";
            });
        }
    }
}
