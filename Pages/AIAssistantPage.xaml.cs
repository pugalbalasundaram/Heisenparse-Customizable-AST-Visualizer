using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace HeisenParserWPF.Pages
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _message;
        public string Sender { get; set; }
        public string Message
        {
            get => _message;
            set { if (_message != value) { _message = value; OnPropertyChanged(); } }
        }
        public DateTime Timestamp { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class OllamaResponse
    {
        public string model { get; set; }
        public DateTime created_at { get; set; }
        public string response { get; set; }
        public bool done { get; set; }
    }

    /// <summary>
    /// Represents a single, distinct conversation session.
    /// </summary>
    public class ChatSession : INotifyPropertyChanged
    {
        private string _title;
        public Guid Id { get; set; }
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }
        public ObservableCollection<ChatMessage> Messages { get; set; }

        public ChatSession()
        {
            Id = Guid.NewGuid();
            Messages = new ObservableCollection<ChatMessage>();
            Title = "New Chat";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class AIAssistantPage : Page, INotifyPropertyChanged
    {
        // Collection of all chat sessions for the history panel.
        public ObservableCollection<ChatSession> ChatHistory { get; set; }

        private ChatSession _currentChatSession;
        // The currently active chat session, bound to the UI.
        public ChatSession CurrentChatSession
        {
            get => _currentChatSession;
            set
            {
                if (_currentChatSession != value)
                {
                    _currentChatSession = value;
                    OnPropertyChanged(); // Notify the UI that the current chat has changed.
                }
            }
        }

        private readonly Frame _mainFrame;
        private readonly HttpClient _httpClient;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public AIAssistantPage(Frame mainFrame)
        {
            InitializeComponent();
            _mainFrame = mainFrame;
            this.DataContext = this;

            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            ChatHistory = new ObservableCollection<ChatSession>();

            StartNewChat();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mainFrame.CanGoBack) _mainFrame.GoBack();
        }

        private void ToggleHistoryPanel_Click(object sender, RoutedEventArgs e)
        {
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation { Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                animation.To = 0;
                storyboard.Completed += (s, _) => HistoryPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                HistoryPanel.Visibility = Visibility.Visible;
                animation.To = 250;
            }

            Storyboard.SetTarget(animation, HistoryPanel);
            Storyboard.SetTargetProperty(animation, new PropertyPath(Border.WidthProperty));
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            StartNewChat();
            if (HistoryPanel.Visibility == Visibility.Visible) ToggleHistoryPanel_Click(sender, e);
        }

        private void StartNewChat()
        {
            var newSession = new ChatSession();
            newSession.Messages.Add(new ChatMessage { Sender = "AI", Message = "Hello! How can I help you today?", Timestamp = DateTime.Now });
            ChatHistory.Insert(0, newSession); // Add new chats to the top of the list.
            CurrentChatSession = newSession;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendUserMessageAsync();

        // THIS IS THE METHOD THAT HANDLES THE 'ENTER' KEY
        private async void UserInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // If the key is 'Enter' AND the 'Shift' key is NOT held down...
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                // Mark the event as handled to stop it from adding a new line.
                e.Handled = true;
                // Send the message.
                await SendUserMessageAsync();
            }
        }

        private async Task SendUserMessageAsync()
        {
            string userMessageText = UserInputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(userMessageText) || CurrentChatSession == null) return;

            // Auto-generate a title for the session from the first user message.
            if (CurrentChatSession.Title == "New Chat")
            {
                CurrentChatSession.Title = userMessageText.Length > 40 ? userMessageText.Substring(0, 40) + "..." : userMessageText;
            }

            var userMessage = new ChatMessage { Sender = "User", Message = userMessageText, Timestamp = DateTime.Now };
            CurrentChatSession.Messages.Add(userMessage);
            UserInputTextBox.Clear();
            ScrollToBottom();

            SetLoadingState(true);

            try
            {
                var aiResponsePlaceholder = new ChatMessage { Sender = "AI", Message = "...", Timestamp = DateTime.Now };
                CurrentChatSession.Messages.Add(aiResponsePlaceholder);
                ScrollToBottom();
                await GetOllamaResponseAsync(userMessageText, aiResponsePlaceholder);
            }
            catch (Exception ex)
            {
                CurrentChatSession.Messages.Add(new ChatMessage { Sender = "AI", Message = $"Sorry, an error occurred: {ex.Message}", Timestamp = DateTime.Now });
            }
            finally
            {
                SetLoadingState(false);
                ScrollToBottom();
            }
        }

        private async Task GetOllamaResponseAsync(string prompt, ChatMessage aiMessageToUpdate)
        {
            var fullResponse = new StringBuilder();
            try
            {
                var requestUri = "http://localhost:11434/api/generate";
                var payload = new { model = "llama3", prompt, stream = true };
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var ollamaPart = JsonSerializer.Deserialize<OllamaResponse>(line);
                    if (ollamaPart?.response != null)
                    {
                        fullResponse.Append(ollamaPart.response);
                        aiMessageToUpdate.Message = fullResponse.ToString();
                        ScrollToBottom();
                    }
                }
            }
            catch (HttpRequestException httpEx)
            {
                aiMessageToUpdate.Message = $"Connection Error: Could not reach the Ollama server. Please ensure it is running. \n\nDetails: {httpEx.Message}";
            }
        }

        private void SetLoadingState(bool isLoading)
        {
            LoadingIndicator.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            UserInputTextBox.IsEnabled = !isLoading;
            SendButton.IsEnabled = !isLoading;
            if (!isLoading) UserInputTextBox.Focus();
        }

        private void ScrollToBottom()
        {
            Application.Current.Dispatcher.Invoke(() => ChatScrollViewer?.ScrollToBottom());
        }
    }
}