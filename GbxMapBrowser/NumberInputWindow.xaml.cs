using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;

namespace GbxMapBrowser
{
    public partial class NumberInputWindow : Window
    {
        private readonly int _minValue;
        private readonly int? _maxValue;
        private bool _canSubmit;

        public int Value { get; private set; }

        public NumberInputWindow(string title, string prompt, int defaultValue, int minValue, int? maxValue = null)
        {
            InitializeComponent();

            Title = title;
            promptTextBlock.Text = prompt;
            numberTextBox.Text = defaultValue.ToString();
            _minValue = minValue;
            _maxValue = maxValue;
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            numberTextBox.Focus();
            numberTextBox.SelectAll();

            Dispatcher.BeginInvoke(
                new Action(() => _canSubmit = true),
                DispatcherPriority.ApplicationIdle
            );
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SaveValue();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void NumberTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveValue();
            }
        }

        private void SaveValue()
        {
            if (!_canSubmit)
            {
                return;
            }

            if (!int.TryParse(numberTextBox.Text.Trim(), out int value) ||
                value < _minValue ||
                (_maxValue.HasValue && value > _maxValue.Value))
            {
                string rangeMessage = _maxValue.HasValue
                    ? $"Enter a number from {_minValue} to {_maxValue.Value}."
                    : $"Enter a number greater than or equal to {_minValue}.";

                MessageBox.Show(
                    rangeMessage,
                    "Invalid number",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );

                return;
            }

            Value = value;
            DialogResult = true;
            Close();
        }
    }
}
