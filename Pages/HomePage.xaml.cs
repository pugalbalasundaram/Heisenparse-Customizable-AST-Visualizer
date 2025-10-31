using Heisenparse2._0.Models;
using HeisenParserWPF.Models;
using HeisenParserWPF.Services;
using Microsoft.Win32;
using Supabase.Gotrue;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HeisenParserWPF.Pages
{
    public partial class HomePage : Page
    {
        private readonly Frame _mainFrame;
        private readonly User _currentUser;

        public HomePage(Frame mainFrame)
        {
            InitializeComponent();
            _mainFrame = mainFrame;
            _currentUser = SupabaseClientManager.Client?.Auth?.CurrentUser;
            Loaded += HomePage_Loaded;
        }

        // ... (HomePage_Loaded and LoadRecentActivities remain the same) ...

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // MODIFIED: Display user info, prioritizing the name from the UserSession
                if (_currentUser != null && !string.IsNullOrEmpty(UserSession.DisplayName))
                {
                    // Use the name provided during login
                    WelcomeText.Text = $"Welcome, {UserSession.DisplayName}";
                    ProfileName.Text = UserSession.DisplayName;
                }
                else if (_currentUser != null)
                {
                    // Fallback to email-derived name if session name is not available
                    var displayName = !string.IsNullOrWhiteSpace(_currentUser.Email) ? _currentUser.Email.Split('@')[0] : "User";
                    WelcomeText.Text = $"Welcome, {displayName}";
                    ProfileName.Text = displayName;
                }
                else
                {
                    // Handle guest users
                    WelcomeText.Text = "Welcome, Guest";
                    ProfileName.Text = "Guest";
                }

                LoadRecentActivities();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading user data:\n{ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadRecentActivities()
        {
            if (_currentUser != null)
            {
                var activities = RecentActivityService.GetRecentActivities(_currentUser.Id);
                RecentActivitiesListBox.ItemsSource = activities.OrderByDescending(a => a.LastModified).ToList();
            }
            else
            {
                RecentActivitiesListBox.ItemsSource = null;
            }
        }

        // --- NEW PROFILE FUNCTIONALITY ---

        /// <summary>
        /// Shows the context menu when the profile button is clicked.
        /// </summary>
        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button profileButton)
            {
                profileButton.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Handles navigation to an Account Settings page (currently a placeholder).
        /// </summary>
        private void AccountSettings_Click(object sender, RoutedEventArgs e)
        {
            // You would typically navigate to an Account Settings page here.
            // Example: _mainFrame.Navigate(new AccountSettingsPage(_mainFrame));

            MessageBox.Show("Account Settings functionality is coming soon!", "Placeholder", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // --- EXISTING NAVIGATION AND LOGOUT ---

        private void RecentActivity_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (RecentActivitiesListBox.SelectedItem is RecentActivity selectedActivity)
            {
                _mainFrame.Navigate(new EditExistingCodePage(_mainFrame, selectedActivity.FilePath));
                RecentActivitiesListBox.SelectedItem = null;
            }
        }

        private void NewCode_Click(object sender, RoutedEventArgs e)
        {
            _mainFrame.Navigate(new NewCodePage(_mainFrame));
        }

        private void AIAssistant_Click(object sender, RoutedEventArgs e)
        {
            _mainFrame.Navigate(new AIAssistantPage(_mainFrame));
        }

        private void UploadCode_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "C# Files (*.cs)|*.cs|All Files (*.*)|*.*",
                Title = "Select a code file to edit"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _mainFrame.Navigate(new EditExistingCodePage(_mainFrame, openFileDialog.FileName));
            }
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // MODIFIED: Clear the display name on logout
                UserSession.DisplayName = null;
                await SupabaseClientManager.Client?.Auth?.SignOut();
                MessageBox.Show("You have been successfully logged out.", "Logout Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                _mainFrame.Navigate(new LoginPage(_mainFrame));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Logout failed:\n{ex.Message}", "Logout Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}









































































































































































































