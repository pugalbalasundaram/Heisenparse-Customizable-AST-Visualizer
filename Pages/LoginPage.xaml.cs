using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Supabase.Gotrue.Exceptions;
using HeisenParserWPF.Services;
using System.Windows.Input; // Added for KeyEventArgs and TraversalRequest

namespace HeisenParserWPF.Pages
{
    public partial class LoginPage : Page
    {
        private readonly Frame _mainFrame;

        public LoginPage(Frame mainFrame)
        {
            InitializeComponent();
            _mainFrame = mainFrame;
            _ = InitializeSupabaseAsync();
        }

        private async Task InitializeSupabaseAsync()
        {
            try
            {
                await SupabaseClientManager.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Supabase initialization failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// NEW: Handles the Enter key press for TextBox fields (Username and Email) to move focus to the next field.
        /// </summary>
        private void Input_KeyDown_Navigate(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Request focus navigation to the next element in the tab order
                var request = new TraversalRequest(FocusNavigationDirection.Next);
                (sender as UIElement)?.MoveFocus(request);
                e.Handled = true; // Prevents further processing of the Enter key
            }
        }

        /// <summary>
        /// NEW: Handles the Enter key press for the PasswordBox to trigger the login action.
        /// </summary>
        private void PasswordBox_KeyDown_Login(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Trigger the Login_Click method
                Login_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            // MODIFIED: Capture and validate the username from the UI
            string username = UsernameBox.Text.Trim();
            string email = EmailBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Please fill in all fields (Username, Email, and Password).",
                    "Missing Information", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var client = SupabaseClientManager.Client;
                var session = await client.Auth.SignIn(email: email, password: password);

                if (session?.User != null)
                {
                    // MODIFIED: Store the entered username for the session
                    UserSession.DisplayName = username;

                    MessageBox.Show($"Welcome, {username}!",
                        "Login Successful", MessageBoxButton.OK, MessageBoxImage.Information);

                    _mainFrame.Navigate(new HomePage(_mainFrame));
                }
                else
                {
                    MessageBox.Show("Invalid credentials. Please try again.",
                        "Login Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (GotrueException ex)
            {
                MessageBox.Show($"Login failed: {ex.Message}",
                    "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SignUp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // MODIFIED: Also capture username for the sign-up process consistency
            string username = UsernameBox.Text.Trim();
            string email = EmailBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Please fill in all fields to sign up.",
                    "Incomplete Information", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var client = SupabaseClientManager.Client;
                var result = await client.Auth.SignUp(email: email, password: password);

                if (result?.User != null)
                {
                    MessageBox.Show($"Sign-up successful, {username}! Please verify your email before logging in.",
                        "Registration Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Sign-up failed. This email may already be in use.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (GotrueException ex)
            {
                MessageBox.Show($"Sign-up failed: {ex.Message}",
                    "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// ADDED: A simple static class to hold the display name for the current session.
    /// </summary>
    public static class UserSession
    {
        public static string DisplayName { get; set; }
    }
}