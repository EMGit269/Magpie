using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Magpie.LangChain
{
    public static class MagpieWindow
    {
        private static Window _window;
        private static StackPanel _chatPanel;
        private static ScrollViewer _chatScroll;
        private static TextBox _txtInput;
        private static Button _btnSend;
        private static TextBlock _txtStatus;
        private static bool _isSending;

        public static void Show()
        {
            ChatWindow.EnsureHostBridgeRuntimeForExternalClients();

            if (_window != null)
            {
                _window.Show();
                _window.Activate();
                _ = RefreshServiceStatusAsync();
                return;
            }

            _window = BuildWindow();
            _window.Closed += (s, e) =>
            {
                _window = null;
                _chatPanel = null;
                _chatScroll = null;
                _txtInput = null;
                _btnSend = null;
                _txtStatus = null;
                ChatWindow.StopHostBridgeRuntimeForExternalClients();
            };

            _window.Show();
            AppendSystemMessage("Magpie is ready. It connects to the external agent_service.");
            _ = RefreshServiceStatusAsync();
        }

        private static Window BuildWindow()
        {
            var window = new Window
            {
                Title = MagpieSettings.WindowTitle,
                Width = 410,
                Height = 760,
                MinWidth = 410,
                MinHeight = 520,
                Background = Brush("#F5F7FA"),
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#D6DAE1"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 14, 16, 14)
            };
            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = MagpieSettings.WindowTitle,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#1C2026")
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = MagpieSettings.WindowSubtitle,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brush("#5C626E")
            });
            header.Child = headerStack;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _chatPanel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
            _chatScroll = new ScrollViewer
            {
                Content = _chatPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent
            };
            Grid.SetRow(_chatScroll, 1);
            root.Children.Add(_chatScroll);

            var inputWrap = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#D6DAE1"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(14, 0, 14, 10),
                Padding = new Thickness(12, 10, 12, 10)
            };

            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtInput = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Foreground = Brush("#1C2026"),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 44,
                MaxHeight = 128,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _txtInput.KeyDown += TxtInput_KeyDown;
            Grid.SetColumn(_txtInput, 0);
            inputGrid.Children.Add(_txtInput);

            _btnSend = new Button
            {
                Content = "Send",
                Width = 56,
                Height = 28,
                Margin = new Thickness(10, 0, 0, 0),
                Background = Brush("#E8ECF3"),
                BorderBrush = Brush("#D6DAE1"),
                Foreground = Brush("#222831"),
                Cursor = Cursors.Hand
            };
            _btnSend.Click += async (s, e) => await SendAsync();
            Grid.SetColumn(_btnSend, 1);
            inputGrid.Children.Add(_btnSend);

            inputWrap.Child = inputGrid;
            Grid.SetRow(inputWrap, 2);
            root.Children.Add(inputWrap);

            var statusWrap = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#D6DAE1"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 8, 14, 8)
            };
            _txtStatus = new TextBlock
            {
                Text = "Checking agent_service...",
                FontSize = 11,
                Foreground = Brush("#5C626E")
            };
            statusWrap.Child = _txtStatus;
            Grid.SetRow(statusWrap, 3);
            root.Children.Add(statusWrap);

            window.Content = root;
            return window;
        }

        private static async void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await SendAsync();
            }
        }

        private static async Task SendAsync()
        {
            if (_isSending || _txtInput == null) return;

            string text = (_txtInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            _txtInput.Clear();
            AppendUserMessage(text);
            SetSendingState(true);

            try
            {
                ChatWindow.EnsureHostBridgeRuntimeForExternalClients();
                string output = await MagpieServiceClient.SendAsync(
                    MagpieSettings.SessionId,
                    text,
                    MagpieSettings.DefaultUserGoal).ConfigureAwait(false);
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    AppendAssistantMessage(output);
                }));
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    AppendErrorMessage("agent_service connection failed: " + ex.Message);
                    SetStatus("agent_service unavailable.");
                }));
            }
            finally
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => SetSendingState(false)));
            }
        }

        private static async Task RefreshServiceStatusAsync()
        {
            try
            {
                string status = await MagpieServiceClient.GetHealthStatusTextAsync().ConfigureAwait(false);
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => SetStatus(status)));
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    SetStatus("agent_service unavailable: " + ex.Message);
                }));
            }
        }

        private static void SetSendingState(bool sending)
        {
            _isSending = sending;
            if (_btnSend != null)
            {
                _btnSend.IsEnabled = !sending;
                _btnSend.Content = sending ? "..." : "Send";
            }
        }

        private static void SetStatus(string text)
        {
            if (_txtStatus != null)
                _txtStatus.Text = text ?? "";
        }

        private static void AppendUserMessage(string text)
        {
            AppendBubble(text, true, Brush("#1C2026"), Brush("#E8ECF3"), Brush("#D6DAE1"));
        }

        private static void AppendAssistantMessage(string text)
        {
            AppendBubble(text, false, Brush("#1C2026"), Brush("#FFFFFF"), Brush("#D6DAE1"));
        }

        private static void AppendSystemMessage(string text)
        {
            AppendBubble(text, false, Brush("#5C626E"), Brush("#F4F6F9"), Brush("#D6DAE1"));
        }

        private static void AppendErrorMessage(string text)
        {
            AppendBubble(text, false, Brush("#8A1F1F"), Brush("#FFF1F1"), Brush("#F0C7C7"));
        }

        private static void AppendBubble(string text, bool alignRight, Brush foreground, Brush background, Brush borderBrush)
        {
            if (_chatPanel == null) return;

            var border = new Border
            {
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                MaxWidth = 320,
                HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            border.Child = new TextBlock
            {
                Text = text ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = foreground
            };
            _chatPanel.Children.Add(border);
            _chatScroll?.ScrollToEnd();
        }

        private static SolidColorBrush Brush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }
    }
}
